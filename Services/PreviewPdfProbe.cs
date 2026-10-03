using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class PreviewPdfProbe
    {
        private static readonly HttpClient Client = new(new SocketsHttpHandler { UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // Check only the header. The native viewer remains responsible for PDF parsing and password prompts.
        public static async Task ValidateAsync(Uri uri, PreviewOptions options, CancellationToken token, HttpClient client = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            using var response = await PreviewContentLoader.WithTimeoutAsync(
                ct => (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct),
                options.RequestTimeout, token);
            int status = (int)response.StatusCode;
            if (status is 401 or 403) throw new PreviewException(PreviewFailure.AccessDenied, true);
            if (status == 404) throw new PreviewException(PreviewFailure.NotFound);
            if (!response.IsSuccessStatusCode) throw new PreviewException(PreviewFailure.Network, status == 429 || status >= 500);
            using var stream = await response.Content.ReadAsStreamAsync(token);
            byte[] prefix = new byte[1024];
            int count = 0;
            while (count < prefix.Length)
            {
                int read = await PreviewContentLoader.WithTimeoutAsync(
                    ct => stream.ReadAsync(prefix.AsMemory(count), ct).AsTask(), options.ReadTimeout, token);
                if (read == 0) break;
                count += read;
                if (Encoding.ASCII.GetString(prefix, 0, count).Contains("%PDF-", StringComparison.Ordinal)) return;
            }
            throw new PreviewException(PreviewFailure.InvalidContent);
        }
    }
}
