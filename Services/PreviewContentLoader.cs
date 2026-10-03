using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class PreviewContentLoader
    {
        private readonly PreviewKind _kind;
        private readonly Func<CancellationToken, Task<PreviewMetadata>> _metadata;
        private readonly Func<CancellationToken, Task<Stream>> _content;
        private readonly PreviewOptions _options;

        public PreviewContentLoader(PreviewKind kind, Func<CancellationToken, Task<PreviewMetadata>> metadata,
            Func<CancellationToken, Task<Stream>> content, PreviewOptions options = null)
        {
            _kind = kind;
            _metadata = metadata;
            _content = content;
            _options = options ?? new();
        }

        public async Task<PreviewContent> LoadAsync(CancellationToken token)
        {
            PreviewMetadata metadata = await WithTimeoutAsync(_metadata, _options.RequestTimeout, token);
            if (metadata == null || metadata.Size < 0) throw new PreviewException(PreviewFailure.InvalidContent);
            if (_kind is PreviewKind.Pdf or PreviewKind.Media)
            {
                if (metadata.Size == 0) throw new PreviewException(PreviewFailure.InvalidContent);
                if (!Uri.TryCreate(metadata.Url, UriKind.Absolute, out Uri uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new PreviewException(PreviewFailure.InvalidContent);
                return new(_kind, Uri: uri);
            }

            int limit = _kind == PreviewKind.Markdown ? _options.MaxTextBytes : _options.MaxImageBytes;
            if (metadata.Size > limit) throw new PreviewException(PreviewFailure.TooLarge);
            using Stream stream = await WithTimeoutAsync(_content, _options.RequestTimeout, token);
            if (stream == null) throw new PreviewException(PreviewFailure.InvalidContent);
            byte[] bytes = await ReadLimitedAsync(stream, limit, _options.ReadTimeout, token);
            if (metadata.Size.HasValue && bytes.LongLength != metadata.Size.Value)
                throw new PreviewException(PreviewFailure.InvalidContent);
            if (_kind == PreviewKind.Image)
            {
                if (bytes.Length == 0) throw new PreviewException(PreviewFailure.InvalidContent);
                return new(_kind, Bytes: bytes);
            }
            try
            {
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
                return new(_kind, Text: await reader.ReadToEndAsync(token));
            }
            catch (DecoderFallbackException exception)
            {
                throw new PreviewException(PreviewFailure.InvalidContent, inner: exception);
            }
        }

        public static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, TimeSpan timeout, CancellationToken token)
        {
            using var memory = new MemoryStream();
            byte[] buffer = new byte[Math.Min(64 * 1024, limit + 1)];
            while (true)
            {
                // One extra byte at the boundary detects a dishonest or missing Content-Length.
                int count = Math.Min(buffer.Length, limit - (int)memory.Length + 1);
                int read = await WithTimeoutAsync(ct => stream.ReadAsync(buffer.AsMemory(0, count), ct).AsTask(), timeout, token);
                if (read == 0) return memory.ToArray();
                if (memory.Length + read > limit) throw new PreviewException(PreviewFailure.TooLarge);
                memory.Write(buffer, 0, read);
            }
        }

        internal static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation,
            TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            Task<T> pending = operation(bounded.Token);
            try { return await pending.WaitAsync(bounded.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _ = DisposeLateResultAsync(pending);
                throw new PreviewException(PreviewFailure.Timeout, true);
            }
            catch (OperationCanceledException)
            {
                _ = DisposeLateResultAsync(pending);
                throw;
            }
        }

        private static async Task DisposeLateResultAsync<T>(Task<T> pending)
        {
            try { if (await pending.ConfigureAwait(false) is IDisposable result) result.Dispose(); }
            catch { /* Observe an abandoned operation and dispose any stream returned after closing. */ }
        }
    }
}
