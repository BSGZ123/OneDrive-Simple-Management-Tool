using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed record AccountScope([property: JsonRequired] string AccountId, [property: JsonRequired] string DriveId = null)
    {
        public bool Contains(string accountId, string driveId) => AccountId == accountId && (DriveId == null || DriveId == driveId);
    }

    public sealed record AccountCleanup([property: JsonRequired] AccountScope Scope, [property: JsonRequired] bool RemoveBookmarks,
        [property: JsonRequired] bool Pending = true, [property: JsonRequired] bool ClearCredentials = false);
    public sealed class AccountAccessException : InvalidOperationException { }

    [JsonSerializable(typeof(List<AccountCleanup>))]
    internal partial class AccountAccessJsonContext : JsonSerializerContext { }

    // A durable deny list also acts as a cleanup journal. Never silently discard a broken journal.
    public sealed class AccountAccessState
    {
        private readonly ProtectedConfigurationFile<List<AccountCleanup>> _file;
        private readonly SemaphoreSlim _writes = new(1, 1);
        private readonly object _gate = new();
        private List<AccountCleanup> _records = new();
        private readonly HashSet<AccessLease> _leases = new();
        private readonly Dictionary<string, long> _accountChanges = new(StringComparer.Ordinal);
        private long _revision, _generation;
        private bool _initialized;
        public event Action<AccountScope> Changed;

        public AccountAccessState(ApplicationDataPaths paths, IConfigurationProtector protector = null)
        {
            _file = new(System.IO.Path.Combine(paths.Root, "Configuration", "account-access.dat"), "account-access",
                AccountAccessJsonContext.Default.ListAccountCleanup, Validate, 4 * 1024 * 1024, protector);
        }

        public async Task InitializeAsync()
        {
            await _writes.WaitAsync();
            try
            {
                using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, default);
                var snapshot = await _file.ReadAsync(default);
                lock (_gate)
                {
                    _records = snapshot?.Data ?? new();
                    _revision = snapshot?.Revision ?? 0;
                    _initialized = true;
                }
            }
            finally { _writes.Release(); }
        }

        // Startup repair only: lets the user inspect the previous revision before choosing a recovery.
        public async Task<IReadOnlyList<AccountCleanup>> ReadBackupAsync()
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, default);
            return (await _file.ReadAsync(default, true)).Data;
        }

        // Replaces an unreadable journal after explicit confirmation. Drive removals from the backup are kept, and
        // every known account gets a pending sign-out, so access returns only through a new interactive sign-in.
        public async Task RepairAsync(IEnumerable<string> accounts, bool useBackup)
        {
            await _writes.WaitAsync();
            try
            {
                using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, default);
                bool broken = false;
                try { await _file.ReadAsync(default); }
                catch (ConfigurationException) { broken = true; }
                // Another process has already repaired it. Do not overwrite its work from a stale error screen.
                if (!broken) throw new ConfigurationException(ConfigurationFailure.Conflict);
                var backup = useBackup ? await _file.ReadAsync(default, true) : null;
                var kept = backup?.Data.Where(x => x.Scope.DriveId != null).ToList() ?? new();
                var signOut = accounts.Concat(backup?.Data.Select(x => x.Scope.AccountId) ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
                var next = kept.Concat(signOut.Select(x => new AccountCleanup(new AccountScope(x), false, ClearCredentials: true))).ToList();
                Validate(next);
                // Keep encrypted copies of the damaged journal and of a backup that is not used.
                await _file.ArchiveAsync(_file.Path, default);
                if (!useBackup) await _file.ArchiveAsync(_file.BackupPath, default);
                var saved = await _file.WriteAsync(next, checked((backup?.Revision ?? 0) + 1), false, default);
                lock (_gate)
                {
                    _records = next;
                    _revision = saved.Revision;
                    foreach (var account in signOut) _accountChanges[account] = ++_generation;
                    _initialized = true;
                }
            }
            finally { _writes.Release(); }
        }

        public long Generation { get { lock (_gate) return _generation; } }
        public IReadOnlyList<AccountCleanup> Pending { get { lock (_gate) return _records.Where(x => x.Pending).ToArray(); } }
        public bool IsRemoved(string account, string drive)
        {
            lock (_gate) return _records.Any(x => x.Scope == new AccountScope(account, drive) && drive != null);
        }
        public bool IsBlocked(string account, string drive = null)
        {
            lock (_gate) return !_initialized || _records.Any(x => x.Scope.Contains(account, drive) || x.ClearCredentials && x.Scope.AccountId == account);
        }

        public void Check(string account, string drive = null)
        {
            if (IsBlocked(account, drive)) throw new AccountAccessException();
        }

        public void CheckCanAdd(string account)
        {
            lock (_gate)
                if (!_initialized || _records.Any(x => x.Scope.AccountId == account && (x.Pending || x.ClearCredentials || x.Scope.DriveId == null)))
                    throw new AccountAccessException();
        }

        public void CheckGeneration(string account, long generation)
        {
            lock (_gate) if (_accountChanges.GetValueOrDefault(account) > generation) throw new AccountAccessException();
        }

        public AccessLease Enter(string account, string drive, CancellationToken token = default)
        {
            lock (_gate)
            {
                Check(account, drive);
                var lease = new AccessLease(this, account, drive, token);
                _leases.Add(lease);
                return lease;
            }
        }

        public AccessLease EnterInteractive(string account, CancellationToken token)
        {
            if (account == null) return null; // The picker has not selected an account yet; generation validation handles its late result.
            lock (_gate)
            {
                if (!_initialized || _records.Any(x => x.Scope.AccountId == account && x.Pending)) throw new AccountAccessException();
                var lease = new AccessLease(this, account, null, token, interactive: true);
                _leases.Add(lease);
                return lease;
            }
        }

        public async Task<AccountCleanup> BeginAsync(AccountScope scope, bool removeBookmarks, bool clearCredentials = false)
        {
            await _writes.WaitAsync();
            try
            {
                List<AccountCleanup> next;
                AccountCleanup operation;
                lock (_gate)
                {
                    if (!_initialized) throw new AccountAccessException();
                    var existing = _records.FirstOrDefault(x => x.Scope == scope && x.Pending);
                    if (existing != null) return existing;
                    if (_records.Any(x => x.Pending && x.Scope.AccountId == scope.AccountId)) throw new AccountAccessException();
                    operation = new(scope, removeBookmarks, ClearCredentials: clearCredentials || scope.DriveId == null);
                    next = _records.Where(x => x.Scope != scope).Append(operation).ToList();
                }
                // Commit denial before any destructive configuration change or credential removal.
                await SaveAsync(next);
                AccessLease[] active;
                lock (_gate)
                {
                    _accountChanges[scope.AccountId] = ++_generation;
                    var cancelScope = operation.ClearCredentials ? new AccountScope(scope.AccountId) : scope;
                    active = _leases.Where(x => cancelScope.Contains(x.AccountId, x.DriveId) || x.Interactive && x.AccountId == scope.AccountId).ToArray();
                }
                foreach (var lease in active) lease.Cancel();
                Changed?.Invoke(scope);
                return operation;
            }
            finally { _writes.Release(); }
        }

        public async Task DrainAsync(AccountScope scope)
        {
            Task[] tasks;
            lock (_gate) tasks = _leases.Where(x => scope.Contains(x.AccountId, x.DriveId) || x.Interactive && x.AccountId == scope.AccountId).Select(x => x.Completion).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task CompleteAsync(AccountCleanup operation)
        {
            await _writes.WaitAsync();
            try
            {
                await SaveAsync(_records.Select(x => x == operation ? x with { Pending = false } : x).ToList());
                Changed?.Invoke(operation.Scope);
            }
            finally { _writes.Release(); }
        }

        // Interactive sign-in is the only path that clears an account denial. A result started
        // before an exit/removal cannot revive the account, including when the picker chose it late.
        public async Task ResumeAccountAsync(string account, long startedGeneration)
        {
            await _writes.WaitAsync();
            try
            {
                lock (_gate)
                {
                    if (!_initialized || _accountChanges.GetValueOrDefault(account) > startedGeneration ||
                        _records.Any(x => x.Scope.AccountId == account && x.Pending)) throw new AccountAccessException();
                }
                var next = _records.Where(x => x.Scope != new AccountScope(account))
                    .Select(x => x.Scope.AccountId == account ? x with { ClearCredentials = false } : x).ToList();
                if (!next.SequenceEqual(_records)) await SaveAsync(next);
                Changed?.Invoke(new AccountScope(account));
            }
            finally { _writes.Release(); }
        }

        public async Task RestoreDriveAsync(string account, string drive)
        {
            await _writes.WaitAsync();
            try
            {
                CheckCanAdd(account);
                var next = _records.Where(x => x.Scope != new AccountScope(account, drive)).ToList();
                if (next.Count != _records.Count) await SaveAsync(next);
                Changed?.Invoke(new AccountScope(account, drive));
            }
            finally { _writes.Release(); }
        }

        private async Task SaveAsync(List<AccountCleanup> records)
        {
            Validate(records);
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, default);
            var current = await _file.ReadAsync(default);
            if ((current?.Revision ?? 0) != _revision) throw new ConfigurationException(ConfigurationFailure.Conflict);
            var saved = await _file.WriteAsync(records, checked(_revision + 1), _revision > 0, default);
            lock (_gate) { _revision = saved.Revision; _records = records; }
        }

        private static void Validate(List<AccountCleanup> records)
        {
            if (records == null || records.Count > 10000 || records.Any(x => x?.Scope == null ||
                string.IsNullOrWhiteSpace(x.Scope.AccountId) || x.Scope.AccountId.Length > 2048 ||
                x.Scope.DriveId != null && (string.IsNullOrWhiteSpace(x.Scope.DriveId) || x.Scope.DriveId.Length > 2048) ||
                x.Scope.DriveId == null && x.RemoveBookmarks) || records.Select(x => x.Scope).Distinct().Count() != records.Count)
                throw new ConfigurationException(ConfigurationFailure.Invalid);
        }

        public sealed class AccessLease : IDisposable
        {
            private readonly AccountAccessState _owner;
            private readonly CancellationTokenSource _cancel;
            private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal AccessLease(AccountAccessState owner, string account, string drive, CancellationToken token, bool interactive = false)
            {
                _owner = owner; AccountId = account; DriveId = drive;
                Interactive = interactive;
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            internal string AccountId { get; }
            internal string DriveId { get; }
            internal bool Interactive { get; }
            public CancellationToken Token => _cancel.Token;
            internal Task Completion => _done.Task;
            internal void Cancel() { lock (_owner._gate) { if (!_done.Task.IsCompleted) _cancel.Cancel(); } }
            public void Dispose()
            {
                lock (_owner._gate)
                {
                    if (_done.Task.IsCompleted) return;
                    _owner._leases.Remove(this);
                    _done.TrySetResult();
                    _cancel.Dispose();
                }
            }
        }
    }
}
