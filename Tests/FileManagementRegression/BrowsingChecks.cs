using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class BrowsingChecks
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Paging follows empty intermediate pages and deduplicates IDs", Paging),
        ("Failed later pages retain rows and retry only the failed page", Retry),
        ("Filters can be replaced, cleared and applied to later pages", Filtering),
        ("Search validates keywords and encodes OData literals within the selected drive", SearchInput),
        ("Foreign drive and remote results never become actionable rows", SearchScope),
        ("New searches win over late success and late failure", LatestSearch),
        ("Cancelled pagination stays partial and resumes without duplication", Cancellation),
        ("Selection on a later page survives refresh", LaterSelection),
        ("Failed new searches preserve previous query and results", FailedSearch),
        ("Search folder navigation resolves real ancestors and rolls back on failure", SearchNavigation),
        ("Malformed pages and cyclic or foreign continuations cannot complete", InvalidPages),
        ("Deletion with failed refresh cannot reappear after clearing filter", DeletedFilter),
        ("Successful rename updates cached names even when refresh fails", RenamedFilter),
        ("Superseded mutation refresh cannot publish errors onto a new search", MutationRefreshRace),
        ("Mutation completion refreshes the latest pending search instead of the old folder", PendingSearchMutation),
        ("A filter replaces a pending search and page navigation cancels old loading", NewIntent),
        ("1000 and 10000 item directories load completely and remain filterable", LargeDirectory),
        ("Browsing and search resources resolve in both languages", Resources)
    ];

    private static string Next(int page) => "https://graph.microsoft.com/v1.0/drives/selected-drive/items/Root/children?cursor=" + page;
    private static object Row(string id, string name = null) => new { id, name = name ?? id + ".txt", file = new { mimeType = "text/plain" } };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Page(object[] rows, string next = null) => Json(new Dictionary<string, object>
    {
        ["value"] = rows, ["@odata.nextLink"] = next
    });
    private static Task<HttpResponseMessage> Result(HttpResponseMessage value) => Task.FromResult(value);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Paging()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.OnGet = (request, _) => Result(request.RequestUri.Query switch
        {
            "?cursor=2" => Page([], Next(3)),
            "?cursor=3" => Page([Row("a"), Row("b")]),
            _ => Page([Row("a")], Next(2))
        });
        await fixture.Drive.GetFiles();
        Check(fixture.Drive.IsComplete && fixture.Drive.Files.Select(file => file.Id).SequenceEqual(["a", "b"]), "Paging lost or duplicated rows.");
        Check(fixture.Handler.Requests.Count == 3, "An empty intermediate page ended traversal.");
    }

    private static async Task Retry()
    {
        using var fixture = new GraphFixture();
        bool fail = true;
        fixture.Handler.OnGet = (request, _) =>
        {
            if (!request.RequestUri.Query.Contains("cursor")) return Result(Page([Row("a")], Next(2)));
            if (fail) throw new HttpRequestException("Second page offline.");
            return Result(Page([Row("b")]));
        };
        await fixture.Drive.GetFiles();
        Check(fixture.Drive.HasError && fixture.Drive.CanRetry && !fixture.Drive.IsComplete && fixture.Drive.Files.Count == 1, "Partial failure hidden.");
        fixture.Drive.SelectedItem = fixture.Drive.Files[0];
        fail = false;
        await fixture.Drive.RetryLoading();
        Check(fixture.Drive.IsComplete && !fixture.Drive.HasError && fixture.Drive.Files.Count == 2 && fixture.Drive.SelectedItem?.Id == "a", "Retry state incorrect.");
        Check(fixture.Handler.Requests.Count(request => !request.Query.Contains("cursor")) == 1, "Retry restarted the first page.");
    }

    private static async Task Filtering()
    {
        using var fixture = new GraphFixture();
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        fixture.Handler.OnGet = (request, _) =>
        {
            if (!request.RequestUri.Query.Contains("cursor")) return Result(Page([Row("a", "report.docx")], Next(2)));
            entered.TrySetResult();
            return gate.Task;
        };
        Task load = fixture.Drive.GetFiles();
        await entered.Task;
        fixture.Drive.FilterByName("photo");
        Check(fixture.Drive.Files.Count == 0 && !fixture.Drive.IsComplete && fixture.Drive.StatusText != "Drive_NoMatches".GetLocalized(), "Premature no-results state.");
        gate.SetResult(Page([Row("b", "photo.png")]));
        await load;
        Check(fixture.Drive.Files.Single().Id == "b", "Later match was lost.");
        fixture.Drive.FilterByName("REPORT");
        Check(fixture.Drive.Files.Single().Id == "a", "Replacing filter used already filtered rows.");
        await fixture.Drive.ClearSearch();
        Check(fixture.Drive.Files.Count == 2 && fixture.Drive.Images.Count == 0 && !fixture.Drive.IsSearchActive, "Clearing filter lost rows.");
    }

    private static async Task SearchInput()
    {
        using var fixture = new GraphFixture("keyword-drive");
        var model = new SearchViewModel(fixture.Drive) { Mode = SearchViewModel.SearchMode.Global };
        foreach (string invalid in new[] { "", "   ", "\t", "word\n", "a\u0001b" })
        {
            model.FileName = invalid;
            Check(!model.CanSearch && !model.SearchCommand.CanExecute(null), "Invalid input enabled search.");
            await fixture.Drive.SearchFile(invalid);
        }
        Check(fixture.Handler.Requests.Count == 0, "Invalid input made a request.");
        string keyword = "中文 O'Brien & # % + ?";
        model.FileName = "  " + keyword + "  ";
        Check(model.CanSearch, "Valid keyword rejected.");
        await model.SearchCommand.ExecuteAsync(null);
        Check(!fixture.Drive.HasError && fixture.Drive.Keyword == keyword && fixture.Drive.IsDriveSearch, "Keyword was not normalized.");
        var request = fixture.Handler.Requests.Last();
        string path = Uri.UnescapeDataString(request.Path);
        Check(path == "/v1.0/drives/keyword-drive/items/root-id/search(q='中文 O''Brien & # % + ?')", "Wrong scope or literal encoding: " + path);
        Check(request.Query == "?%24top=200" || request.Query == "?$top=200", "Keyword escaped into query parameters: " + request.Query);
        Check(fixture.Handler.Requests.First().Path.EndsWith("/keyword-drive/root"), "Search did not resolve this drive's root.");
        await fixture.Drive.ApplyLocalFilter("report");
        Check(!fixture.Drive.IsDriveSearch && fixture.Drive.Files.Single().Name == "report.docx", "Local filter reused drive search results.");
    }

    private static async Task SearchScope()
    {
        using var fixture = new GraphFixture();
        string next = "https://graph.microsoft.com/v1.0/drives/selected-drive/items/root-id/search(q='report')?cursor=2";
        fixture.Handler.OnGet = (request, _) =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/root")) return Result(null);
            if (request.RequestUri.Query.Contains("cursor")) return Result(Page([Row("second")]));
            return Result(Page([
                Row("local"),
                new { id = "foreign", name = "foreign.txt", parentReference = new { driveId = "another-drive" } },
                new { id = "remote", name = "remote.txt", remoteItem = new { id = "elsewhere" } }
            ], next));
        };
        await fixture.Drive.SearchFile("report");
        Check(fixture.Drive.IsComplete && fixture.Drive.Files.Select(file => file.Id).SequenceEqual(["local", "second"]), "Search included external items or dropped pages.");
        Check(fixture.Drive.StatusText.Contains("2 external/shared entries"), "Excluded results were hidden without explanation.");
        Check(fixture.Handler.Requests.All(request => request.Path.StartsWith("/v1.0/drives/selected-drive/")), "Search changed drives.");
    }

    private static async Task LatestSearch()
    {
        foreach (bool fail in new[] { false, true })
        {
            using var fixture = new GraphFixture();
            var entered = new TaskCompletionSource();
            var gate = new TaskCompletionSource<HttpResponseMessage>();
            fixture.Handler.OnGet = (request, _) =>
            {
                string path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
                if (path.EndsWith("/root")) return Result(null);
                if (path.Contains("'old'"))
                {
                    entered.TrySetResult();
                    return gate.Task; // Deliberately ignore cancellation.
                }
                return Result(Page([Row("new")]));
            };
            Task old = fixture.Drive.SearchFile("old");
            await entered.Task;
            await fixture.Drive.SearchFile("new");
            if (fail) gate.SetException(new HttpRequestException("Late old failure."));
            else gate.SetResult(Page([Row("old")]));
            await old;
            Check(fixture.Drive.Keyword == "new" && fixture.Drive.Files.Single().Id == "new" &&
                !fixture.Drive.HasError && fixture.Drive.IsLoading == Visibility.Collapsed, "Old request overwrote current state.");
        }
    }

    private static async Task Cancellation()
    {
        using var fixture = new GraphFixture();
        var entered = new TaskCompletionSource();
        bool slow = true;
        fixture.Handler.OnGet = async (request, token) =>
        {
            if (!request.RequestUri.Query.Contains("cursor")) return Page([Row("a")], Next(2));
            if (slow)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Page([Row("b")]);
        };
        Task load = fixture.Drive.GetFiles();
        await entered.Task;
        fixture.Drive.CancelLoading();
        await load;
        Check(!fixture.Drive.HasError && !fixture.Drive.IsComplete && fixture.Drive.CanRetry, "Cancellation became an error or completion.");
        slow = false;
        await fixture.Drive.RetryLoading();
        Check(fixture.Drive.IsComplete && fixture.Drive.Files.Count == 2, "Cancelled listing did not resume.");
    }

    private static async Task LaterSelection()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.OnGet = (_, _) => Result(Page([Row("a"), Row("b")]));
        await fixture.Drive.GetFiles();
        fixture.Drive.SelectedItem = fixture.Drive.Files[1];
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        fixture.Handler.OnGet = (request, _) =>
        {
            if (!request.RequestUri.Query.Contains("cursor")) return Result(Page([Row("a")], Next(2)));
            entered.TrySetResult();
            return gate.Task;
        };
        Task refresh = fixture.Drive.Refresh();
        await entered.Task;
        Check(fixture.Drive.SelectedItem == null && !fixture.Drive.IsComplete, "Old selected object survived reset.");
        gate.SetResult(Page([Row("b")]));
        await refresh;
        Check(fixture.Drive.SelectedItem?.Id == "b", "Later-page selection was forgotten.");
    }

    private static async Task FailedSearch()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.SearchFile("old");
        fixture.Drive.SelectedItem = fixture.Drive.Files[1];
        var selected = fixture.Drive.SelectedItem;
        fixture.Handler.ListError = new HttpRequestException("Offline");
        await fixture.Drive.SearchFile("new");
        Check(fixture.Drive.Keyword == "old" && fixture.Drive.SelectedItem == selected && fixture.Drive.HasError && fixture.Drive.CanRetry, "Failed first page committed new context.");
        fixture.Handler.ListError = null;
        await fixture.Drive.RetryLoading();
        Check(fixture.Drive.Keyword == "new" && !fixture.Drive.HasError, "Failed search could not retry.");
        await fixture.Drive.ClearSearch();
        Check(!fixture.Drive.IsSearchActive && fixture.Drive.ParentItemId == "Root", "Exit search did not restore source folder.");
    }

    private static async Task SearchNavigation()
    {
        using var fixture = new GraphFixture();
        bool fail = false;
        bool cycle = false;
        fixture.Handler.OnGet = (request, _) =>
        {
            string path = request.RequestUri.AbsolutePath;
            if (path.Contains("search(")) return Result(Page([new { id = "deep", name = "Deep folder", folder = new { } }]));
            if (path.EndsWith("/items/deep"))
            {
                if (fail) throw new HttpRequestException("Missing ancestor");
                return Result(Json(new { id = "deep", name = "Deep folder", folder = new { }, parentReference = new { id = "parent", driveId = "selected-drive" } }));
            }
            if (path.EndsWith("/items/parent"))
                return Result(Json(new { id = "parent", name = "Actual parent", folder = new { }, parentReference = new { id = cycle ? "deep" : "root-id" } }));
            if (path.EndsWith("/children")) return Result(Page([]));
            return Result(null);
        };
        await fixture.Drive.SearchFile("deep");
        fail = true;
        await fixture.Drive.OpenFolder(fixture.Drive.Files.Single());
        Check(fixture.Drive.IsDriveSearch && fixture.Drive.ParentItemId == "Root" && fixture.Drive.BreadcrumbItems.Count == 1, "Failed navigation changed path.");
        fail = false;
        cycle = true;
        await fixture.Drive.OpenFolder(fixture.Drive.Files.Single());
        Check(fixture.Drive.HasError && fixture.Drive.IsDriveSearch, "Ancestor cycle accepted.");
        cycle = false;
        await fixture.Drive.OpenFolder(fixture.Drive.Files.Single());
        Check(!fixture.Drive.IsSearchActive && fixture.Drive.ParentItemId == "deep" &&
            fixture.Drive.BreadcrumbItems.Select(item => item.ItemId).SequenceEqual(["Root", "parent", "deep"]), "Wrong result path.");
        await fixture.Drive.GoUp();
        Check(fixture.Drive.ParentItemId == "parent", "Up did not reach actual parent.");
    }

    private static async Task InvalidPages()
    {
        foreach (string badLink in new[] { Next(2), "https://example.com/steal", "https://graph.microsoft.com/v1.0/drives/foreign/items/Root/children" })
        {
            using var fixture = new GraphFixture();
            fixture.Handler.OnGet = (_, _) => Result(Page([Row("a")], badLink));
            await fixture.Drive.GetFiles();
            Check(fixture.Drive.HasError && !fixture.Drive.IsComplete && fixture.Drive.Files.Count == 1, "Invalid continuation completed.");
            Check(fixture.Handler.Requests.Count <= 2 && fixture.Handler.Requests.All(request => request.Path.StartsWith("/v1.0/drives/selected-drive/")), "Invalid link was followed.");
        }
        using var malformed = new GraphFixture();
        await malformed.Drive.GetFiles();
        malformed.Handler.OnGet = (_, _) => Result(Page([new { name = "No ID" }]));
        await malformed.Drive.Refresh();
        Check(malformed.Drive.HasError && malformed.Drive.Files.Count == 3, "Malformed first page damaged previous data.");
    }

    private static async Task DeletedFilter()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        fixture.Drive.FilterByName("report");
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        await new DeleteFileViewModel(fixture.Drive.Files.Single()).DeleteFile();
        await fixture.Drive.ClearSearch();
        Check(fixture.Drive.Files.Count == 2 && fixture.Drive.Files.All(file => file.Id != "file-id"), "Deleted file reappeared from raw data.");
    }

    private static async Task RenamedFilter()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        fixture.Drive.FilterByName("report");
        var model = new RenameFileViewModel(fixture.Drive, fixture.Drive.Files.Single()) { FileName = "renamed.docx" };
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        await model.RenameFile();
        Check(model.HasSucceeded && fixture.Drive.Files.Count == 0, "Successful rename left stale filter match.");
        await fixture.Drive.ClearSearch();
        Check(fixture.Drive.Files.Single(file => file.Id == "file-id").Name == "renamed.docx", "Clearing filter restored the old name.");
    }

    private static async Task MutationRefreshRace()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var entered = new TaskCompletionSource();
        fixture.Handler.OnGet = (request, _) =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/children"))
            {
                entered.TrySetResult();
                return gate.Task;
            }
            return Result(null);
        };
        var deletion = new DeleteFileViewModel(fixture.Drive.Files.Single(file => file.Id == "file-id"));
        Task mutation = deletion.DeleteFile();
        await entered.Task;
        await fixture.Drive.SearchFile("new");
        gate.SetException(new HttpRequestException("Old refresh failed."));
        await mutation;
        Check(deletion.HasSucceeded && !fixture.Drive.HasError && fixture.Drive.Keyword == "new", "Old mutation refresh overwrote new search status.");
    }

    private static async Task PendingSearchMutation()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        fixture.Handler.MutationGate = new TaskCompletionSource();
        var deletion = new DeleteFileViewModel(fixture.Drive.Files.Single(file => file.Id == "file-id"));
        Task mutation = deletion.DeleteFile();
        var searchEntered = new TaskCompletionSource();
        var oldSearch = new TaskCompletionSource<HttpResponseMessage>();
        int searches = 0;
        fixture.Handler.OnGet = (request, _) =>
        {
            if (!request.RequestUri.AbsolutePath.Contains("search(")) return Result(null);
            if (++searches != 1) return Result(Page([Row("new-result")]));
            searchEntered.SetResult();
            return oldSearch.Task;
        };
        Task search = fixture.Drive.SearchFile("latest");
        await searchEntered.Task;
        fixture.Handler.MutationGate.SetResult();
        await mutation;
        oldSearch.SetResult(Page([Row("stale-result")]));
        await search;
        Check(deletion.HasSucceeded && fixture.Drive.Keyword == "latest" &&
            fixture.Drive.Files.Single().Id == "new-result" && !fixture.Drive.HasError,
            "Mutation completion cancelled the user's pending search.");
    }

    private static async Task NewIntent()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var entered = new TaskCompletionSource();
        fixture.Handler.OnGet = (request, _) =>
        {
            if (request.RequestUri.AbsolutePath.Contains("search("))
            {
                entered.TrySetResult();
                return gate.Task;
            }
            return Result(null);
        };
        Task search = fixture.Drive.SearchFile("pending");
        await entered.Task;
        await fixture.Drive.ApplyLocalFilter("report");
        gate.SetResult(Page([Row("wrong")]));
        await search;
        Check(!fixture.Drive.IsDriveSearch && fixture.Drive.Files.Single().Id == "file-id", "Pending search replaced newer local filter.");
        await fixture.Drive.ClearSearch();
        await fixture.Drive.OpenFolder(fixture.Drive.Files.Single(file => file.IsFolder));
        Check(fixture.Drive.ParentItemId == "folder-id" && !fixture.Drive.HasError, "New navigation failed.");
    }

    private static async Task LargeDirectory()
    {
        foreach (int count in new[] { 1000, 10000 })
        {
            using var fixture = new GraphFixture();
            fixture.Handler.OnGet = (request, _) =>
            {
                string query = request.RequestUri.Query;
                int offset = query.StartsWith("?cursor=") ? int.Parse(query[8..]) : 0;
                object[] rows = Enumerable.Range(offset, Math.Min(200, count - offset))
                    .Select(index => Row("id-" + index, index == count - 1 ? "last-match.txt" : "file-" + index + ".txt")).ToArray();
                return Result(Page(rows, offset + rows.Length < count ? Next(offset + rows.Length) : null));
            };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await fixture.Drive.GetFiles();
            Check(fixture.Drive.IsComplete && fixture.Drive.LoadedCount == count && fixture.Drive.Files.Count == count, "Large directory truncated.");
            fixture.Drive.FilterByName("last-match");
            Check(fixture.Drive.Files.Single().Id == "id-" + (count - 1), "Last-page filter failed.");
            await fixture.Drive.ClearSearch();
            Check(fixture.Drive.Files.Count == count, "Large filter lost rows.");
            Console.WriteLine($"  {count} simulated items: {watch.ElapsedMilliseconds} ms (platform-fake rendering)");
        }
    }

    private static Task Resources()
    {
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            ResourceHelper.Language = language;
            foreach (string key in new[] { "SearchDialog.PrimaryButtonText", "Search_InvalidKeyword", "Search_LocalHint", "Search_DriveHint",
                "Drive_ClearSearch.Content", "Drive_CancelLoading.Content", "Drive_RetryLoading.Content", "Drive_SearchScope", "Drive_FilterScope",
                "Drive_LoadingNew", "Drive_LoadingCount", "Drive_PartialCount", "Drive_CompleteCount", "Drive_NoMatches", "Drive_Empty", "Drive_ExcludedCount" })
                Check(!string.IsNullOrWhiteSpace(key.GetLocalized()), "Missing resource " + key);
        }
        ResourceHelper.Language = "en-US";
        return Task.CompletedTask;
    }
}
