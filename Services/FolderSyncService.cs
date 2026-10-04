using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class FolderSyncService
    {
        private readonly FolderSyncStore _store;
        private readonly Func<FolderSyncBinding, IFolderSyncTarget> _targetFactory;
        private readonly List<FolderSyncJob> _jobs = new();
        private readonly SemaphoreSlim _changes = new(1, 1);
        private Task _initialization;
        public IReadOnlyList<FolderSyncJob> Jobs => _jobs.ToList();
        public string ErrorKey { get; private set; }
        public event Action Changed;

        public FolderSyncService() : this(new FolderSyncStore(new ApplicationDataPaths().FolderSync),
            OneDrive.CreateFolderSyncTarget) { }

        public FolderSyncService(FolderSyncStore store, Func<FolderSyncBinding, IFolderSyncTarget> factory)
        {
            _store = store;
            _targetFactory = factory;
        }

        public Task InitializeAsync() => _initialization ??= LoadAsync();

        public async Task RetryFailedAsync(bool restoreBackups = false)
        {
            await InitializeAsync();
            await _changes.WaitAsync();
            try
            {
                if (restoreBackups) await _store.RestoreUnreadableBackupsAsync(_jobs.Select(j => j.Binding.Id).ToList());
                await LoadAsync();
            }
            finally { _changes.Release(); }
        }

        public IFolderSyncBrowser CreateBrowser(FolderSyncBinding binding)
        {
            var target = _targetFactory(binding);
            if (target is IFolderSyncBrowser browser) return browser;
            target.Dispose();
            throw new NotSupportedException();
        }

        private async Task LoadAsync()
        {
            try
            {
                var loaded = await _store.LoadAsync(CancellationToken.None);
                ErrorKey = _store.LegacyCleanupPending ? "Configuration_LegacyCleanup" : loaded.Errors > 0 ? "Sync_ConfigError" : null;
                foreach (var binding in loaded.Bindings)
                {
                    if (_jobs.Any(j => j.Binding.Id == binding.Id)) continue;
                    try { CheckOverlap(binding); }
                    catch { ErrorKey = "Sync_ConfigError"; continue; }
                    AddJob(binding);
                }
            }
            catch { ErrorKey = "Sync_ConfigError"; }
            Changed?.Invoke();
        }

        private void CheckOverlap(FolderSyncBinding binding)
        {
            if (FolderSyncRules.Overlaps(binding.LocalPath, _store.DirectoryPath))
                throw new FolderSyncException("Sync_ConfigInsideRoot");
            foreach (var job in _jobs)
            {
                var other = job.Binding;
                if (FolderSyncRules.Overlaps(binding.LocalPath, other.LocalPath) ||
                    (binding.AccountId == other.AccountId && binding.DriveId == other.DriveId && (binding.RemoteFolderId == other.RemoteFolderId ||
                        binding.RemoteAncestorIds.Contains(other.RemoteFolderId) || other.RemoteAncestorIds.Contains(binding.RemoteFolderId))))
                    throw new FolderSyncException("Sync_Overlap");
            }
        }

        private void AddJob(FolderSyncBinding binding)
        {
            var job = new FolderSyncJob(binding, _store, _targetFactory);
            _jobs.Add(job);
            job.Start();
        }

        public async Task AddAsync(FolderSyncBinding binding)
        {
            await InitializeAsync();
            await _changes.WaitAsync();
            try
            {
                binding.LocalPath = FolderSyncRules.NormalizeRoot(binding.LocalPath);
                FolderSyncRules.ValidateName(binding.FolderName);
                FolderSyncRules.CheckLocalRoot(binding);
                CheckOverlap(binding);
                using var target = _targetFactory(binding);
                await target.ValidateRootAsync(CancellationToken.None);
                await _store.SaveAsync(binding, CancellationToken.None);
                AddJob(binding);
                Changed?.Invoke();
            }
            finally { _changes.Release(); }
        }

        public async Task RemoveAsync(FolderSyncJob job)
        {
            await _changes.WaitAsync();
            try
            {
                await job.SetEnabledAsync(false);
                await _store.RemoveAsync(job.Binding);
                await job.StopAsync();
                _jobs.Remove(job);
                Changed?.Invoke();
            }
            finally { _changes.Release(); }
        }

        public void Stop()
        {
            foreach (var job in _jobs) job.RequestStop();
        }
    }

    public sealed class FolderSyncJob
    {
        private readonly FolderSyncStore _store;
        private readonly Func<FolderSyncBinding, IFolderSyncTarget> _targetFactory;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _attemptLock = new();
        private CancellationTokenSource _attempt;
        private bool _pauseRequested;
        private FileSystemWatcher _watcher;
        private Task _worker;
        public FolderSyncBinding Binding { get; }
        public FolderSyncProgress Progress { get; private set; } = new("Sync_Waiting", 0, 0, "");
        public IReadOnlyList<FolderSyncIssue> Issues { get; private set; } = Array.Empty<FolderSyncIssue>();
        public event Action Updated;

        public FolderSyncJob(FolderSyncBinding binding, FolderSyncStore store, Func<FolderSyncBinding, IFolderSyncTarget> factory)
        {
            Binding = binding;
            _store = store;
            _targetFactory = factory;
        }

        public void Start() => _worker ??= Task.Run(LoopAsync);

        public void RequestScan()
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }

        public async Task SetEnabledAsync(bool enabled)
        {
            if (!enabled) lock (_attemptLock) { _pauseRequested = true; _attempt?.Cancel(); }
            await _gate.WaitAsync();
            try
            {
                bool previous = Binding.Enabled;
                Binding.Enabled = enabled;
                try { await _store.SaveAsync(Binding, CancellationToken.None); }
                catch { Binding.Enabled = previous; lock (_attemptLock) _pauseRequested = false; throw; }
                lock (_attemptLock) _pauseRequested = !enabled;
                if (!enabled) StopWatcher();
                Progress = Progress with { StateKey = enabled ? "Sync_Waiting" : "Sync_Paused", CurrentPath = "" };
                Updated?.Invoke();
            }
            finally { _gate.Release(); }
            RequestScan();
        }

        private void EnsureWatcher()
        {
            if (_watcher != null) return;
            _watcher = new FileSystemWatcher(Binding.LocalPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024
            };
            _watcher.Created += OnChange;
            _watcher.Changed += OnChange;
            _watcher.Deleted += OnChange;
            _watcher.Renamed += OnChange;
            _watcher.Error += (_, _) => { StopWatcher(); RequestScan(); };
            _watcher.EnableRaisingEvents = true;
        }

        private void OnChange(object sender, FileSystemEventArgs args) => RequestScan();

        private void StopWatcher() => Interlocked.Exchange(ref _watcher, null)?.Dispose();

        private async Task LoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    await _gate.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (Binding.Enabled)
                        {
                            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                            lock (_attemptLock)
                            {
                                _attempt = attempt;
                                if (_pauseRequested) attempt.Cancel();
                            }
                            try
                            {
                                attempt.Token.ThrowIfCancellationRequested();
                                FolderSyncRules.CheckLocalRoot(Binding);
                                EnsureWatcher(); // Install before scanning, so changes during first upload are retained.
                                using var target = _targetFactory(Binding);
                                var progress = new InlineProgress<FolderSyncProgress>(value => { Progress = value; Updated?.Invoke(); });
                                DateTime lastCheckpoint = DateTime.MinValue;
                                var result = await new FolderSyncEngine().RunAsync(Binding, target,
                                    async token =>
                                    {
                                        // Coalesce manifest writes for large trees. A crash can at most repeat a
                                        // confirmed upload; it cannot skip unconfirmed content.
                                        if (DateTime.UtcNow - lastCheckpoint < TimeSpan.FromSeconds(2)) return;
                                        await _store.SaveAsync(Binding, token);
                                        lastCheckpoint = DateTime.UtcNow;
                                    }, progress, attempt.Token);
                                await _store.SaveAsync(Binding, attempt.Token);
                                Issues = result.Issues;
                            }
                            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                            catch (Exception exception)
                            {
                                string key = GetErrorKey(exception);
                                Issues = new[] { new FolderSyncIssue("", key) };
                                Progress = new("Sync_Attention", 0, 0, "");
                                if (key is "Sync_RootMissing" or "Sync_RemoteMissing" or "Sync_RemoteMoved" or "Sync_NameMismatch" or "Sync_LoginNeeded")
                                {
                                    Binding.Enabled = false;
                                    StopWatcher();
                                    try { await _store.SaveAsync(Binding, _lifetime.Token); }
                                    catch { Issues = new[] { new FolderSyncIssue("", "Sync_ConfigError") }; }
                                }
                            }
                            finally { lock (_attemptLock) _attempt = null; }
                        }
                        else Progress = Progress with { StateKey = "Sync_Paused" };
                        Updated?.Invoke();
                    }
                    finally { _gate.Release(); }
                    // Coalesce noisy writes; regular rescans also repair missing watcher notifications.
                    await _signal.WaitAsync(TimeSpan.FromMinutes(Issues.Count == 0 ? 5 : 1), _lifetime.Token);
                    await Task.Delay(1000, _lifetime.Token);
                    while (_signal.Wait(0)) { }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { StopWatcher(); }
        }

        public static string GetErrorKey(Exception exception) => exception switch
        {
            Microsoft.Kiota.Abstractions.ApiException api => api.ResponseStatusCode switch
            {
                401 => "Sync_LoginNeeded", 403 => "Sync_AccessDenied", 404 => "Sync_RemoteMissing",
                409 or 412 => "Sync_RemoteChanged", 507 => "Sync_Quota", _ => "Sync_NetworkError"
            },
            AccountAuthenticationException auth when auth.Failure is AuthenticationFailure.RequiresSignIn or AuthenticationFailure.AccountMismatch => "Sync_LoginNeeded",
            AccountAuthenticationException => "Sync_NetworkError",
            _ => FolderSyncEngine.ErrorKey(exception)
        };

        public void RequestStop()
        {
            _lifetime.Cancel();
            StopWatcher();
        }

        public async Task StopAsync()
        {
            RequestStop();
            if (_worker != null) await _worker;
        }
    }
}
