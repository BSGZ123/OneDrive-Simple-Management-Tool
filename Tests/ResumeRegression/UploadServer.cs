using OneDrive_Simple_Management_Tool.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal sealed class UploadServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly MemoryStream _bytes = new();
    private int _session;
    public string Url { get; }
    public int Creates;
    public long Received => _bytes.Length;
    public bool ExpireCurrent;
    public bool RejectNextRange;
    public bool SawAuthorization;
    public byte[] CompletedBytes = [];
    public ConcurrentQueue<long> Offsets = new();
    public UploadServer()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Url = $"http://127.0.0.1:{number}/";
        _listener.Prefixes.Add(Url); _listener.Start();
        _loop = Run();
    }
    private async Task Run()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                try
                {
                    var request = context.Request;
                    if (request.Headers["Authorization"] != null) SawAuthorization = true;
                    if (request.Url!.AbsolutePath == "/create")
                    {
                        Creates++; _session++; _bytes.SetLength(0); ExpireCurrent = false;
                        await Json(context, 200, new UploadSessionInfo(Url + "session/" + _session, DateTimeOffset.UtcNow.AddHours(1)));
                    }
                    else if (ExpireCurrent || request.Url.AbsolutePath != "/session/" + _session) context.Response.StatusCode = 404;
                    else if (request.HttpMethod == "DELETE") { ExpireCurrent = true; context.Response.StatusCode = 204; }
                    else if (request.HttpMethod == "GET") await Status(context, 200);
                    else
                    {
                        string[] range = request.Headers["Content-Range"]![6..].Split('-', '/');
                        long start = long.Parse(range[0]), end = long.Parse(range[1]), size = long.Parse(range[2]);
                        Offsets.Enqueue(start);
                        if (RejectNextRange || start != _bytes.Length)
                        {
                            RejectNextRange = false;
                            context.Response.StatusCode = 416;
                        }
                        else
                        {
                            await request.InputStream.CopyToAsync(_bytes);
                            if (_bytes.Length != end + 1) throw new Exception("Invalid slice length.");
                            if (_bytes.Length == size)
                            {
                                CompletedBytes = _bytes.ToArray();
                                await Json(context, 201, new { id = "uploaded-item", name = "data.bin", size });
                            }
                            else await Status(context, 202);
                        }
                    }
                }
                finally { context.Response.Close(); }
            }
        }
        catch (Exception exception) when (!_listener.IsListening && exception is HttpListenerException or ObjectDisposedException) { }
    }
    private Task Status(HttpListenerContext context, int status) => Json(context, status,
        new { expirationDateTime = DateTimeOffset.UtcNow.AddHours(1), nextExpectedRanges = new[] { _bytes.Length + "-" } });
    private static async Task Json(HttpListenerContext context, int status, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
    public async ValueTask DisposeAsync() { _listener.Stop(); await _loop; _listener.Close(); _bytes.Dispose(); }
}
