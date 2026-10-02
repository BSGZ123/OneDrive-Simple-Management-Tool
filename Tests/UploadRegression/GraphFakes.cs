using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Services;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal sealed class UploadFixture : IAsyncDisposable
{
    public LoopbackUploadServer Server { get; } = new();
    public MetadataHandler Metadata { get; }
    public OneDrive Provider { get; }
    private readonly GraphServiceClient _client;

    public UploadFixture()
    {
        Metadata = new MetadataHandler(Server.Url);
        _client = new GraphServiceClient(new HttpClient(Metadata), new TestAuthenticationProvider());
        Provider = new OneDrive("test-drive", "test-account");
        typeof(OneDrive).GetField("graphClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Provider, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Server.DisposeAsync();
    }

    private sealed class TestAuthenticationProvider : IAuthenticationProvider
    {
        public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object> additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            // This literal is deliberately not a real credential. The upload server
            // must never receive it, even though metadata requests do.
            request.Headers.Add("Authorization", "Bearer local-test-token");
            return Task.CompletedTask;
        }
    }
}

internal sealed class MetadataHandler(string uploadUrl) : HttpMessageHandler
{
    public HttpStatusCode SessionStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode FolderStatus { get; set; } = HttpStatusCode.Created;
    public HttpStatusCode EmptyFileStatus { get; set; } = HttpStatusCode.Created;
    public string RejectFileName { get; set; }
    public bool InvalidSession { get; set; }
    public int Sessions;
    public int Folders;
    public int EmptyFiles;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != "local-test-token")
        {
            throw new InvalidOperationException("Metadata requests must use the authenticated Graph client.");
        }

        string path = request.RequestUri!.AbsolutePath;
        HttpStatusCode status;
        object body;
        if (path.EndsWith("createUploadSession"))
        {
            Interlocked.Increment(ref Sessions);
            status = RejectFileName != null && path.Contains(RejectFileName) ? HttpStatusCode.Forbidden : SessionStatus;
            body = InvalidSession ? new { uploadUrl = "" } : (object)new
            {
                uploadUrl,
                expirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
                nextExpectedRanges = new[] { "0-" }
            };
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("children"))
        {
            int id = Interlocked.Increment(ref Folders);
            status = FolderStatus;
            body = new { id = $"folder-{id}" };
        }
        else if (request.Method == HttpMethod.Put && path.EndsWith("content"))
        {
            Interlocked.Increment(ref EmptyFiles);
            if ((await request.Content!.ReadAsByteArrayAsync(cancellationToken)).Length != 0)
            {
                throw new InvalidOperationException("Only empty files should use this test's simple upload endpoint.");
            }
            status = EmptyFileStatus;
            body = new { id = "empty-item" };
        }
        else
        {
            throw new InvalidOperationException($"Unexpected metadata request: {request.Method} {path}");
        }

        if ((int)status >= 400)
        {
            body = new { error = new { code = "accessDenied", message = "Simulated metadata rejection." } };
        }

        return new HttpResponseMessage(status)
        {
            Content = status == HttpStatusCode.NoContent ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}

// Real SDK HTTP requests reach only this loopback server. Metadata never leaves
// the in-memory handler above, so no account or cloud data is needed for these tests.
internal sealed class LoopbackUploadServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Task _acceptLoop;
    public ConcurrentQueue<Dictionary<string, string>> Headers { get; } = new();
    public int StatusCode { get; set; } = 200;
    public bool OmitItemId { get; set; }
    public string Url { get; }

    public LoopbackUploadServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/upload";
        _acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _requests.Add(RespondAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var headerBytes = new List<byte>();
            byte[] single = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(single, _stop.Token) == 0)
                {
                    throw new IOException("Incomplete upload request headers.");
                }
                headerBytes.Add(single[0]);
                int n = headerBytes.Count;
                if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10)
                {
                    break;
                }
                if (n > 65536) throw new IOException("Unexpectedly large HTTP headers.");
            }

            var headers = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n")
                .Skip(1).Where(line => line.Contains(':'))
                .Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            Headers.Enqueue(headers);

            int remaining = int.Parse(headers["Content-Length"]);
            byte[] buffer = new byte[32768];
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), _stop.Token);
                if (read == 0) throw new IOException("Incomplete upload request body.");
                remaining -= read;
            }

            string[] range = headers["Content-Range"].Replace("bytes ", "").Split('-', '/');
            long next = long.Parse(range[1]) + 1;
            bool last = next == long.Parse(range[2]);
            int status = StatusCode >= 400 ? StatusCode : last ? 201 : 202;
            object response = status >= 400
                ? new { error = new { code = "accessDenied", message = "Simulated slice rejection." } }
                : last ? OmitItemId ? new { name = "missing-id" } : (object)new { id = "uploaded-item" }
                : new { nextExpectedRanges = new[] { $"{next}-" }, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(response);
            byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_requests);
        _stop.Dispose();
    }
}
