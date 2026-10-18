using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class DownloadBatchSession : IDownloadSession
    {
        private sealed class Root(string id)
        {
            public string Id = id;
            public string Name = id;
            public bool Resolved;
            public DownloadFailure Failure;
        }

        private sealed class Node(DownloadTreeItem item, string path, Node parent)
        {
            public DownloadTreeItem Item = item;
            public string Path = path;
            public Node Parent = parent;
            public bool Enumerated;
            public string NextLink;
            public HashSet<string> LoadedLinks = new(StringComparer.Ordinal);
            public DownloadFailure Failure;
            public DownloadSession Session;
            public DownloadSnapshot Progress;
            public Guid StagingId = Guid.NewGuid();
            public DownloadResumeRecord Resume;
        }

        private readonly object _gate = new();
        private readonly IDownloadTreeSource _source;
        private readonly Root[] _roots;
        private readonly HashSet<string> _selectedIds;
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly DownloadBatchPaths _paths;
        private readonly DownloadSessionOptions _fileOptions;
        private CancellationTokenSource _cancellation;
        private Task _activeTask = Task.CompletedTask;
        private Task _cancelTask = Task.CompletedTask;
        private bool _cancelRequested;
        private DownloadFailure _fatalFailure;
        private long _lastProgress;
        private TransferRecord _record;
        private Action<TransferRecord> _checkpoint;
        private string _retryItemId;
        private DownloadSnapshot _snapshot = new(0, DownloadTaskState.Pending, 0, 0, 0, 0, DownloadFailure.None, false);

        public DownloadBatchSession(IDownloadTreeSource source, IEnumerable<string> itemIds, string destination,
            DownloadSessionOptions options = null, IReadOnlyDictionary<string, string> names = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _selectedIds = itemIds.ToHashSet(StringComparer.Ordinal);
            if (_selectedIds.Count == 0 || _selectedIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(nameof(itemIds));
            _roots = _selectedIds.Select(id => new Root(id) { Name = names != null && names.TryGetValue(id, out var name) ? name : id }).ToArray();
            DestinationPath = Path.GetFullPath(destination);
            _paths = new DownloadBatchPaths(DestinationPath);
            options ??= new DownloadSessionOptions();
            _fileOptions = new DownloadSessionOptions
            {
                MaxAttempts = options.MaxAttempts, BlockTimeoutMilliseconds = options.BlockTimeoutMilliseconds,
                RetryDelay = options.RetryDelay, ReplaceExisting = false, ValidateDestination = _paths.Validate, Policy = options.Policy
            };
        }

        public string DestinationPath { get; }
        public TransferPolicy Policy => _fileOptions.Policy;
        public DownloadSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
        public event Action<DownloadSnapshot> Changed;
        public Action RequireCheckpoint { get; set; }

        public void AttachRecord(TransferRecord record, Action<TransferRecord> checkpoint, bool restore)
        {
            lock (_gate)
            {
                if (restore)
                {
                    try
                    {
                        foreach (var saved in record.Roots)
                        {
                            var root = _roots.Single(x => x.Id == saved.Id);
                            root.Name = saved.Name; root.Resolved = saved.Resolved; root.Failure = saved.Failure;
                        }
                        var pending = record.Downloads.ToList();
                        while (pending.Count > 0)
                        {
                            var ready = pending.Where(x => x.ParentId == null || _nodes.ContainsKey(x.ParentId)).ToArray();
                            if (ready.Length == 0) throw new InvalidDataException();
                            foreach (var saved in ready)
                            {
                                ValidateItem(saved.Item);
                                _paths.Restore(saved.Path);
                                var node = new Node(saved.Item, saved.Path, saved.ParentId == null ? null : _nodes[saved.ParentId])
                                {
                                    Enumerated = saved.Enumerated, Failure = saved.Failure, StagingId = saved.StagingId, Resume = saved.Resume,
                                    Progress = new(0, saved.Completed ? DownloadTaskState.Completed : DownloadTaskState.Pending,
                                        saved.Completed ? saved.Item.Size : saved.Resume?.Bytes ?? 0, saved.Resume?.Size ?? saved.Item.Size, 0, 0, DownloadFailure.None, false)
                                };
                                _nodes.Add(saved.Item.Id, node);
                                pending.Remove(saved);
                            }
                        }
                        var state = record.DownloadState is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Failed or DownloadTaskState.Paused
                            ? record.DownloadState : DownloadTaskState.Interrupted;
                        Publish(state);
                        if (state is DownloadTaskState.Completed or DownloadTaskState.Cancelled) _paths.Dispose();
                    }
                    catch { _paths.Dispose(); throw; }
                }
                _record = record;
                _checkpoint = checkpoint;
                if (!restore) SaveCheckpoint();
            }
        }

        public void ReleaseReservations() => _paths.Dispose();

        public Task RetryItemAsync(string id)
        {
            lock (_gate)
            {
                if (!_activeTask.IsCompleted || _snapshot.State is not (DownloadTaskState.Failed or DownloadTaskState.Interrupted or DownloadTaskState.Paused))
                    return _activeTask;
                if (!_nodes.ContainsKey(id) && !_roots.Any(x => x.Id == id)) return Task.CompletedTask;
                _retryItemId = id;
                return StartAsync();
            }
        }

        public async Task StopForExitAsync()
        {
            bool preserveState = Snapshot.State is DownloadTaskState.Paused or DownloadTaskState.Pausing or DownloadTaskState.Failed;
            await PauseAsync().ConfigureAwait(false);
            if (Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
            foreach (var node in _nodes.Values.Where(x => x.Session != null))
            {
                await node.Session.StopForExitAsync().ConfigureAwait(false);
                lock (_gate) node.Progress = node.Session.Snapshot;
            }
            lock (_gate) if (!preserveState) Publish(DownloadTaskState.Interrupted);
        }

        private bool Selected(Node node)
        {
            if (_retryItemId == null) return true;
            for (var current = node; current != null; current = current.Parent)
                if (current.Item.Id == _retryItemId) return true;
            return false;
        }

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (!_activeTask.IsCompleted || !_cancelTask.IsCompleted || _snapshot.State is not
                    (DownloadTaskState.Pending or DownloadTaskState.Paused or DownloadTaskState.Failed or DownloadTaskState.Interrupted)) return _activeTask;
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                _cancelRequested = false;
                _fatalFailure = DownloadFailure.None;
                Publish(DownloadTaskState.Preparing);
                _activeTask = Task.Run(() => RunAsync(_cancellation.Token));
                return _activeTask;
            }
        }

        public async Task PauseAsync()
        {
            CancellationTokenSource cancellation;
            Task running;
            lock (_gate)
            {
                if (_snapshot.State == DownloadTaskState.Pending) Publish(DownloadTaskState.Paused);
                cancellation = null;
                if (_snapshot.State is DownloadTaskState.Preparing or DownloadTaskState.Downloading)
                {
                    Publish(DownloadTaskState.Pausing);
                    cancellation = _cancellation;
                }
                running = _activeTask;
            }
            cancellation?.Cancel();
            await running.ConfigureAwait(false);
        }

        public Task CancelAsync()
        {
            lock (_gate)
            {
                if (!_cancelTask.IsCompleted) return _cancelTask;
                if (_snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return Task.CompletedTask;
                _cancelRequested = true;
                Publish(DownloadTaskState.Cancelling);
                return _cancelTask = CancelCoreAsync(_cancellation, _activeTask);
            }
        }

        private async Task CancelCoreAsync(CancellationTokenSource cancellation, Task running)
        {
            await Task.Yield();
            cancellation?.Cancel();
            await running.ConfigureAwait(false);
            Node[] files;
            lock (_gate) files = _nodes.Values.Where(node => !node.Item.IsFolder && node.Progress?.State != DownloadTaskState.Completed).ToArray();
            bool clean = true;
            foreach (var node in files)
            {
                if (node.Session == null)
                {
                    node.Session = new DownloadSession(node.Path, ct => _source.GetDownloadSourceAsync(node.Item.Id, ct),
                        new DownloadSessionOptions { StagingId = node.StagingId, ValidateDestination = _paths.Validate });
                }
                await node.Session.CancelAsync().ConfigureAwait(false);
                var progress = node.Session.Snapshot;
                lock (_gate) { node.Progress = progress; if (progress.State == DownloadTaskState.Cancelled) node.Resume = null; }
                clean &= progress.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled;
            }
            lock (_gate)
            {
                if (clean) _paths.Dispose();
                else _fatalFailure = DownloadFailure.LocalStorage;
                Publish(clean ? DownloadTaskState.Cancelled : DownloadTaskState.Failed);
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                lock (_gate) RequireCheckpoint?.Invoke();
                token.ThrowIfCancellationRequested();
                _paths.EnsureDirectory(DestinationPath);
                await ResolveRootsAsync(token).ConfigureAwait(false);
                // Iterate a growing queue instead of recursive calls, including empty folders.
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    Node folder;
                    lock (_gate) folder = _nodes.Values.FirstOrDefault(node => Selected(node) && node.Item.IsFolder && !node.Enumerated && !visited.Contains(node.Item.Id));
                    if (folder == null) break;
                    visited.Add(folder.Item.Id);
                    token.ThrowIfCancellationRequested();
                    await ScanFolderAsync(folder, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                Node[] files;
                lock (_gate)
                {
                    files = _nodes.Values.Where(node => Selected(node) && !node.Item.IsFolder && node.Progress?.State != DownloadTaskState.Completed).ToArray();
                    Publish(DownloadTaskState.Downloading);
                }
                int next = -1;
                async Task Worker()
                {
                    while (!token.IsCancellationRequested)
                    {
                        int index = Interlocked.Increment(ref next);
                        if (index >= files.Length) return;
                        await DownloadFileAsync(files[index], token).ConfigureAwait(false);
                    }
                }
                await Task.WhenAll(Enumerable.Range(0, Math.Min(3, files.Length)).Select(_ => Worker())).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    bool complete = _roots.All(root => root.Resolved) && _nodes.Values.All(node => node.Item.IsFolder
                        ? node.Enumerated : node.Progress?.State == DownloadTaskState.Completed);
                    if (!_cancelRequested)
                    {
                        if (complete) _paths.Dispose();
                        Publish(complete ? DownloadTaskState.Completed : DownloadTaskState.Failed);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _fatalFailure = Classify(exception);
                    if (!_cancelRequested) Publish(DownloadTaskState.Failed);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (token.IsCancellationRequested && !_cancelRequested) Publish(DownloadTaskState.Paused);
                    _retryItemId = null;
                }
            }
        }

        private async Task ResolveRootsAsync(CancellationToken token)
        {
            var metadata = new Dictionary<string, DownloadTreeItem>(StringComparer.Ordinal);
            async Task<DownloadTreeItem> Get(string id)
            {
                if (!metadata.TryGetValue(id, out var item))
                {
                    item = await _source.GetDownloadItemAsync(id, token).ConfigureAwait(false);
                    ValidateItem(item);
                    if (item.Id != id) throw new InvalidDataException();
                    metadata.Add(id, item);
                }
                return item;
            }
            foreach (var root in _roots.Where(root => !root.Resolved && (_retryItemId == null || root.Id == _retryItemId)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var item = await Get(root.Id).ConfigureAwait(false);
                    lock (_gate) root.Name = item.Name;
                    var parent = item;
                    var ancestors = new HashSet<string>(StringComparer.Ordinal) { item.Id };
                    bool covered = false;
                    while (!parent.IsRoot && parent.ParentId != null)
                    {
                        if (!ancestors.Add(parent.ParentId) || ancestors.Count > 512) throw new InvalidDataException();
                        covered |= _selectedIds.Contains(parent.ParentId);
                        parent = await Get(parent.ParentId).ConfigureAwait(false);
                        if (!parent.IsFolder) throw new InvalidDataException();
                    }
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (!covered) _nodes.Add(item.Id, new Node(item, _paths.Reserve(DestinationPath, item.Name, item.IsFolder), null));
                        root.Resolved = true;
                        root.Failure = DownloadFailure.None;
                        Publish();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    lock (_gate) { root.Failure = Classify(exception); Publish(); }
                }
            }
        }

        private async Task ScanFolderAsync(Node folder, CancellationToken token)
        {
            try
            {
                _paths.EnsureDirectory(folder.Path);
                lock (_gate) folder.Failure = DownloadFailure.None;
                do
                {
                    token.ThrowIfCancellationRequested();
                    string link = folder.NextLink;
                    if (link != null && folder.LoadedLinks.Contains(link)) throw new InvalidDataException();
                    var page = await _source.GetDownloadChildrenAsync(folder.Item.Id, link, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (page?.Items == null) throw new InvalidDataException();
                    lock (_gate)
                    {
                        // Commit each page atomically relative to cancellation. Retry resumes
                        // its continuation, and duplicate IDs never allocate another filename.
                        foreach (var item in page.Items)
                        {
                            ValidateItem(item);
                            if (item.IsRoot || (item.ParentId != null && item.ParentId != folder.Item.Id)) throw new InvalidDataException();
                            if (_nodes.TryGetValue(item.Id, out var existing))
                            {
                                if (existing.Parent != folder || existing.Item.IsFolder != item.IsFolder) throw new InvalidDataException();
                                continue;
                            }
                            _nodes.Add(item.Id, new Node(item, _paths.Reserve(folder.Path, item.Name, item.IsFolder), folder));
                        }
                        if (link != null) folder.LoadedLinks.Add(link);
                        folder.NextLink = string.IsNullOrEmpty(page.NextLink) ? null : page.NextLink;
                        folder.Enumerated = folder.NextLink == null;
                        Publish();
                    }
                } while (!folder.Enumerated);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                lock (_gate) { folder.Failure = Classify(exception); Publish(); }
            }
        }

        private async Task DownloadFileAsync(Node node, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                lock (_gate) RequireCheckpoint?.Invoke();
                _paths.EnsureDirectory(Path.GetDirectoryName(node.Path));
                if (node.Session == null || node.Progress?.State == DownloadTaskState.Cancelled)
                {
                    node.Session = new DownloadSession(node.Path, ct => _source.GetDownloadSourceAsync(node.Item.Id, ct), new DownloadSessionOptions
                    {
                        MaxAttempts = _fileOptions.MaxAttempts, BlockTimeoutMilliseconds = _fileOptions.BlockTimeoutMilliseconds,
                        RetryDelay = _fileOptions.RetryDelay, ReplaceExisting = false, ValidateDestination = _paths.Validate,
                        StagingId = _record == null ? null : node.StagingId, Policy = Policy
                    });
                    node.Session.Changed += progress => OnFileProgress(node, progress);
                    if (_record != null) node.Session.AttachResume(node.Resume, resume =>
                    {
                        lock (_gate)
                        {
                            node.Resume = resume;
                            SaveCheckpoint();
                            RequireCheckpoint?.Invoke();
                        }
                    });
                }
                lock (_gate) node.Failure = DownloadFailure.None;
                // Start installs its cancellation source before registration, so an
                // immediate stop cannot be lost between queue admission and transfer.
                Task running = node.Session.StartAsync();
                using var registration = token.Register(() => _ = node.Session.PauseAsync());
                await running.ConfigureAwait(false);
                OnFileProgress(node, node.Session.Snapshot);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                lock (_gate) { node.Failure = Classify(exception); Publish(); }
            }
        }

        private void OnFileProgress(Node node, DownloadSnapshot progress)
        {
            lock (_gate)
            {
                var previous = node.Progress;
                node.Progress = progress;
                if (progress.State == DownloadTaskState.Downloading && previous?.State == progress.State &&
                    Environment.TickCount64 - _lastProgress < 200) return;
                _lastProgress = Environment.TickCount64;
                Publish(checkpoint: previous?.State != progress.State);
            }
        }

        private static void ValidateItem(DownloadTreeItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Size < 0)
                throw new InvalidDataException();
        }

        private static DownloadFailure Classify(Exception exception) => exception switch
        {
            DownloadFailureException failure => failure.Failure,
            InvalidDataException => DownloadFailure.InvalidResponse,
            IOException or UnauthorizedAccessException => DownloadFailure.LocalStorage,
            HttpRequestException or OperationCanceledException => DownloadFailure.Network,
            _ => DownloadFailure.Unknown
        };

        // Called only under _gate. Cached child snapshots avoid taking a child session
        // lock here: its progress callback can itself be holding that lock.
        private void Publish(DownloadTaskState? state = null, bool checkpoint = true)
        {
            var failures = _roots.Where(root => root.Failure != DownloadFailure.None)
                .Select(root => new DownloadItemFailure(root.Name, root.Failure, root.Id)).ToList();
            if (_fatalFailure != DownloadFailure.None) failures.Add(new DownloadItemFailure(DestinationPath, _fatalFailure));
            long received = 0, total = 0, speed = 0;
            int files = 0, completed = 0, folders = 0;
            bool discovered = _roots.All(root => root.Resolved);
            foreach (var node in _nodes.Values)
            {
                if (node.Item.IsFolder) { folders++; discovered &= node.Enumerated; }
                else
                {
                    files++;
                    total += node.Progress?.State == DownloadTaskState.Completed || node.Progress?.TotalBytes > 0 ? node.Progress.TotalBytes : node.Item.Size;
                    received += node.Progress?.ReceivedBytes ?? 0;
                    speed += node.Progress?.BytesPerSecond ?? 0;
                    if (node.Progress?.State == DownloadTaskState.Completed) completed++;
                }
                var failure = node.Failure != DownloadFailure.None ? node.Failure :
                    node.Progress?.State == DownloadTaskState.Failed ? node.Progress.Failure : DownloadFailure.None;
                if (failure != DownloadFailure.None) failures.Add(new DownloadItemFailure(Path.GetRelativePath(DestinationPath, node.Path), failure, node.Item.Id));
            }
            var next = state ?? _snapshot.State;
            _snapshot = new DownloadSnapshot(_snapshot.Revision + 1, next, received, total,
                next == DownloadTaskState.Downloading ? speed : 0, 0,
                next == DownloadTaskState.Failed ? failures.FirstOrDefault()?.Failure ?? DownloadFailure.Unknown : DownloadFailure.None,
                false, new DownloadBatchDetails(files, completed, folders, discovered, failures.ToArray()));
            if (checkpoint) SaveCheckpoint();
            Changed?.Invoke(_snapshot);
        }

        private void SaveCheckpoint()
        {
            if (_record == null) return;
            _record.DownloadState = _snapshot.State;
            _record.Roots = _roots.Select(x => new DownloadRootRecord { Id = x.Id, Name = x.Name, Resolved = x.Resolved, Failure = x.Failure }).ToList();
            _record.Downloads = _nodes.Values.Select(x => new DownloadNodeRecord
            {
                Item = x.Progress?.State == DownloadTaskState.Completed ? x.Item with { Size = x.Progress.TotalBytes } : x.Item,
                Path = x.Path, ParentId = x.Parent?.Item.Id, Enumerated = x.Enumerated,
                Completed = x.Progress?.State == DownloadTaskState.Completed, StagingId = x.StagingId,
                Resume = x.Resume,
                Failure = x.Failure != DownloadFailure.None ? x.Failure : x.Progress?.Failure ?? DownloadFailure.None
            }).ToList();
            _checkpoint(_record);
        }
    }
}
