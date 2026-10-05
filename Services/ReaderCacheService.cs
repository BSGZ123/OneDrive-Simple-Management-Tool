using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class ReaderCacheService
    {
        public const long MaximumCacheBytes = 500_000_000;
        private readonly ApplicationDataPaths _paths;
        private readonly long _maximumBytes;
        private readonly ProtectedConfigurationFile<List<ReaderCacheEntry>> _index;
        public ReaderCacheService(ApplicationDataPaths paths, long maximumBytes = MaximumCacheBytes)
        {
            if (maximumBytes <= 0 || maximumBytes > MaximumCacheBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            _maximumBytes = maximumBytes;
            _paths = paths;
            _index = new(paths.ReaderCacheIndex, "reader-cache", ReaderJsonContext.Default.ListReaderCacheEntry, ValidateIndex, 4 * 1024 * 1024);
        }

        public async Task<ReaderLocalBook> OpenAsync(ReaderOpenRequest request, IProgress<double> progress, CancellationToken token)
        {
            if (request.Identity == null && request.ResolveSource == null)
                return await ReaderLocalBook.OpenAsync(request.LocalPath, token).ConfigureAwait(false);
            if (request.LocalPath != null || request.ResolveSource == null || request.Identity?.Kind != "onedrive"
                || !ReaderProtocol.ValidIdentity(request.Identity)) throw new ReaderException("InvalidBook");
            // Authorization always precedes a cache hit. Network/auth failures never fall back to old bytes.
            var source = await request.ResolveSource(token).ConfigureAwait(false);
            ValidateSource(source);
            using var gate = await ConfigurationFileLease.AcquireAsync(_index.Path, token).ConfigureAwait(false);
            CheckRoot();
            Directory.CreateDirectory(_paths.ReaderCache);
            var snapshot = await _index.ReadAsync(token).ConfigureAwait(false);
            var entries = snapshot?.Data ?? new();
            long revision = snapshot?.Revision ?? 0;
            var cached = entries.Find(x => x.Identity == request.Identity && x.Version == source.Version && x.Size == source.Size);
            if (cached != null)
            {
                ReaderLocalBook book = null;
                try
                {
                    book = await ReaderLocalBook.OpenCachedAsync(OwnedPath(cached.FileName), cached.Identity, cached.Version, cached.Sha256, token).ConfigureAwait(false);
                    if (book.Length != source.Size) throw new ReaderException("InvalidBook");
                }
                catch (Exception error) when (error is IOException or ReaderException)
                {
                    book?.Dispose(); book = null;
                    entries.Remove(cached);
                    TryDelete(cached.FileName);
                }
                if (book != null)
                {
                    try
                    {
                        entries[entries.IndexOf(cached)] = cached with { LastUsed = DateTimeOffset.UtcNow };
                        await WriteIndexAsync(entries, revision, snapshot != null, token).ConfigureAwait(false);
                        return book;
                    }
                    catch { book.Dispose(); throw; }
                }
            }

            // The cross-process index lease serializes downloads and eviction; an active book's
            // read-only file lease independently prevents eviction in this or another process.
            CleanupOrphans(entries);
            MakeRoom(entries, source.Size, request.Identity);
            string name = Guid.NewGuid().ToString("N") + ".epub";
            string destination = OwnedPath(name);
            ReaderLocalBook opened = null;
            var download = new DownloadSession(destination, ResolveVersionAsync, new() { MaxAttempts = 2 });
            download.Changed += OnProgress;
            try
            {
                progress?.Report(0);
                await download.StartAsync().WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (download.Snapshot.State != DownloadTaskState.Completed)
                    throw new ReaderException(download.Snapshot.Failure switch
                    {
                        DownloadFailure.AccessDenied => "AccessDenied", DownloadFailure.NotFound => "NotFound",
                        DownloadFailure.SourceChanged => "SourceChanged", DownloadFailure.Network => "Network",
                        DownloadFailure.LocalStorage => "CacheStorage", _ => "InvalidBook"
                    });
                opened = await ReaderLocalBook.OpenCachedAsync(destination, request.Identity, source.Version, null, token).ConfigureAwait(false);
                entries.Add(new(request.Identity, source.Version, name, opened.ContentHash, opened.Length, DateTimeOffset.UtcNow));
                await WriteIndexAsync(entries, revision, snapshot != null, token).ConfigureAwait(false);
                var result = opened; opened = null;
                return result;
            }
            finally
            {
                download.Changed -= OnProgress;
                // Cancellation drains DownloadSession before releasing the cache gate. A closing
                // ReaderSession need not wait for this background cleanup to finish.
                await download.CancelAsync().ConfigureAwait(false);
                opened?.Dispose();
                if (!entries.Any(x => x.FileName == name) || opened != null) TryDelete(name);
            }

            async Task<DownloadSource> ResolveVersionAsync(CancellationToken cancellation)
            {
                try
                {
                    var current = await request.ResolveSource(cancellation).ConfigureAwait(false);
                    ValidateSource(current);
                    if (current.Version != source.Version || current.Size != source.Size) throw new DownloadFailureException(DownloadFailure.SourceChanged);
                    return current;
                }
                catch (ReaderException error)
                {
                    throw new DownloadFailureException(error.Code switch
                    {
                        "AccessDenied" => DownloadFailure.AccessDenied, "NotFound" => DownloadFailure.NotFound,
                        "Network" => DownloadFailure.Network, _ => DownloadFailure.InvalidResponse
                    });
                }
            }
            void OnProgress(DownloadSnapshot value)
            {
                if (!token.IsCancellationRequested && value.State == DownloadTaskState.Downloading)
                    progress?.Report(value.TotalBytes > 0 ? Math.Clamp((double)value.ReceivedBytes / value.TotalBytes, 0, 1) : 0);
            }
        }

        public async Task<int> ClearAsync(CancellationToken token = default)
        {
            using var gate = await ConfigurationFileLease.AcquireAsync(_index.Path, token).ConfigureAwait(false);
            CheckRoot();
            // Clear is an explicit user action and can recover a damaged cache index. Reading
            // progress/settings live elsewhere and are never included in this operation.
            List<ReaderCacheEntry> entries;
            long revision;
            bool existing;
            try
            {
                var snapshot = await _index.ReadAsync(token).ConfigureAwait(false);
                entries = snapshot?.Data ?? new(); revision = snapshot?.Revision ?? 0; existing = snapshot != null;
            }
            catch (ConfigurationException)
            {
                await _index.ArchiveAsync(_index.Path, token).ConfigureAwait(false);
                await _index.ArchiveAsync(_index.BackupPath, token).ConfigureAwait(false);
                entries = new(); revision = 0; existing = false;
            }
            int retained = 0;
            foreach (var file in OwnedFiles()) { token.ThrowIfCancellationRequested(); if (!TryDelete(Path.GetFileName(file))) retained++; }
            entries.RemoveAll(x => !File.Exists(OwnedPath(x.FileName)));
            await WriteIndexAsync(entries, revision, existing, token).ConfigureAwait(false);
            return retained;
        }
        private Task WriteIndexAsync(List<ReaderCacheEntry> entries, long revision, bool preserve, CancellationToken token) =>
            _index.WriteAsync(entries, checked(revision + 1), preserve, token);
        private void CleanupOrphans(List<ReaderCacheEntry> entries)
        {
            entries.RemoveAll(x => !File.Exists(OwnedPath(x.FileName)));
            foreach (string file in OwnedFiles())
                if (!entries.Any(x => x.FileName == Path.GetFileName(file))) TryDelete(Path.GetFileName(file));
        }
        private void MakeRoom(List<ReaderCacheEntry> entries, long additional, ReaderIdentity opening)
        {
            long bytes = OwnedFiles().Sum(path => new FileInfo(path).Length);
            foreach (var entry in entries.Where(x => x.Identity != opening).OrderBy(x => x.LastUsed).ToArray())
            {
                if (bytes + additional <= _maximumBytes && entries.Count < 1000) break;
                if (TryDelete(entry.FileName)) { bytes -= entry.Size; entries.Remove(entry); }
            }
            if (bytes + additional > _maximumBytes || entries.Count >= 1000) throw new ReaderException("CacheStorage");
        }
        private IEnumerable<string> OwnedFiles() => Directory.Exists(_paths.ReaderCache)
            ? Directory.EnumerateFiles(_paths.ReaderCache).Where(path => IsOwnedName(Path.GetFileName(path))) : [];
        private static bool IsBookName(string name) => name?.Length == 37 && name.EndsWith(".epub", StringComparison.Ordinal)
            && Guid.TryParseExact(name[..32], "N", out _);
        private static bool IsOwnedName(string name) => IsBookName(name) || name.Length is 47 or 56 && name.StartsWith(".cloudflow-", StringComparison.Ordinal)
            && (name.EndsWith(".tmp", StringComparison.Ordinal) || name.EndsWith(".tmp.download", StringComparison.Ordinal))
            && Guid.TryParseExact(name.Substring(11, 32), "N", out _);
        private string OwnedPath(string name)
        {
            if (!IsOwnedName(name) || Path.GetFileName(name) != name) throw new ReaderException("CacheStorage");
            string root = Path.GetFullPath(_paths.ReaderCache) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ReaderException("CacheStorage");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ReaderException("CacheStorage");
            return path;
        }
        private bool TryDelete(string name)
        {
            try { File.Delete(OwnedPath(name)); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private void CheckRoot()
        {
            for (var directory = new DirectoryInfo(_paths.ReaderCache); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new ReaderException("CacheStorage");
        }
        private static void ValidateSource(DownloadSource source)
        {
            if (source?.Size is not > 0 or > ReaderProtocol.MaximumBookBytes) throw new ReaderException("BookLimit");
            if (!ReaderProtocol.ValidText(source.Version, 2048)) throw new ReaderException("InvalidBook");
        }
        private static void ValidateIndex(List<ReaderCacheEntry> entries)
        {
            if (entries == null || entries.Count > 1000 || entries.Any(x => x == null || x.Identity?.Kind != "onedrive"
                || !ReaderProtocol.ValidIdentity(x.Identity) || !ReaderProtocol.ValidText(x.Version, 2048) || !IsBookName(x.FileName)
                || !ReaderProtocol.IsHash(x.Sha256) || x.Size is <= 0 or > ReaderProtocol.MaximumBookBytes || x.LastUsed == default)
                || entries.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                throw new ConfigurationException(ConfigurationFailure.Invalid);
        }
    }
}
