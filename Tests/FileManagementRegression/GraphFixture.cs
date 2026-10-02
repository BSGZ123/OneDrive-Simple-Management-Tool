using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal sealed class GraphFixture : IDisposable
{
    public GraphHandler Handler { get; } = new();
    public DriveViewModel Drive { get; }
    private readonly GraphServiceClient _client;

    public GraphFixture(string driveId = "selected-drive")
    {
        _client = new GraphServiceClient(new HttpClient(Handler), new AnonymousAuthenticationProvider());
        var provider = new OneDrive(driveId, "test-account") { IsAuthenticated = true };
        typeof(OneDrive).GetField("graphClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(provider, _client);
        Drive = new DriveViewModel(provider);
    }

    public void Dispose() => _client.Dispose();
}

internal sealed class GraphHandler : HttpMessageHandler
{
    public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode MutationStatus { get; set; } = HttpStatusCode.OK;
    public Exception ListError { get; set; }
    public bool InvalidList { get; set; }
    public TaskCompletionSource MutationGate { get; set; }
    public List<(string Method, string Path, string Query, string Body)> Requests { get; } = [];
    public Dictionary<string, string> Names { get; } = new()
    {
        ["folder-id"] = "Folder",
        ["file-id"] = "report.docx",
        ["image-id"] = "photo.png"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, request.RequestUri.Query, body));
        if (path.EndsWith("/content"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-test")) };

        if (request.Method == HttpMethod.Get)
        {
            if (ListError != null) throw ListError;
            if (ListStatus != HttpStatusCode.OK) return Error(ListStatus);
            if (InvalidList) return Json("{}", HttpStatusCode.OK);
            var rows = path.Contains("/items/folder-id/")
                ? new[] { Item("child-id", "inside.txt") }
                : Names.Select(pair => Item(pair.Key, pair.Value)).ToArray();
            return Json(JsonSerializer.Serialize(new { value = rows }), HttpStatusCode.OK);
        }

        if (MutationGate != null) await MutationGate.Task.WaitAsync(cancellationToken);
        if (MutationStatus != HttpStatusCode.OK) return Error(MutationStatus);
        string id = path.Split('/').Last();
        if (request.Method == HttpMethod.Delete || path.EndsWith("/permanentDelete"))
        {
            if (path.EndsWith("/permanentDelete")) id = path.Split('/')[^2];
            Names.Remove(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        string name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
        if (path.EndsWith("/children")) id = "new-folder";
        Names[id] = name;
        return Json(JsonSerializer.Serialize(new { id, name }), HttpStatusCode.OK);
    }

    private static object Item(string id, string name) => new
    {
        id, name,
        folder = id is "folder-id" or "new-folder" ? new { childCount = 1 } : null,
        image = id == "image-id" ? new { width = 10, height = 10 } : null
    };

    private static HttpResponseMessage Error(HttpStatusCode status) => Json("{\"error\":{\"code\":\"testFailure\",\"message\":\"Local simulated failure\"}}", status);
    private static HttpResponseMessage Json(string value, HttpStatusCode status) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}
