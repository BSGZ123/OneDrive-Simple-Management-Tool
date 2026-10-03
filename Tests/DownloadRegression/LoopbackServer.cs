using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = new();
    private readonly Task _accept;
    public byte[] Bytes { get; set; } = Enumerable.Range(0, 1024 * 512).Select(i => (byte)(i * 31 + i / 251)).ToArray();
    public int DelayMilliseconds { get; set; } = 3;
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/octet-stream";
    public bool Attachment { get; set; }
    public int DropsRemaining;
    public bool SupportsRange { get; set; } = true;
    public bool IgnoreRange { get; set; }
    public bool RejectRange { get; set; }
    public bool BadContentRange { get; set; }
    public bool OmitLength { get; set; }
    public bool StallBody { get; set; }
    public ConcurrentQueue<long> Offsets { get; } = new();
    public ConcurrentQueue<string> Paths { get; } = new();
    public int DownloadRequests => Offsets.Count;
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file";

    public LoopbackServer()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string request = await reader.ReadLineAsync(_stop.Token);
                if (request == null) return;
                string range = null;
                string line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
                bool head = request.StartsWith("HEAD ");
                long start = 0;
                long end = Bytes.Length - 1;
                if (range != null)
                {
                    string[] values = range[6..].Split('-');
                    start = long.Parse(values[0]);
                    if (!string.IsNullOrEmpty(values[1])) end = Math.Min(long.Parse(values[1]), end);
                }
                byte[] data = Bytes;
                int status = StatusCode;
                bool resumed = range != null && start > 0;
                if (status == 200 && range != null && !IgnoreRange && SupportsRange)
                    status = resumed && RejectRange ? 416 : 206;
                if (status == 200) { start = 0; end = data.Length - 1; }
                bool success = status is 200 or 206;
                long length = success ? Math.Max(0, end - start + 1) : 0;
                var headers = new StringBuilder($"HTTP/1.1 {status} Test\r\nConnection: close\r\nContent-Type: {ContentType}\r\n");
                if (Attachment) headers.Append("Content-Disposition: attachment; filename=sample.pdf\r\n");
                if (!OmitLength || !success) headers.Append($"Content-Length: {length}\r\n");
                headers.Append($"Accept-Ranges: {(SupportsRange ? "bytes" : "none")}\r\n");
                if (status == 206)
                    headers.Append($"Content-Range: bytes {start + (resumed && BadContentRange ? 1 : 0)}-{end}/{data.Length}\r\n");
                headers.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _stop.Token);
                if (head || !success) return;
                Offsets.Enqueue(start);
                Paths.Enqueue(request.Split(' ')[1]);
                if (StallBody) await Task.Delay(Timeout.Infinite, _stop.Token);
                bool drop = DropsRemaining > 0 && length > 65536;
                if (drop) Interlocked.Decrement(ref DropsRemaining);
                long limit = drop ? Math.Min(end + 1, start + 65536) : end + 1;
                for (long offset = start; offset < limit; offset += 8192)
                {
                    int count = (int)Math.Min(8192, limit - offset);
                    await stream.WriteAsync(data.AsMemory((int)offset, count), _stop.Token);
                    if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, _stop.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients);
        _stop.Dispose();
    }
}
