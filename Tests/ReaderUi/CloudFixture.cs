using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ReaderCloudFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _bytes;
    private readonly List<Task> _clients = new();
    private readonly Task _loop;
    public int Gets;
    public string Version = "ctag:native-v1", Failure;
    public ReaderOpenRequest Request { get; }
    public ReaderCloudFixture(string path)
    {
        _bytes = File.ReadAllBytes(path); _listener.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/book";
        Request = new(Identity: new("onedrive", "native-test-account", "native-test-drive", "native-test-item"),
            ResolveSource: token => { token.ThrowIfCancellationRequested(); if (Failure != null) throw new ReaderException(Failure); return Task.FromResult(new DownloadSource(url, _bytes.Length, Version)); });
        _loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client) try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string request = await reader.ReadLineAsync(_stop.Token); string header;
            do { header = await reader.ReadLineAsync(_stop.Token); } while (!string.IsNullOrEmpty(header));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {_bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
            if (!request.StartsWith("HEAD ", StringComparison.Ordinal)) { Interlocked.Increment(ref Gets); await stream.WriteAsync(_bytes, _stop.Token); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_clients); _stop.Dispose(); }
}
