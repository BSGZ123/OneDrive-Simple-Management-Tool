using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    internal sealed class ThrottledUploadContent(byte[] buffer, int count, long offset, TransferPolicy policy,
        IProgress<long> progress, CancellationTokenSource timeout) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = count; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
            SerializeToStreamAsync(stream, context, timeout.Token);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken token)
        {
            int sent = 0;
            var timer = Stopwatch.StartNew();
            long reportedAt = 0;
            while (sent < count)
            {
                int block = policy.BlockSize(count - sent);
                // Deliberate throttling must not consume the network inactivity timeout.
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await policy.WaitAsync(block, token).ConfigureAwait(false);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await stream.WriteAsync(buffer.AsMemory(sent, block), token).ConfigureAwait(false);
                sent += block;
                policy.ReportBytes(block);
                if (sent == count || timer.ElapsedMilliseconds - reportedAt >= 100)
                {
                    progress?.Report(offset + sent);
                    reportedAt = timer.ElapsedMilliseconds;
                }
            }
        }
    }
}
