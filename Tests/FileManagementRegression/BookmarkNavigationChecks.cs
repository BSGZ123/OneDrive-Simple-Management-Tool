using OneDrive_Simple_Management_Tool.Helpers;
using System.Net;
using System.Text;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class BookmarkNavigationChecks
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Bookmark file location resolves breadcrumbs and selects a later-page item", LaterPage),
        ("Bookmark folder opens its contents without selecting an unrelated file", Folder),
        ("Initial bookmark listing failure keeps the intended location for refresh", Retry),
        ("A file moved again after resolution reports missing selection", Missing),
        ("Superseded bookmark navigation cannot overwrite a new view", Superseded)
    ];

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Metadata(HttpRequestMessage request)
    {
        string path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/root")) return Json("{\"id\":\"root-id\",\"folder\":{}}");
        if (path.EndsWith("/items/parent")) return Json("{\"id\":\"parent\",\"name\":\"Moved folder\",\"folder\":{},\"parentReference\":{\"id\":\"root-id\",\"driveId\":\"selected-drive\"}}");
        return null;
    }
    private static async Task LaterPage()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.OnGet = (request, _) => Task.FromResult(Metadata(request) ?? Json(request.RequestUri.Query.Contains("cursor")
            ? "{\"value\":[{\"id\":\"target\",\"name\":\"renamed.txt\",\"file\":{}}]}"
            : "{\"value\":[{\"id\":\"other\",\"name\":\"other.txt\",\"file\":{}}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/drives/selected-drive/items/parent/children?cursor=2\"}"));
        await fixture.Drive.OpenLocationAsync("parent", "target");
        Check(fixture.Drive.SelectedItem?.Id == "target" && fixture.Drive.ParentItemId == "parent" && fixture.Drive.IsComplete);
        Check(fixture.Drive.BreadcrumbItems.Select(b => b.ItemId).SequenceEqual(new[] { "Root", "parent" }));
        Check(!fixture.Drive.HasError);
    }
    private static async Task Folder()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.OnGet = (request, _) => Task.FromResult(Metadata(request) ?? Json("{\"value\":[{\"id\":\"child\",\"name\":\"child.txt\",\"file\":{}}]}"));
        await fixture.Drive.OpenLocationAsync("parent", null);
        Check(fixture.Drive.ParentItemId == "parent" && fixture.Drive.SelectedItem == null && fixture.Drive.Files.Count == 1);
    }
    private static async Task Retry()
    {
        using var fixture = new GraphFixture();
        bool fail = true;
        fixture.Handler.OnGet = (request, _) => Task.FromResult(Metadata(request) ?? (fail
            ? Json("{\"error\":{\"code\":\"denied\",\"message\":\"test\"}}", HttpStatusCode.Forbidden)
            : Json("{\"value\":[{\"id\":\"target\",\"name\":\"file.txt\",\"file\":{}}]}")));
        await fixture.Drive.OpenLocationAsync("parent", "target");
        Check(fixture.Drive.HasError && fixture.Drive.CanRetry);
        fail = false;
        await fixture.Drive.TryRefresh();
        Check(fixture.Drive.ParentItemId == "parent" && fixture.Drive.SelectedItem?.Id == "target" && !fixture.Drive.HasError);
    }
    private static async Task Missing()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.OnGet = (request, _) => Task.FromResult(Metadata(request) ?? Json("{\"value\":[]}"));
        await fixture.Drive.OpenLocationAsync("parent", "target");
        Check(fixture.Drive.IsComplete && fixture.Drive.ErrorMessage == "Bookmarks_LocationChanged".GetLocalized());
    }
    private static async Task Superseded()
    {
        using var fixture = new GraphFixture();
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        fixture.Handler.OnGet = (request, _) =>
        {
            var metadata = Metadata(request);
            if (metadata != null) return Task.FromResult(metadata);
            if (request.RequestUri.AbsolutePath.Contains("/parent/children")) return pending.Task;
            return Task.FromResult(Json("{\"value\":[]}"));
        };
        var old = fixture.Drive.OpenLocationAsync("parent", "target");
        await fixture.Drive.GetFiles();
        pending.SetResult(Json("{\"value\":[{\"id\":\"target\",\"name\":\"old.txt\",\"file\":{}}]}"));
        await old;
        Check(fixture.Drive.ParentItemId == "Root" && fixture.Drive.Files.Count == 0 && !fixture.Drive.HasError);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Bookmark navigation assertion failed"); }
}
