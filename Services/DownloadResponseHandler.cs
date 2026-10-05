using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    internal sealed class DownloadRangeException : IOException { }

    // Downloader does not validate a resumed response's Content-Range before writing it.
    internal sealed class DownloadResponseHandler : DelegatingHandler
    {
        private readonly long _expectedSize;

        public DownloadResponseHandler(long expectedSize)
            : base(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(15)
            }) => _expectedSize = expectedSize;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range != null && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new DownloadRangeException();

                if (!response.IsSuccessStatusCode || request.Method == HttpMethod.Head)
                    return response;

                if (response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
                    throw new InvalidDataException("Encoded download responses are not byte-addressable.");

                if (range != null && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var actual = response.Content.Headers.ContentRange;
                    if (actual?.Unit != "bytes" || actual?.From != range.From || actual?.To != range.To ||
                        actual?.Length != _expectedSize)
                        throw new DownloadRangeException();
                }
                else if (range?.From > 0)
                {
                    throw new DownloadRangeException();
                }
                else if (range == null && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    throw new DownloadRangeException();
                }
                else if (response.StatusCode == HttpStatusCode.OK &&
                    response.Content.Headers.ContentLength is long length && length != _expectedSize)
                {
                    throw new InvalidDataException("The response length differs from the remote file size.");
                }

                long maximum = response.Content.Headers.ContentRange is { From: long from, To: long to } ? to - from + 1 : _expectedSize;
                response.Content = new BoundedContent(response.Content, maximum);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        // Enforce metadata size while streaming, including chunked responses. A final length
        // check alone would allow an overlong server response to exhaust the destination disk.
        private sealed class BoundedContent : HttpContent
        {
            private readonly HttpContent _inner;
            private readonly long _maximum;
            public BoundedContent(HttpContent inner, long maximum)
            {
                _inner = inner; _maximum = maximum;
                foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
            protected override async Task<Stream> CreateContentReadStreamAsync() => new BoundedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _maximum);
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                using var input = await CreateContentReadStreamAsync().ConfigureAwait(false);
                await input.CopyToAsync(stream).ConfigureAwait(false);
            }
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
        private sealed class BoundedStream(Stream inner, long maximum) : Stream
        {
            private long _received;
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            private int Accept(int count) { _received += count; if (_received > maximum) throw new InvalidDataException("Download body exceeds metadata size."); return count; }
            private int ReadSize(int requested) => (int)Math.Min(requested, Math.Max(1, maximum - _received + 1));
            public override int Read(byte[] buffer, int offset, int count) => Accept(inner.Read(buffer, offset, ReadSize(count)));
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Accept(await inner.ReadAsync(buffer[..ReadSize(buffer.Length)], cancellationToken).ConfigureAwait(false));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
    }
}
