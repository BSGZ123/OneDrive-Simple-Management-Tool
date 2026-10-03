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

                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
