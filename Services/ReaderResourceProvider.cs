using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed record ReaderResource(Stream Content, int Status, string Mime, long Length);
    public sealed class ReaderResourceProvider : IDisposable
    {
        public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; script-src-attr 'none'; style-src 'self' 'unsafe-inline' blob:; img-src blob:; font-src blob: data:; connect-src 'self'; frame-src blob:; worker-src 'none'; media-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        public static readonly string[] Assets = ["index.html", "reader.css", "bootstrap.js", "reader-adapter.js", "epub-loader.js", "resource-budget.js", "protocol.js",
            "foliate/view.js", "foliate/epub.js", "foliate/epubcfi.js", "foliate/progress.js", "foliate/overlayer.js", "foliate/text-walker.js",
            "foliate/paginator.js", "foliate/fixed-layout.js", "foliate/search.js", "foliate/tts.js", "foliate/vendor/zip.js"];
        private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
        private readonly List<Stream> _streams = new();
        private readonly object _gate = new();
        private readonly ReaderLocalBook _book;
        private readonly string _bookUrl;
        private bool _disposed;
        private int _bookRequests;
        public ReaderResourceProvider(ReaderLocalBook book, string session)
        {
            _book = book;
            _bookUrl = ReaderProtocol.Origin + "/book/" + session + ".epub";
        }
        public async Task PrepareAsync(string applicationDirectory, CancellationToken token)
        {
            foreach (string asset in Assets)
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(applicationDirectory, "Assets", "Reader", asset), token).ConfigureAwait(false);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _assets.Add(ReaderProtocol.Origin + "/reader/" + asset, bytes);
                }
            }
        }
        public async Task<ReaderResource> ResolveAsync(string url, string method, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Stream stream = null;
            string mime;
            lock (_gate)
            {
                if (_disposed || method != "GET") return new(null, 403, "text/plain", 0);
                if (_streams.Count >= 128) return new(null, 429, "text/plain", 0);
                if (_assets.TryGetValue(url, out var bytes))
                {
                    stream = new MemoryStream(bytes, false);
                    mime = url.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : url.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html";
                    _streams.Add(stream);
                    return new(stream, 200, mime, bytes.Length);
                }
                if (url != _bookUrl) return new(null, 403, "text/plain", 0);
                if (++_bookRequests > 2) return new(null, 429, "text/plain", 0);
            }
            // Only open a file owned by this immutable session, never a request-derived path.
            stream = await Task.Run(_book.OpenRead, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || token.IsCancellationRequested) { stream.Dispose(); token.ThrowIfCancellationRequested(); return new(null, 403, "text/plain", 0); }
                _streams.Add(stream);
            }
            return new(stream, 200, "application/epub+zip", _book.Length);
        }
        public static string Headers(ReaderResource resource) => "Content-Type: " + resource.Mime + "\r\nContent-Length: " + resource.Length
            + "\r\nContent-Security-Policy: " + ContentSecurityPolicy + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n";
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var stream in _streams) stream.Dispose();
                _streams.Clear(); _assets.Clear();
            }
        }
    }
}
