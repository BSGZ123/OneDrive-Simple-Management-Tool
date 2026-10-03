using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class SliceServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public ConcurrentQueue<Dictionary<string, string>> Headers { get; } = new();
    public string Url { get; }
    public string Name { get; set; } = "large.bin";
    public bool Reject { get; set; }
    public SliceServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/upload";
        _loop = RespondAsync();
    }

    private async Task RespondAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var bytes = new List<byte>();
                byte[] single = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(single, _stop.Token) == 0) throw new IOException();
                    bytes.Add(single[0]);
                    int n = bytes.Count;
                    if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                    if (n > 65536) throw new IOException();
                }
                var headers = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n").Skip(1).Where(line => line.Contains(':'))
                    .Select(line => line.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                Headers.Enqueue(headers);
                int remaining = int.Parse(headers["Content-Length"]);
                byte[] buffer = new byte[32768];
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), _stop.Token);
                    if (read == 0) throw new IOException();
                    remaining -= read;
                }
                string[] range = headers["Content-Range"].Replace("bytes ", "").Split('-', '/');
                long next = long.Parse(range[1]) + 1, length = long.Parse(range[2]);
                bool last = next == length;
                int status = Reject ? 403 : last ? 201 : 202;
                object response = Reject ? new { error = new { code = "accessDenied", message = "Local test" } } :
                    last ? (object)new { id = "uploaded", name = Name, size = length, file = new { } } :
                    new { nextExpectedRanges = new[] { $"{next}-" }, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) };
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
