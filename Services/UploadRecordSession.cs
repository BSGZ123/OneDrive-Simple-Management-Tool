using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed record UploadSourceItem(string Key, string Name, bool IsFolder, long Size);

    public interface IUploadRecordSource
    {
        Task<IReadOnlyList<UploadSourceItem>> GetChildrenAsync(string key, CancellationToken token);
        Task<UploadOutcome> CreateFolderAsync(UploadEntryRecord item, string parentId, CancellationToken token);
        Task<UploadOutcome> UploadAsync(UploadEntryRecord item, string parentId, IProgress<long> progress, CancellationToken token);
    }

    public sealed class UploadRecordSession
    {
        private readonly TransferRecord _record;
        private readonly IUploadRecordSource _source;
        private readonly Action _save;
        private readonly Action _changed;
        private readonly Func<Exception, string> _errorKey;

        public UploadRecordSession(TransferRecord record, IUploadRecordSource source, Action save, Action changed, Func<Exception, string> errorKey)
        {
            _record = record; _source = source; _save = save; _changed = changed; _errorKey = errorKey;
            foreach (var entry in record.Uploads.Where(x => x.State == TransferEntryState.Working)) entry.State = TransferEntryState.Pending;
        }

        public async Task RunAsync(IProgress<long> progress, CancellationToken token, string selectedKey = null)
        {
            if (_record.Uploads.Count == 0)
            {
                _record.Uploads.Add(new() { Key = ".", Name = _record.Name, IsFolder = _record.IsFolder, Size = _record.Size });
                _save();
            }
            bool Selected(UploadEntryRecord item) => selectedKey == null || item.Key == selectedKey ||
                (selectedKey == "." || item.Key.StartsWith(selectedKey + "/", StringComparison.OrdinalIgnoreCase));
            // Discovery can fail independently for each directory. A later attempt retains known children.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var folder = _record.Uploads.FirstOrDefault(x => Selected(x) && x.IsFolder && !x.Enumerated &&
                    x.State != TransferEntryState.Skipped && !visited.Contains(x.Key));
                if (folder == null) break;
                visited.Add(folder.Key);
                token.ThrowIfCancellationRequested();
                try
                {
                    var children = await _source.GetChildrenAsync(folder.Key, token);
                    foreach (var child in children)
                    {
                        if (!_record.Uploads.Any(x => x.Key.Equals(child.Key, StringComparison.OrdinalIgnoreCase)))
                            _record.Uploads.Add(new() { Key = child.Key, ParentKey = folder.Key, Name = child.Name, IsFolder = child.IsFolder, Size = child.Size });
                    }
                    folder.Enumerated = true;
                    folder.State = TransferEntryState.Pending;
                    folder.ErrorKey = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { Fail(folder, exception); }
                _save(); _changed();
            }

            var byKey = _record.Uploads.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            string ParentId(UploadEntryRecord item) => item.ParentKey == null ? _record.ItemId : byKey[item.ParentKey].TargetId;
            bool ParentSkipped(UploadEntryRecord item) => item.ParentKey != null && byKey[item.ParentKey].State == TransferEntryState.Skipped;
            foreach (var folder in _record.Uploads.Where(x => x.IsFolder).OrderBy(x => x.Key.Count(c => c == '/')).ThenBy(x => x.Key.Length))
            {
                if (!Selected(folder) || folder.State is TransferEntryState.Completed or TransferEntryState.Skipped) continue;
                token.ThrowIfCancellationRequested();
                if (!folder.Enumerated) continue;
                try
                {
                    if (ParentSkipped(folder)) folder.State = TransferEntryState.Skipped;
                    else
                    {
                        if (ParentId(folder) == null) throw new IOException();
                        if (folder.TargetId == null)
                        {
                            folder.State = TransferEntryState.Working;
                            _save();
                            Apply(folder, await _source.CreateFolderAsync(folder, ParentId(folder), token));
                        }
                        else folder.State = TransferEntryState.Completed;
                        folder.ErrorKey = null;
                    }
                }
                catch (OperationCanceledException exception) when (token.IsCancellationRequested || exception is UploadAbortedException) { throw; }
                catch (Exception exception) { Fail(folder, exception); }
                _save(); _changed();
            }

            var files = _record.Uploads.Where(x => Selected(x) && !x.IsFolder && x.State is not (TransferEntryState.Completed or TransferEntryState.Skipped)).ToArray();
            var received = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var progressGate = new object();
            void Report(UploadEntryRecord file, long bytes)
            {
                lock (progressGate)
                {
                    received[file.Key] = Math.Clamp(bytes, 0, file.Size);
                    progress?.Report(_record.Uploads.Where(x => !x.IsFolder).Sum(x => x.State is TransferEntryState.Completed or TransferEntryState.Skipped
                        ? x.Size : received.GetValueOrDefault(x.Key, x.Resume?.Bytes ?? 0)));
                }
            }
            int next = -1;
            async Task Worker()
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int index = Interlocked.Increment(ref next);
                    if (index >= files.Length) return;
                    var file = files[index];
                    try
                    {
                        if (ParentSkipped(file)) file.State = TransferEntryState.Skipped;
                        else
                        {
                            if (ParentId(file) == null) throw new IOException();
                            file.State = TransferEntryState.Working;
                            _save(); _changed();
                            Apply(file, await _source.UploadAsync(file, ParentId(file), new FileProgress(value => Report(file, value)), token));
                            file.ErrorKey = null;
                        }
                    }
                    catch (OperationCanceledException exception) when (token.IsCancellationRequested || exception is UploadAbortedException) { throw; }
                    catch (Exception exception)
                    {
                        // A confirmed remote success must survive a failed local checkpoint.
                        // Retain it in memory for Save/Retry instead of uploading it again.
                        if (file.State == TransferEntryState.Completed) throw;
                        Fail(file, exception);
                    }
                    _save(); _changed();
                    Report(file, file.State is TransferEntryState.Completed or TransferEntryState.Skipped ? file.Size : 0);
                }
            }
            // A bounded producer avoids creating one waiting Task per file.
            await Task.WhenAll(Enumerable.Range(0, Math.Min(3, files.Length)).Select(_ => Worker()));
        }

        private void Fail(UploadEntryRecord item, Exception exception)
        {
            item.State = TransferEntryState.Failed;
            item.ErrorKey = _errorKey(exception);
        }

        private static void Apply(UploadEntryRecord item, UploadOutcome result)
        {
            if (result.Uploaded > 0 && string.IsNullOrWhiteSpace(result.TargetId)) throw new InvalidDataException();
            item.TargetId = result.TargetId;
            item.TargetName = result.TargetName;
            item.State = result.Uploaded > 0 ? TransferEntryState.Completed : TransferEntryState.Skipped;
        }

        private sealed class FileProgress(Action<long> report) : IProgress<long>
        {
            public void Report(long value) => report(value);
        }
    }
}
