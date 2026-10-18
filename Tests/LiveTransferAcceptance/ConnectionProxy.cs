using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Opt-in, loopback-only CONNECT tunnel. TLS stays end to end; no certificates or
// tokens are intercepted. Only a child launched with this proxy is affected.
internal sealed class ConnectionProxy(string directory)
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<long, (TcpClient Client, TcpClient Remote)> _connections = new();
    private volatile bool _offline;
    private long _sequence;
    private long _sent;
    private long _received;
    private long _failures;
    private readonly ConcurrentDictionary<long, Task> _workers = new();

    public async Task RunAsync()
    {
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        await File.WriteAllTextAsync(Path.Combine(directory, "proxy-port.txt"), port.ToString());
        Console.WriteLine($"Loopback CONNECT proxy listening on port {port}.");
        using var stopping = new CancellationTokenSource();
        var watch = WatchAsync(stopping.Token);
        try
        {
            while (!File.Exists(Path.Combine(directory, "stop-proxy")))
            {
                if (!_listener.Pending()) { await Task.Delay(50); continue; }
                var client = await _listener.AcceptTcpClientAsync();
                long id = Interlocked.Increment(ref _sequence);
                _workers[id] = HandleAsync(id, client);
                foreach (var finished in _workers.Where(x => x.Value.IsCompleted).ToArray())
                    if (_workers.TryRemove(finished.Key, out var task)) await task;
            }
        }
        finally
        {
            stopping.Cancel();
            _listener.Stop();
            CloseConnections();
            await Task.WhenAll(_workers.Values);
            try { await watch; } catch (OperationCanceledException) { }
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        while (true)
        {
            bool offline = File.Exists(Path.Combine(directory, "offline"));
            if (offline != _offline)
            {
                _offline = offline;
                if (offline) CloseConnections();
                Console.WriteLine(offline ? "OFFLINE: existing tunnels closed; new connections rejected." : "ONLINE: connections restored.");
            }
            var status = new { Offline = _offline, Connections = _connections.Count, Sent = Interlocked.Read(ref _sent),
                Received = Interlocked.Read(ref _received), Failures = Interlocked.Read(ref _failures) };
            await File.WriteAllTextAsync(Path.Combine(directory, "proxy-status.json"), JsonSerializer.Serialize(status), token);
            await Task.Delay(200, token);
        }
    }

    private void CloseConnections()
    {
        foreach (var pair in _connections.Values) { pair.Client.Dispose(); pair.Remote.Dispose(); }
    }

    private async Task HandleAsync(long id, TcpClient client)
    {
        using (client)
        using (var remote = new TcpClient { NoDelay = true })
        {
            _connections[id] = (client, remote);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                client.NoDelay = true;
                var incoming = client.GetStream();
                string header = await ReadHeaderAsync(incoming, timeout.Token);
                var first = header.Split("\r\n", 2)[0].Split(' ');
                if (first.Length != 3 || first[0] != "CONNECT" || !Uri.TryCreate("https://" + first[1], UriKind.Absolute, out var target) ||
                    target.Port != 443 || target.UserInfo.Length != 0 || _offline)
                {
                    await incoming.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                    return;
                }
                // Preserve an existing system/HTTP proxy upstream when present.
                var upstream = HttpClient.DefaultProxy.IsBypassed(target) ? target : HttpClient.DefaultProxy.GetProxy(target);
                bool viaProxy = upstream != null && upstream != target;
                await remote.ConnectAsync(viaProxy ? upstream.Host : target.Host, viaProxy ? upstream.Port : target.Port, timeout.Token);
                var outgoing = remote.GetStream();
                if (viaProxy)
                {
                    if (upstream.Scheme != "http") throw new IOException("Unsupported upstream proxy type.");
                    await outgoing.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target.Host}:443 HTTP/1.1\r\nHost: {target.Host}:443\r\n\r\n"), timeout.Token);
                    string response = await ReadHeaderAsync(outgoing, timeout.Token);
                    if (!response.StartsWith("HTTP/1.1 200") && !response.StartsWith("HTTP/1.0 200")) throw new IOException("Upstream proxy refused tunnel.");
                }
                if (_offline) return;
                await incoming.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
                var send = CopyAsync(incoming, outgoing, true);
                var receive = CopyAsync(outgoing, incoming, false);
                await Task.WhenAny(send, receive);
                client.Dispose(); remote.Dispose();
                await Task.WhenAll(send, receive);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                Interlocked.Increment(ref _failures);
            }
            finally { _connections.TryRemove(id, out _); }
        }
    }

    private async Task CopyAsync(Stream source, Stream destination, bool sending)
    {
        byte[] buffer = new byte[65536];
        while (true)
        {
            int count = await source.ReadAsync(buffer);
            if (count == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, count));
            if (sending) Interlocked.Add(ref _sent, count); else Interlocked.Add(ref _received, count);
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[8192];
        for (int count = 0; count < bytes.Length; count++)
        {
            if (await stream.ReadAsync(bytes.AsMemory(count, 1), token) == 0) throw new IOException();
            if (count >= 3 && bytes[count - 3] == 13 && bytes[count - 2] == 10 && bytes[count - 1] == 13 && bytes[count] == 10)
                return Encoding.ASCII.GetString(bytes, 0, count + 1);
        }
        throw new IOException("Proxy request header exceeded its bound.");
    }
}
