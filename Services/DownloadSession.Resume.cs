using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed partial class DownloadSession
    {
        private DownloadResumeRecord _resume;
        private Action<DownloadResumeRecord> _saveResume;

        public void AttachResume(DownloadResumeRecord resume, Action<DownloadResumeRecord> save)
        {
            _resume = resume;
            _saveResume = save;
            _discardPreviousPartial = resume == null;
            if (resume != null) _source = new DownloadSource("", resume.Size, resume.Version);
        }

        private void SaveResume(DownloadResumeRecord resume)
        {
            _resume = resume;
            _saveResume?.Invoke(resume);
        }

        private static void CheckRegularFile(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A transfer file cannot be a reparse point.");
        }

        private async Task<bool> RecoverCommittedDownloadAsync(DownloadSource source, CancellationToken token)
        {
            // A crash may occur after the atomic move but before the completion checkpoint.
            if (_saveResume == null || _resume?.Bytes != source.Size || File.Exists(_stagingPath) || !File.Exists(DestinationPath)) return false;
            CheckRegularFile(DestinationPath);
            await using var stream = new FileStream(DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != source.Size || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != _resume.PrefixSha256)
                return false;
            ReleaseDestination();
            Update(state: DownloadTaskState.Completed, received: source.Size);
            SaveResume(null);
            return true;
        }

        private async Task TransferDurableAsync(DownloadSource source, CancellationToken token)
        {
            CheckRegularFile(_stagingPath);
            await using var output = new FileStream(_stagingPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long offset = _resume?.Bytes ?? 0;
            bool valid = _resume != null && _resume.Version == source.Version && _resume.Size == source.Size &&
                offset >= 0 && offset <= source.Size && output.Length >= offset;
            if (valid)
            {
                long remaining = offset;
                while (remaining > 0)
                {
                    int count = await output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (count == 0) { valid = false; break; }
                    hash.AppendData(buffer, 0, count);
                    remaining -= count;
                }
                valid &= Convert.ToHexString(hash.GetCurrentHash()) == _resume.PrefixSha256;
            }
            if (!valid)
            {
                if (offset > 0) Update(received: 0, restarted: true);
                offset = 0;
                hash.GetHashAndReset();
            }
            // Bytes beyond the last fsynced checkpoint are never assumed committed.
            output.SetLength(offset);
            output.Position = offset;
            void Checkpoint()
            {
                output.Flush(true);
                SaveResume(new(source.Version, source.Size, offset, Convert.ToHexString(hash.GetCurrentHash())));
            }
            Checkpoint();
            Update(received: offset, state: DownloadTaskState.Downloading);
            if (offset == source.Size) { _readyToSave = true; return; }

            using var client = new HttpClient(new DownloadResponseHandler(source.Size)) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, source.Size - 1);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            long committed = offset, startedAt = offset;
            var timer = Stopwatch.StartNew();
            long lastCheckpoint = 0;
            try
            {
                while (true)
                {
                    int block = Policy.BlockSize(buffer.Length);
                    using var blockTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    blockTimeout.CancelAfter(_options.BlockTimeoutMilliseconds);
                    int count = await input.ReadAsync(buffer.AsMemory(0, block), blockTimeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    await Policy.WaitAsync(count, token).ConfigureAwait(false);
                    Policy.ReportBytes(count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    offset += count;
                    lock (_gate)
                    {
                        if (_snapshot.State == DownloadTaskState.Downloading)
                            Publish(received: offset, speed: (long)((offset - startedAt) / Math.Max(0.001, timer.Elapsed.TotalSeconds)));
                    }
                    if (offset - committed >= 4 * 1024 * 1024 || timer.ElapsedMilliseconds - lastCheckpoint >= 1000)
                    {
                        Checkpoint();
                        committed = offset;
                        lastCheckpoint = timer.ElapsedMilliseconds;
                    }
                }
                if (offset != source.Size) throw new HttpIOException(HttpRequestError.ResponseEnded);
                _readyToSave = true;
            }
            finally
            {
                // Drain and flush on pause/failure too; the cancelled network token must not cancel saving.
                output.SetLength(offset);
                Checkpoint();
            }
        }
    }
}
