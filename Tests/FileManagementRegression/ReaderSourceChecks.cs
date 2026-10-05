using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;
internal static class ReaderSourceChecks
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Reader pins account/drive/item and uses cTag with eTag fallback", Identity),
        ("Reader rejects remote shortcuts, foreign drives, missing files and non-HTTPS URLs", Metadata),
        ("Reader maps permission, deletion and offline failures without switching accounts", Failures)
    ];
    private static async Task Identity()
    {
        using var f = new GraphFixture(); var provider = f.Drive.Provider;
        f.Handler.OnGet = (_, _) => Task.FromResult(Reply());
        var request = GraphReaderSource.Create(provider, "book-id");
        var source = await request.ResolveSource(default);
        Assert(request.Identity == new ReaderIdentity("onedrive", "test-account", "selected-drive", "book-id") && source.Version == "ctag:v1");
        Assert(f.Handler.Requests.Last().Path.EndsWith("/drives/selected-drive/items/book-id"));
        f.Handler.OnGet = (_, _) => Task.FromResult(Reply(ctag: null, name: "renamed.EPUB"));
        Assert((await request.ResolveSource(default)).Version == "etag:e1");
        provider.HomeAccountId = "other-account";
        int count = f.Handler.Requests.Count;
        await Reject(request, "AccessDenied"); Assert(count == f.Handler.Requests.Count);
    }
    private static async Task Metadata()
    {
        using var f = new GraphFixture(); var request = GraphReaderSource.Create(f.Drive.Provider, "book-id");
        foreach (var response in new[] { Reply(remote: new { id = "remote" }), Reply(drive: "foreign"), Reply(file: false), Reply(name: "not-epub.pdf"), Reply(url: "http://example.com/book"), Reply(id: "wrong-item") })
        {
            f.Handler.OnGet = (_, _) => Task.FromResult(response); await Reject(request, "InvalidBook");
        }
        f.Handler.OnGet = (_, _) => Task.FromResult(Reply(size: 100_000_001)); await Reject(request, "BookLimit");
    }
    private static async Task Failures()
    {
        using var f = new GraphFixture(); var request = GraphReaderSource.Create(f.Drive.Provider, "book-id");
        foreach (var (status, code) in new[] { (HttpStatusCode.Forbidden, "AccessDenied"), (HttpStatusCode.NotFound, "NotFound") })
        {
            f.Handler.ListStatus = status; await Reject(request, code);
        }
        f.Handler.ListStatus = HttpStatusCode.OK; f.Handler.ListError = new HttpRequestException("Simulated offline"); await Reject(request, "Network");
    }
    private static HttpResponseMessage Reply(string ctag = "v1", string name = "book.epub", object remote = null, string drive = "selected-drive",
        bool file = true, string url = "https://example.com/book", long size = 100, string id = "book-id") => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = name, ["file"] = file ? new { hashes = new { } } : null,
                ["remoteItem"] = remote, ["parentReference"] = new { driveId = drive }, ["size"] = size,
                ["cTag"] = ctag, ["eTag"] = "e1", ["@microsoft.graph.downloadUrl"] = url
            }), Encoding.UTF8, "application/json")
        };
    private static async Task Reject(ReaderOpenRequest request, string code)
    {
        try { await request.ResolveSource(default); throw new InvalidOperationException("Reader source accepted invalid metadata"); }
        catch (ReaderException error) { Assert(error.Code == code); }
    }
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Reader source assertion failed"); }
}
