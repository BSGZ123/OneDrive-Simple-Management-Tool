using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class BatchManagementChecks
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Multi-selection survives layout and sort; context menus keep the selected group", Selection),
        ("Filters and navigation remove hidden selections; select-all covers only visible rows", SelectionScope),
        ("Refresh remaps multiple selections across pages and removes missing IDs", PagedSelection),
        ("An explicit selection during refresh replaces pending restored selections", SelectionDuringRefresh),
        ("Name, numeric size and modified time sort across pages in both directions", Sorting),
        ("Sorting survives refresh, filtering and rename without losing selection", SortPersistence),
        ("Batch deletion reports partial failures and retries only failed targets", PartialDelete),
        ("Batch deletion captures targets and rejects concurrent or completed resubmission", CapturedDelete),
        ("Refresh failure cannot repeat successful deletions or resurrect filtered rows", DeleteRefreshFailure),
        ("Delete mode is frozen for retries and mixed-drive targets are rejected", DeleteMode),
        ("Selection, sorting and deletion resources resolve in both languages", Resources)
    ];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Selected(DriveViewModel drive, params string[] ids) =>
        Check(drive.SelectedItems.Select(item => item.Id).ToHashSet().SetEquals(ids), "Wrong selection: " + string.Join(",", drive.SelectedItems.Select(item => item.Id)));

    private static Task<HttpResponseMessage> Page(object[] rows, bool more = false) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["value"] = rows,
            ["@odata.nextLink"] = more ? "https://graph.microsoft.com/v1.0/drives/selected-drive/items/Root/children?cursor=2" : null
        }), Encoding.UTF8, "application/json")
    });

    private static object Row(string id, string name = null, long? size = null, string modified = null, bool folder = false) => new
    {
        id, name = name ?? id, size, lastModifiedDateTime = modified, folder = folder ? new { childCount = 0 } : null
    };

    private static async Task Selection()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SetSelection(drive.Files.Where(file => !file.IsFolder));
        drive.Layout = FileLayout.Grid;
        drive.SortDescending = true;
        drive.Layout = FileLayout.List;
        Selected(drive, "file-id", "image-id");
        drive.SelectForContextMenu(drive.Files.Single(file => file.Id == "file-id"));
        Selected(drive, "file-id", "image-id");
        Check(drive.SelectedItem.Id == "file-id", "Context target is not focused.");
        drive.SelectForContextMenu(drive.Files.Single(file => file.IsFolder));
        Selected(drive, "folder-id");
        drive.SelectAll();
        Check(drive.SelectedCount == 3 && drive.CanDeleteSelection, "Select-all did not enable deletion.");
        drive.ClearSelection();
        Check(!drive.CanDeleteSelection && drive.SelectedItem == null, "Cleared selection is actionable.");
        Check(fixture.Handler.Requests.Count == 1, "Local selection or sorting made requests.");
    }

    private static async Task SelectionScope()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectAll();
        drive.FilterByName("report");
        Selected(drive, "file-id");
        drive.SelectAll();
        Selected(drive, "file-id");
        await drive.ClearSearch();
        Selected(drive, "file-id");
        drive.SelectAll();
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        await drive.OpenFolder(drive.Files.Single(file => file.IsFolder));
        Check(drive.SelectedCount == 3, "Failed navigation changed selection.");
        fixture.Handler.ListStatus = HttpStatusCode.OK;
        await drive.OpenFolder(drive.Files.Single(file => file.IsFolder));
        Selected(drive);
        await drive.GoUp();
        Selected(drive);
    }

    private static async Task PagedSelection()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectAll();
        var old = drive.SelectedItems.ToArray();
        bool fail = true;
        fixture.Handler.OnGet = (request, _) => !request.RequestUri.Query.Contains("cursor")
            ? Page([Row("folder-id", folder: true)], true)
            : fail ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) : Page([Row("image-id")]);
        await drive.Refresh();
        Selected(drive, "folder-id");
        Check(drive.CanRetry, "Partial refresh was not retryable.");
        drive.SortDescending = true;
        fail = false;
        await drive.RetryLoading();
        Selected(drive, "folder-id", "image-id");
        Check(drive.SelectedItems.All(item => !old.Contains(item)), "Old view models survived refresh.");
        fixture.Handler.OnGet = (_, _) => Page([Row("file-id"), Row("folder-id", folder: true), Row("image-id")]);
        await drive.Refresh();
        Selected(drive, "folder-id", "image-id");
    }

    private static async Task SelectionDuringRefresh()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectAll();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var entered = new TaskCompletionSource();
        fixture.Handler.OnGet = (request, _) =>
        {
            if (!request.RequestUri.Query.Contains("cursor")) return Page([Row("folder-id", folder: true)], true);
            entered.TrySetResult();
            return gate.Task;
        };
        Task refresh = drive.Refresh();
        await entered.Task;
        Check(!drive.CanDeleteSelection, "Deletion offered during an unsettled listing.");
        drive.ClearSelection();
        drive.SelectAll();
        gate.SetResult(await Page([Row("file-id"), Row("image-id")]));
        await refresh;
        Selected(drive, "folder-id");
    }

    private static async Task Sorting()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        fixture.Handler.OnGet = (request, _) => !request.RequestUri.Query.Contains("cursor")
            ? Page([Row("z", "zeta", 2, "2025-01-01T00:00:00Z"), Row("unknown", "Unknown"), Row("folder", "Folder", 200, "2026-01-01T00:00:00Z", true)], true)
            : Page([Row("a", "Alpha", 100, "2023-01-01T00:00:00Z"), Row("b", "beta", 10, "2024-01-01T00:00:00Z")]);
        await drive.GetFiles();
        Order("folder", "a", "b", "unknown", "z");
        drive.SortDescending = true;
        Order("folder", "z", "unknown", "b", "a");
        drive.SortIndex = (int)FileSortField.Size;
        Order("folder", "a", "b", "z", "unknown");
        drive.SortDescending = false;
        Order("folder", "z", "b", "a", "unknown");
        drive.SortIndex = (int)FileSortField.Modified;
        Order("folder", "a", "b", "z", "unknown");
        drive.SortDescending = true;
        Order("folder", "z", "b", "a", "unknown");
        drive.FoldersFirst = false;
        drive.SortDescending = false;
        Order("a", "b", "z", "folder", "unknown");
        Check(fixture.Handler.Requests.Count == 2, "Sort reloaded the directory.");
        void Order(params string[] ids) => Check(drive.Files.Select(file => file.Id).SequenceEqual(ids), "Wrong sort order: " + string.Join(",", drive.Files.Select(file => file.Id)));
    }

    private static async Task SortPersistence()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SortDescending = true;
        drive.FoldersFirst = false;
        drive.SelectAll();
        await drive.Refresh();
        Check(drive.SortDescending && !drive.FoldersFirst && drive.Files[0].Id == "file-id" && drive.SelectedCount == 3, "Refresh lost sorting/selection.");
        var rename = new RenameFileViewModel(drive, drive.Files.Single(file => file.Id == "file-id")) { FileName = "AAA.docx" };
        await rename.RenameFile();
        Check(drive.Files.Last().Id == "file-id" && drive.SelectedCount == 3, "Rename failed to reorder/preserve selection.");
        drive.FilterByName(".");
        Selected(drive, "file-id", "image-id");
        await drive.ClearSearch();
        Check(drive.Files.Last().Id == "file-id", "Clearing filter discarded sort order.");
    }

    private static async Task PartialDelete()
    {
        using var fixture = new GraphFixture("batch-drive");
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectAll();
        fixture.Handler.ItemMutationStatus["file-id"] = HttpStatusCode.Forbidden;
        var model = new DeleteFileViewModel(drive.SelectedItems);
        int before = fixture.Handler.Requests.Count;
        await model.DeleteFile();
        Check(model.SuccessCount == 2 && model.FailureCount == 1 && model.CanSubmit && !model.HasSucceeded, "Partial result missing.");
        Selected(drive, "file-id");
        Check(model.Items.Single(item => item.File.Id == "file-id").ErrorMessage == "FileOperation_AccessDenied".GetLocalized(), "Failure details missing.");
        Check(fixture.Handler.Requests.Skip(before).Count(request => request.Method == "GET") == 1, "Batch refreshed per item.");
        fixture.Handler.ItemMutationStatus.Clear();
        await model.DeleteFile();
        Check(model.HasSucceeded && !model.HasError && model.SuccessCount == 3 && model.FailureCount == 0, "Retry did not finish.");
        var requests = fixture.Handler.Requests.Where(request => request.Method == "DELETE").ToArray();
        Check(requests.Length == 4 && requests.Count(request => request.Path.EndsWith("/file-id")) == 2 &&
            requests.All(request => request.Path.StartsWith("/v1.0/drives/batch-drive/items/")), "Retry repeated successes or used another drive.");
        Selected(drive);
    }

    private static async Task CapturedDelete()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        var targets = drive.Files.Where(file => !file.IsFolder).ToArray();
        var model = new DeleteFileViewModel(targets.Concat(targets));
        fixture.Handler.MutationGate = new TaskCompletionSource();
        Task deleting = model.DeleteFile();
        await model.DeleteFile();
        Check(model.IsBusy && fixture.Handler.Requests.Count(request => request.Method == "DELETE") == 1, "Concurrent submission duplicated requests.");
        await drive.OpenFolder(drive.Files.Single(file => file.IsFolder));
        drive.SelectAll();
        fixture.Handler.MutationGate.SetResult();
        await deleting;
        await model.DeleteFile();
        Check(model.HasSucceeded && drive.ParentItemId == "folder-id" && drive.Files.Single().Id == "child-id", "Deletion changed navigation or deleted new selection.");
        Check(fixture.Handler.Requests.Count(request => request.Method == "DELETE") == 2, "Duplicate targets or completed operation repeated.");
    }

    private static async Task DeleteRefreshFailure()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.FilterByName(".");
        drive.SelectAll();
        var model = new DeleteFileViewModel(drive.SelectedItems);
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        await model.DeleteFile();
        await model.DeleteFile();
        await drive.ClearSearch();
        Check(model.HasSucceeded && !model.CanSubmit && drive.Files.Single().IsFolder && drive.SelectedCount == 0,
            "Refresh failure resurrected deleted files or kept selection.");
        Check(fixture.Handler.Requests.Count(request => request.Method == "DELETE") == 2, "Successful deletions repeated after refresh failure.");
    }

    private static async Task DeleteMode()
    {
        using var fixture = new GraphFixture();
        using var foreign = new GraphFixture("foreign");
        await fixture.Drive.GetFiles();
        await foreign.Drive.GetFiles();
        var file = fixture.Drive.Files.Single(item => item.Id == "file-id");
        try
        {
            _ = new DeleteFileViewModel(new[] { file, foreign.Drive.Files.Single(item => item.Id == "file-id") });
            throw new Exception("Mixed drive deletion accepted.");
        }
        catch (ArgumentException) { }
        fixture.Handler.MutationStatus = HttpStatusCode.Forbidden;
        var model = new DeleteFileViewModel(new[] { file }) { PermanentDelete = true };
        await model.DeleteFile();
        Check(!model.CanChooseDeleteMode && model.CanSubmit, "Retry allowed mode changes.");
        model.PermanentDelete = false;
        fixture.Handler.MutationStatus = HttpStatusCode.OK;
        await model.DeleteFile();
        Check(fixture.Handler.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/permanentDelete")) == 2,
            "Retry changed confirmed deletion mode.");
    }

    private static Task Resources()
    {
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            ResourceHelper.Language = language;
            foreach (string key in new[] { "Drive_SelectedCount", "Drive_SelectAll.Content", "Drive_ClearSelection.Content", "Drive_DeleteSelected.Content",
                "Drive_Sort.ToolTipService.ToolTip", "Drive_SortField.Header", "Drive_SortName.Content", "Drive_SortSize.Content", "Drive_SortModified.Content",
                "Drive_SortDescending.Content", "Drive_FoldersFirst.Content", "DeleteBatch_Dialog.Title", "DeleteBatch_Dialog.CloseButtonText", "DeleteBatch_Question",
                "DeleteBatch_Results", "DeleteBatch_Summary", "DeleteBatch_RetryHint", "DeleteBatch_Retry", "DeleteBatch_Pending", "DeleteBatch_Working", "DeleteBatch_Succeeded", "DeleteBatch_Failed" })
                Check(!string.IsNullOrWhiteSpace(key.GetLocalized()), "Missing resource " + key);
        }
        ResourceHelper.Language = "en-US";
        return Task.CompletedTask;
    }
}
