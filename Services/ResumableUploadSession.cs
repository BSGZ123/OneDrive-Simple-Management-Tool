using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class UploadCompletionUnknownException : IOException { }
    public sealed record UploadSessionInfo(string Url, DateTimeOffset Expires);

    // Anonymous HTTP is intentional: uploadUrl already contains authorization.
    public sealed class ResumableUploadSession
    {
        public const int SliceSize = 10 * 320 * 1024;
        private readonly UploadEntryRecord _entry;
        private readonly Action _save;
        private readonly Func<string, CancellationToken, Task<UploadSessionInfo>> _create;
        private readonly TransferPolicy _policy;
        private readonly int _sliceSize;
        private long _rangeEnd;
        public ResumableUploadSession(UploadEntryRecord entry, Action save,
            Func<string, CancellationToken, Task<UploadSessionInfo>> create, TransferPolicy policy = null, int sliceSize = SliceSize)
        {
            if (sliceSize < 320 * 1024 || sliceSize > SliceSize || sliceSize % (320 * 1024) != 0) throw new ArgumentOutOfRangeException(nameof(sliceSize));
            _entry = entry; _save = save; _create = create; _policy = policy;
            _sliceSize = sliceSize;
        }

        private static Uri SessionUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new InvalidDataException("Invalid upload session address.");
            return uri;
        }

        private static HttpClient Client() => new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 1024 * 1024 };

        public static async Task DeleteAsync(UploadResumeRecord resume)
        {
            if (resume == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = Client();
            try { using var response = await client.DeleteAsync(SessionUri(resume.Url), timeout.Token).ConfigureAwait(false); }
            catch { /* Best effort; OneDrive also expires abandoned sessions. Never log the URL. */ }
        }

        private void Save(UploadResumeRecord resume) { _entry.Resume = resume; _save(); }

        public async Task<UploadOutcome> RunAsync(Stream content, string behavior, IProgress<long> progress, CancellationToken token)
        {
            if (!content.CanSeek || content.Length == 0) throw new InvalidDataException();
            content.Position = 0;
            string fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false));
            long size = content.Length;
            _entry.Size = size;
            var saved = _entry.Resume;
            if (saved != null && (saved.Size != size || saved.SourceSha256 != fingerprint))
            {
                await DeleteAsync(saved).ConfigureAwait(false);
                Save(null);
            }
            using var client = Client();
            using var uploadClient = Client();
            uploadClient.Timeout = Timeout.InfiniteTimeSpan;
            for (int generation = 0; generation < 2; generation++)
            {
                token.ThrowIfCancellationRequested();
                var resume = _entry.Resume;
                if (resume == null)
                {
                    var created = await _create(behavior, token).ConfigureAwait(false);
                    SessionUri(created.Url);
                    Save(new(created.Url, created.Expires, size, fingerprint, ConflictBehavior: behavior));
                    resume = _entry.Resume;
                }
                else behavior = resume.ConflictBehavior;

                using var status = await client.GetAsync(SessionUri(resume.Url), token).ConfigureAwait(false);
                if (status.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    // The final response may have been lost. Never silently create a duplicate.
                    if (resume.CommitPending) throw new UploadCompletionUnknownException();
                    Save(null);
                    continue;
                }
                status.EnsureSuccessStatusCode();
                using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                long offset = ReadStatus(statusJson.RootElement, size, resume);
                progress?.Report(offset);
                byte[] buffer = new byte[_sliceSize];
                int recoveries = 0;
                while (offset < size)
                {
                    token.ThrowIfCancellationRequested();
                    content.Position = offset;
                    int count = (int)Math.Min(_sliceSize, _rangeEnd - offset + 1);
                    if (count <= 0 || (offset + count != size && count % (320 * 1024) != 0)) throw new InvalidDataException();
                    await content.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    bool last = offset + count == size;
                    Save(_entry.Resume with { CommitPending = last });
                    using var request = new HttpRequestMessage(HttpMethod.Put, SessionUri(_entry.Resume.Url));
                    using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    uploadTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    request.Content = _policy == null ? new ByteArrayContent(buffer, 0, count) :
                        new ThrottledUploadContent(buffer, count, offset, _policy, progress, uploadTimeout);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, size);
                    using var response = await uploadClient.SendAsync(request, uploadTimeout.Token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    {
                        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                        string id = result.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                        if (!last || string.IsNullOrWhiteSpace(id)) throw new InvalidDataException();
                        string name = result.RootElement.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : _entry.Name;
                        _entry.TargetId = id; _entry.TargetName = name;
                        _entry.State = TransferEntryState.Completed;
                        Save(null);
                        progress?.Report(size);
                        return new(1, 0, id, name);
                    }
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && recoveries++ < 2)
                    {
                        using var refreshed = await client.GetAsync(SessionUri(_entry.Resume.Url), token).ConfigureAwait(false);
                        refreshed.EnsureSuccessStatusCode();
                        using var missing = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                        offset = ReadStatus(missing.RootElement, size, _entry.Resume);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.Accepted) throw new InvalidDataException();
                    using var next = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                    long nextOffset = ReadStatus(next.RootElement, size, _entry.Resume);
                    if (nextOffset <= offset && recoveries++ >= 2) throw new InvalidDataException();
                    offset = nextOffset;
                    progress?.Report(offset);
                }
            }
            throw new IOException("The upload session is no longer available.");
        }

        private long ReadStatus(JsonElement status, long size, UploadResumeRecord resume)
        {
            if (!status.TryGetProperty("nextExpectedRanges", out var ranges) || ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() == 0)
                throw new InvalidDataException();
            long first = long.MaxValue;
            foreach (var range in ranges.EnumerateArray())
            {
                string[] bounds = (range.GetString() ?? "").Split('-');
                if (bounds.Length != 2 || !long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
                    start < 0 || start >= size || start % (320 * 1024) != 0 ||
                    (bounds[1].Length > 0 && (!long.TryParse(bounds[1], out long end) || end < start || end >= size)))
                    throw new InvalidDataException();
                if (start < first)
                {
                    first = start;
                    _rangeEnd = bounds[1].Length == 0 ? size - 1 : long.Parse(bounds[1], CultureInfo.InvariantCulture);
                }
            }
            var expires = status.TryGetProperty("expirationDateTime", out var expiry) && expiry.TryGetDateTimeOffset(out var value) ? value : resume.Expires;
            Save(resume with { Bytes = first, Expires = expires, CommitPending = false });
            return first;
        }
    }
}
