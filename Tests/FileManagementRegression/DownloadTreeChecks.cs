using OneDrive_Simple_Management_Tool.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class DownloadTreeChecks
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Download tree retains folder identity, empty pages and continuation tokens", Paging),
        ("Download continuation cannot leave the selected folder, drive or Graph host", ContinuationScope),
        ("Download tree rejects deleted, shared, malformed and foreign items", InvalidItems),
        ("Download metadata is revalidated at transfer time and failures are classified", TransferMetadata)
    ];

    private static async Task Paging()
    {
        using var f = new GraphFixture();
        var provider = f.Drive.Provider;
        const string next = "https://graph.microsoft.com/v1.0/drives/selected-drive/items/folder/children?$skiptoken=opaque%2Bvalue";
        f.Handler.OnGet = (request, _) => Task.FromResult(!request.RequestUri!.AbsolutePath.EndsWith("/children")
            ? Reply(Item("folder", true))
            : Reply(new Dictionary<string, object> { ["value"] = request.RequestUri.Query.Contains("skiptoken")
                ? new[] { Item("child", parent: "folder") } : [], ["@odata.nextLink"] = request.RequestUri.Query.Contains("skiptoken") ? null : next }));
        var folder = await provider.GetDownloadItemAsync("folder", default);
        Check(folder.IsFolder && folder.ParentId == "root" && folder.Name == "folder", "Folder identity changed.");
        var first = await provider.GetDownloadChildrenAsync("folder", null, default);
        Check(first.Items.Count == 0 && first.NextLink == next, "Empty intermediate page lost continuation.");
        var second = await provider.GetDownloadChildrenAsync("folder", first.NextLink, default);
        Check(second.Items.Single().Id == "child" && second.NextLink == null &&
            f.Handler.Requests.Last().Query.Contains("opaque%2Bvalue"), "Continuation token or child was changed.");
    }

    private static async Task ContinuationScope()
    {
        using var f = new GraphFixture();
        foreach (string link in new[]
        {
            "https://example.com/v1.0/drives/selected-drive/items/folder/children",
            "https://graph.microsoft.com/v1.0/drives/other/items/folder/children",
            "https://graph.microsoft.com/v1.0/drives/selected-drive/items/other/children",
            "https://graph.microsoft.com/v1.0/drives/selected-drive/items/folder/content",
            "https://graph.microsoft.com/v1.0/drives/selected-drive/items/folder/children#fragment",
            "https://user@graph.microsoft.com/v1.0/drives/selected-drive/items/folder/children"
        })
        {
            await Reject(() => f.Drive.Provider.GetDownloadChildrenAsync("folder", link, default));
            Check(f.Handler.Requests.Count == 0, "An untrusted continuation sent a request.");
        }
    }

    private static async Task InvalidItems()
    {
        using var f = new GraphFixture();
        foreach (string invalid in new[] { "foreign", "remote", "deleted", "missing-type", "both-types", "negative", "missing-parent", "wrong-parent", "wrong-id" })
        {
            var item = Item("child", parent: "folder");
            switch (invalid)
            {
                case "foreign": item["parentReference"] = new { id = "folder", driveId = "foreign" }; break;
                case "remote": item["remoteItem"] = new { id = "shortcut" }; break;
                case "deleted": item["deleted"] = new { state = "deleted" }; break;
                case "missing-type": item.Remove("file"); break;
                case "both-types": item["folder"] = new { }; break;
                case "negative": item["size"] = -1; break;
                case "missing-parent": item.Remove("parentReference"); break;
                case "wrong-parent": item["parentReference"] = new { id = "other", driveId = "selected-drive" }; break;
                case "wrong-id": item["id"] = "other"; break;
            }
            f.Handler.OnGet = (_, _) => Task.FromResult(Reply(item));
            if (invalid != "wrong-parent") await Reject(() => f.Drive.Provider.GetDownloadItemAsync("child", default));
            if (invalid != "wrong-id")
            {
                f.Handler.OnGet = (_, _) => Task.FromResult(Reply(new { value = new[] { item } }));
                await Reject(() => f.Drive.Provider.GetDownloadChildrenAsync("folder", null, default));
            }
        }
        var root = Item("root", true); root.Remove("parentReference"); root["root"] = new { };
        f.Handler.OnGet = (_, _) => Task.FromResult(Reply(root));
        Check((await f.Drive.Provider.GetDownloadItemAsync("root", default)).IsRoot, "Root without parent was rejected.");
    }

    private static async Task TransferMetadata()
    {
        using var f = new GraphFixture();
        var item = Item("child");
        item["@microsoft.graph.downloadUrl"] = "https://example.com/local-fixture";
        item["cTag"] = "v1";
        f.Handler.OnGet = (_, _) => Task.FromResult(Reply(item));
        Check((await f.Drive.Provider.GetDownloadSourceAsync("child", default)).Version == "v1", "Transfer metadata failed.");
        item["remoteItem"] = new { id = "moved" };
        await Reject(() => f.Drive.Provider.GetDownloadSourceAsync("child", default), DownloadFailure.InvalidResponse);
        foreach (var (status, failure) in new[] { (HttpStatusCode.Forbidden, DownloadFailure.AccessDenied), (HttpStatusCode.NotFound, DownloadFailure.NotFound) })
        {
            f.Handler.OnGet = null; f.Handler.ListStatus = status;
            await Reject(() => f.Drive.Provider.GetDownloadItemAsync("child", default), failure);
            await Reject(() => f.Drive.Provider.GetDownloadChildrenAsync("folder", null, default), failure);
            await Reject(() => f.Drive.Provider.GetDownloadSourceAsync("child", default), failure);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        f.Handler.ListStatus = HttpStatusCode.OK;
        try { await f.Drive.Provider.GetDownloadChildrenAsync("folder", null, cancellation.Token); throw new Exception("Cancelled scan was accepted."); }
        catch (OperationCanceledException) { }
    }

    private static Dictionary<string, object> Item(string id, bool folder = false, string parent = "root") => new()
    {
        ["id"] = id, ["name"] = id, ["size"] = 123L,
        [folder ? "folder" : "file"] = new { },
        ["parentReference"] = new { id = parent, driveId = "selected-drive" }
    };
    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private static async Task Reject(Func<Task> action, DownloadFailure? expected = null)
    {
        try { await action(); throw new Exception("Invalid download metadata accepted."); }
        catch (DownloadFailureException error) { Check(expected == null || expected == error.Failure, "Wrong failure category."); }
        catch (InvalidDataException) when (expected == null) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
