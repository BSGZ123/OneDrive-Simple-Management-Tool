using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDrive_Simple_Management_Tool.Services
{
    // File and byte checkpoints commit encrypted deltas rather than a full directory manifest.
    // A truncated final journal frame is an uncommitted write; complete invalid frames are errors.
    public sealed class TransferStore
    {
        private const string Purpose = "OneDriveSimpleManagementTool/transfers/v1";
        private readonly string _directory;
        private readonly IConfigurationProtector _protector;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, CachedRecord> _saved = new();
        private readonly Dictionary<Guid, long> _journalLengths = new();
        private readonly HashSet<Guid> _removed = new();

        public TransferStore(string directory, IConfigurationProtector protector = null)
        {
            _directory = System.IO.Path.GetFullPath(directory);
            _protector = protector ?? new WindowsConfigurationProtector();
        }

        public IReadOnlyList<TransferRecord> Load()
        {
            lock (_gate)
            {
                if (!Directory.Exists(_directory)) return Array.Empty<TransferRecord>();
                var records = new List<TransferRecord>();
                foreach (string path in Directory.EnumerateFiles(_directory, "*.dat").OrderBy(path => path))
                {
                    if (File.Exists(path + ".removed")) { CleanupRemoved(path); continue; }
                    if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException();
                    byte[] data = _protector.Unprotect(File.ReadAllBytes(path), Purpose);
                    try
                    {
                        var record = JsonSerializer.Deserialize(data, TransferJsonContext.Default.TransferRecord);
                        Validate(record);
                        if (System.IO.Path.GetFileNameWithoutExtension(path) != record.Id.ToString("N")) throw new InvalidDataException();
                        Replay(record, path + ".journal");
                        Validate(record);
                        records.Add(record);
                        _saved[record.Id] = new(record);
                    }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
                }
                return records;
            }
        }

        public void Save(TransferRecord record)
        {
            lock (_gate)
            {
                Validate(record, entries: false);
                Directory.CreateDirectory(_directory);
                string path = RecordPath(record.Id);
                if (_removed.Contains(record.Id) || File.Exists(path + ".removed")) throw new IOException("The task was removed.");
                if (!_saved.TryGetValue(record.Id, out var previous))
                {
                    Validate(record);
                    if (File.Exists(path)) throw new IOException("Load existing transfer records before updating them.");
                    WriteSnapshot(path, record);
                    _saved[record.Id] = new(record);
                }
                else
                {
                    if (previous.Downloads.Count != record.Downloads.Count || previous.Uploads.Count != record.Uploads.Count) Validate(record);
                    if (record.Downloads.Count < previous.Downloads.Count || record.Uploads.Count < previous.Uploads.Count) throw new InvalidDataException();
                    var delta = new TransferDelta
                    {
                        Header = Header(record), Roots = record.Roots,
                        Downloads = record.Downloads.Where(x => !previous.Downloads.TryGetValue(x.Item.Id, out var old) || old != x).ToList(),
                        Uploads = record.Uploads.Where(x => !previous.Uploads.TryGetValue(x.Key, out var old) || old != x).ToList()
                    };
                    if (delta.Downloads.Count == 0 && delta.Uploads.Count == 0 && previous.Roots.SequenceEqual(record.Roots) &&
                        JsonSerializer.Serialize(previous.Header, TransferJsonContext.Default.TransferRecord) ==
                        JsonSerializer.Serialize(delta.Header, TransferJsonContext.Default.TransferRecord)) return;
                    byte[] data = JsonSerializer.SerializeToUtf8Bytes(delta, TransferJsonContext.Default.TransferDelta);
                    byte[] protectedData;
                    try { protectedData = _protector.Protect(data, Purpose); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
                    if (protectedData.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                    string journal = path + ".journal";
                    using (var stream = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Validate/truncate only the final incomplete frame after a previous crash/write failure.
                        long committed = _journalLengths.TryGetValue(record.Id, out long known) ? known : ValidLength(stream);
                        stream.SetLength(committed);
                        stream.Position = committed;
                        stream.Write(BitConverter.GetBytes(protectedData.Length));
                        stream.Write(protectedData);
                        stream.Flush(true);
                        _journalLengths[record.Id] = stream.Position;
                    }
                    if (new FileInfo(journal).Length > 16 * 1024 * 1024)
                    {
                        // The journal already includes this checkpoint. Replaying it after a crash
                        // between snapshot replacement and truncation is therefore idempotent.
                        WriteSnapshot(path, record);
                        File.Delete(journal);
                        _journalLengths[record.Id] = 0;
                    }
                    previous.Update(record, delta.Downloads, delta.Uploads);
                }
            }
        }

        private void WriteSnapshot(string path, TransferRecord record)
        {
            string temporary = path + ".tmp";
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, TransferJsonContext.Default.TransferRecord);
            try
            {
                byte[] protectedData = _protector.Protect(data, Purpose);
                if (protectedData.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedData);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void Replay(TransferRecord record, string journal)
        {
            if (!File.Exists(journal)) return;
            var downloads = record.Downloads.ToDictionary(x => x.Item.Id, StringComparer.Ordinal);
            var uploads = record.Uploads.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            long end = ValidLength(stream);
            _journalLengths[record.Id] = end;
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            while (stream.Position < end)
            {
                byte[] data = _protector.Unprotect(reader.ReadBytes(reader.ReadInt32()), Purpose);
                try
                {
                    var delta = JsonSerializer.Deserialize(data, TransferJsonContext.Default.TransferDelta);
                    if (delta?.Header?.Id != record.Id || delta.Header.Version != record.Version || delta.Header.AccountId != record.AccountId ||
                        delta.Header.DriveId != record.DriveId || delta.Header.Direction != record.Direction || delta.Header.Path != record.Path ||
                        delta.Roots == null || delta.Downloads == null || delta.Uploads == null) throw new InvalidDataException();
                    record.DownloadState = delta.Header.DownloadState; record.UploadState = delta.Header.UploadState;
                    record.DownloadFailure = delta.Header.DownloadFailure;
                    record.Size = delta.Header.Size; record.Name = delta.Header.Name;
                    record.Priority = delta.Header.Priority; record.SpeedLimitKiB = delta.Header.SpeedLimitKiB;
                    record.DownloadResume = delta.Header.DownloadResume;
                    record.Roots = delta.Roots;
                    foreach (var entry in delta.Downloads) downloads[entry.Item.Id] = entry;
                    foreach (var entry in delta.Uploads) uploads[entry.Key] = entry;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
                }
            }
            record.Downloads = downloads.Values.ToList(); record.Uploads = uploads.Values.ToList();
        }

        private static long ValidLength(FileStream stream)
        {
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            long committed = 0;
            while (stream.Length - stream.Position >= sizeof(int))
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 64 * 1024 * 1024) throw new InvalidDataException();
                if (stream.Length - stream.Position < length) break;
                stream.Position += length;
                committed = stream.Position;
            }
            return committed;
        }

        private static TransferRecord Header(TransferRecord record) => record with { Roots = new(), Downloads = new(), Uploads = new() };
        private sealed class CachedRecord
        {
            public TransferRecord Header;
            public DownloadRootRecord[] Roots;
            public readonly Dictionary<string, DownloadNodeRecord> Downloads = new(StringComparer.Ordinal);
            public readonly Dictionary<string, UploadEntryRecord> Uploads = new(StringComparer.OrdinalIgnoreCase);
            public CachedRecord(TransferRecord record) => Update(record, record.Downloads, record.Uploads);
            public void Update(TransferRecord record, IEnumerable<DownloadNodeRecord> downloads, IEnumerable<UploadEntryRecord> uploads)
            {
                Header = TransferStore.Header(record);
                Roots = record.Roots.Select(x => x with { }).ToArray();
                foreach (var entry in downloads) Downloads[entry.Item.Id] = entry with { };
                foreach (var entry in uploads) Uploads[entry.Key] = entry with { };
            }
        }

        public void Remove(Guid id)
        {
            lock (_gate)
            {
                string path = RecordPath(id);
                Directory.CreateDirectory(_directory);
                // Commit removal before deleting either part of the checkpoint. A failed cleanup
                // must never resurrect an old snapshot on the next launch.
                using (var marker = new FileStream(path + ".removed", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) marker.Flush(true);
                _removed.Add(id);
                _saved.Remove(id);
                _journalLengths.Remove(id);
                CleanupRemoved(path);
            }
        }

        private static void CleanupRemoved(string path)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + ".journal");
                File.Delete(path + ".removed");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private string RecordPath(Guid id) => System.IO.Path.Combine(_directory, id.ToString("N") + ".dat");

        private static void Validate(TransferRecord record, bool entries = true)
        {
            if (record == null || record.Version != 1 || record.Id == Guid.Empty ||
                !Enum.IsDefined(record.Direction) || !Enum.IsDefined(record.DownloadState) || !Enum.IsDefined(record.DownloadFailure) || !Enum.IsDefined(record.UploadState) ||
                string.IsNullOrWhiteSpace(record.AccountId) || string.IsNullOrWhiteSpace(record.DriveId) ||
                string.IsNullOrWhiteSpace(record.Name) || !System.IO.Path.IsPathFullyQualified(record.Path ?? "") ||
                record.Roots == null || record.Downloads == null || record.Uploads == null || record.Size < 0 ||
                record.Priority is < 0 or > 2 || record.SpeedLimitKiB is < 0 or > TransferPolicy.MaximumKiBPerSecond)
                throw new InvalidDataException("Invalid transfer record.");
            if (!entries) return;
            ValidateResume(record.DownloadResume);
            foreach (var download in record.Downloads) ValidateResume(download?.Resume);
            foreach (var upload in record.Uploads)
            {
                var resume = upload?.Resume;
                if (resume != null && (resume.Size < 1 || resume.Bytes < 0 || resume.Bytes > resume.Size ||
                    resume.SourceSha256?.Length != 64 || string.IsNullOrWhiteSpace(resume.Url) ||
                    resume.ConflictBehavior is not ("fail" or "replace" or "rename"))) throw new InvalidDataException("Invalid upload checkpoint.");
            }
            if (record.Downloads.Any(x => x?.Item == null || x.StagingId == Guid.Empty || x.Item.Size < 0 || string.IsNullOrWhiteSpace(x.Item.Id)) ||
                record.Roots.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id)) ||
                record.Uploads.Any(x => x == null || string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Name) || !Enum.IsDefined(x.State)) ||
                record.Downloads.Select(x => x.Item.Id).Distinct(StringComparer.Ordinal).Count() != record.Downloads.Count ||
                record.Uploads.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Uploads.Count)
                throw new InvalidDataException("Invalid transfer entries.");
        }

        private static void ValidateResume(DownloadResumeRecord resume)
        {
            if (resume != null && (resume.Size < 0 || resume.Bytes < 0 || resume.Bytes > resume.Size ||
                string.IsNullOrEmpty(resume.Version) || resume.PrefixSha256?.Length != 64)) throw new InvalidDataException("Invalid download checkpoint.");
        }
    }

    internal sealed class TransferDelta
    {
        public TransferRecord Header { get; set; }
        public List<DownloadRootRecord> Roots { get; set; }
        public List<DownloadNodeRecord> Downloads { get; set; }
        public List<UploadEntryRecord> Uploads { get; set; }
    }

    [JsonSerializable(typeof(TransferRecord))]
    [JsonSerializable(typeof(TransferDelta))]
    internal partial class TransferJsonContext : JsonSerializerContext { }
}
