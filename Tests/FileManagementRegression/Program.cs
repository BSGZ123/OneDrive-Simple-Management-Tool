using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class Program
{
    private static async Task Main()
    {
        Ioc.Default.ConfigureServices(new TestServices());
        (string Name, Func<Task> Run)[] cases =
        [
            ("Layout changes preserve directory, selection and loaded data without requests", Layout),
            ("Opening a folder and going up commit matching breadcrumbs", Navigation),
            ("Failed navigation preserves directory, breadcrumb and selection", FailedNavigation),
            ("Refresh restores selection by ID and clears removed selections", RefreshSelection),
            ("Local and global searches survive refresh and layout changes", Search),
            ("Network and invalid-list errors preserve data and release loading", LoadFailures),
            ("Invalid or unchanged names never send mutations", NameValidation),
            ("Renaming uses the selected drive/item and refreshes its name", RenameTargets),
            ("Busy operations reject duplicate submissions and successful ones cannot repeat", BusyOperation),
            ("Mutation errors retain input and allow retry", MutationRetry),
            ("Creation captures its parent and preserves the upload conflict policy", CreateFolder),
            ("Ordinary and permanent deletion use their respective endpoints", DeleteTargets),
            ("Successful deletion with failed refresh cannot be submitted again", RefreshFailure),
            ("Cancelling conversion does not download or write anything", CancelConversion),
            ("Conversion sends format as query to the selected drive and saves bytes", ConvertRequest),
            ("Unsupported conversion never opens the picker", UnsupportedConversion),
            ("English and Chinese mutation error and validation resources resolve", Resources)
        ];
        cases = cases.Concat(BrowsingChecks.Cases).ToArray();
        foreach (var test in cases)
        {
            await Microsoft.UI.Dispatching.TestUiContext.Run(test.Run);
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {cases.Length} file management regression checks.");
    }

    private static async Task Layout()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectedItem = drive.Files[1];
        var selection = drive.SelectedItem;
        int requests = fixture.Handler.Requests.Count;
        drive.Layout = FileLayout.Grid;
        drive.Layout = FileLayout.List;
        Assert(drive.SelectedItem == selection && drive.ParentItemId == "Root" && drive.Files.Count == 3, "Layout lost state.");
        Assert(requests == fixture.Handler.Requests.Count, "Layout sent a request.");
    }

    private static async Task Navigation()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        await drive.OpenFolder(drive.Files.Single(file => file.IsFolder));
        Assert(drive.ParentItemId == "folder-id" && drive.BreadcrumbItems.Count == 2 && drive.Files.Single().Id == "child-id", "Wrong folder.");
        await drive.GoUp();
        Assert(drive.ParentItemId == "Root" && drive.BreadcrumbItems.Count == 1 && drive.Files.Count == 3, "Up did not reach parent.");
        int requests = fixture.Handler.Requests.Count;
        await drive.GoUp();
        Assert(requests == fixture.Handler.Requests.Count, "Going up at root sent a request.");
    }

    private static async Task FailedNavigation()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectedItem = drive.Files[1];
        var selected = drive.SelectedItem;
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        await drive.OpenFolder(drive.Files[0]);
        Assert(drive.ParentItemId == "Root" && drive.BreadcrumbItems.Count == 1 && drive.SelectedItem == selected, "Failure committed navigation.");
        Assert(drive.HasError && drive.IsLoading == Visibility.Collapsed, "Failure left loading active.");
        fixture.Handler.ListStatus = HttpStatusCode.OK;
        await drive.OpenFolder(drive.Files[0]);
        fixture.Handler.ListStatus = HttpStatusCode.NotFound;
        await drive.GoUp();
        Assert(drive.ParentItemId == "folder-id" && drive.BreadcrumbItems.Count == 2, "Failed back navigation removed breadcrumb.");
    }

    private static async Task RefreshSelection()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.SelectedItem = drive.Files[1];
        var old = drive.SelectedItem;
        await drive.Refresh();
        Assert(drive.SelectedItem?.Id == old.Id && drive.SelectedItem != old, "Selection was not remapped.");
        fixture.Handler.Names.Remove(old.Id);
        await drive.Refresh();
        Assert(drive.SelectedItem == null && drive.Images.Count == 1, "Selection or image collection is stale.");
    }

    private static async Task Search()
    {
        using var fixture = new GraphFixture();
        var drive = fixture.Drive;
        await drive.GetFiles();
        drive.FilterByName("REPORT");
        drive.SelectedItem = drive.Files.Single();
        await drive.Refresh();
        Assert(drive.Files.Single().Id == "file-id" && drive.SelectedItem?.Id == "file-id", "Refresh lost local filter.");
        await drive.SearchFile("report");
        drive.Layout = FileLayout.Grid;
        await drive.Refresh();
        Assert(fixture.Handler.Requests.Last().Path.Contains("search"), "Global search refresh went to directory instead.");
    }

    private static async Task LoadFailures()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        fixture.Handler.ListError = new HttpRequestException("Simulated disconnect");
        await fixture.Drive.Refresh();
        Assert(fixture.Drive.Files.Count == 3 && fixture.Drive.HasError && fixture.Drive.IsLoading == Visibility.Collapsed, "Network failure damaged state.");
        fixture.Handler.ListError = null;
        fixture.Handler.InvalidList = true;
        await fixture.Drive.Refresh();
        Assert(fixture.Drive.Files.Count == 3 && fixture.Drive.HasError, "Invalid response cleared data.");
        fixture.Handler.InvalidList = false;
        await fixture.Drive.Refresh();
        Assert(!fixture.Drive.HasError, "Retry retained stale error.");
    }

    private static async Task NameValidation()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var rename = new RenameFileViewModel(fixture.Drive, fixture.Drive.Files[1]);
        var create = new CreateFolderViewModel(fixture.Drive);
        int requests = fixture.Handler.Requests.Count;
        await rename.RenameFile();
        foreach (string name in new[] { "", " ", ".", "..", " bad", "bad ", "a/b", "a\\b", "a:b", "a\nb" })
        {
            rename.FileName = name;
            create.FolderName = name;
            await rename.RenameFile();
            await create.CreateFolder();
            Assert(!rename.CanSubmit && !create.CanSubmit, "Invalid name accepted.");
        }
        Assert(fixture.Handler.Requests.Count == requests, "Invalid names reached Graph.");
        rename.FileName = "文档 2026.docx";
        Assert(rename.CanSubmit, "Valid Unicode name rejected.");
    }

    private static async Task RenameTargets()
    {
        foreach (bool folder in new[] { false, true })
        {
            using var fixture = new GraphFixture("another-drive");
            await fixture.Drive.GetFiles();
            var file = fixture.Drive.Files.First(file => file.IsFolder == folder);
            fixture.Drive.SelectedItem = file;
            var model = new RenameFileViewModel(fixture.Drive, file) { FileName = "renamed" };
            await model.RenameFile();
            var request = fixture.Handler.Requests.Single(request => request.Method == "PATCH");
            Assert(request.Path == $"/v1.0/drives/another-drive/items/{file.Id}", "Wrong rename target.");
            Assert(model.HasSucceeded && fixture.Drive.SelectedItem?.Name == "renamed", "Renamed selection did not refresh.");
        }
    }

    private static async Task BusyOperation()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        fixture.Handler.MutationGate = new TaskCompletionSource();
        var model = new RenameFileViewModel(fixture.Drive, fixture.Drive.Files[1]) { FileName = "new.docx" };
        Task first = model.RenameFile();
        await model.RenameFile();
        Assert(model.IsBusy && !model.CanSubmit && fixture.Handler.Requests.Count(request => request.Method == "PATCH") == 1, "Duplicate request while busy.");
        fixture.Handler.MutationGate.SetResult();
        await first;
        await model.RenameFile();
        Assert(model.HasSucceeded && !model.CanSubmit && !model.IsBusy && fixture.Handler.Requests.Count(request => request.Method == "PATCH") == 1, "Completed operation repeated.");
    }

    private static async Task MutationRetry()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var model = new RenameFileViewModel(fixture.Drive, fixture.Drive.Files[1]) { FileName = "retry.docx" };
        fixture.Handler.MutationStatus = HttpStatusCode.Conflict;
        await model.RenameFile();
        Assert(model.HasError && model.CanSubmit && !model.HasSucceeded && model.FileName == "retry.docx", "Failed mutation lost input/state.");
        fixture.Handler.MutationStatus = HttpStatusCode.OK;
        await model.RenameFile();
        Assert(model.HasSucceeded && !model.HasError, "Retry failed.");
    }

    private static async Task CreateFolder()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var model = new CreateFolderViewModel(fixture.Drive) { FolderName = "new folder" };
        await fixture.Drive.OpenFolder(fixture.Drive.Files[0]);
        await model.CreateFolder();
        var request = fixture.Handler.Requests.Single(request => request.Method == "POST");
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert(request.Path.EndsWith("/items/Root/children"), "Captured destination changed.");
        Assert(body.GetProperty("@microsoft.graph.conflictBehavior").GetString() == "rename", "Upload conflict behavior changed.");
        Assert(model.HasSucceeded, "Creation did not finish.");
    }

    private static async Task DeleteTargets()
    {
        foreach (bool permanent in new[] { false, true })
        {
            using var fixture = new GraphFixture();
            await fixture.Drive.GetFiles();
            var file = fixture.Drive.Files[1];
            fixture.Drive.SelectedItem = file;
            var model = new DeleteFileViewModel(file) { PermanentDelete = permanent };
            await model.DeleteFile();
            var request = fixture.Handler.Requests.Single(request => request.Method != "GET");
            Assert(request.Method == (permanent ? "POST" : "DELETE") && request.Path.EndsWith(permanent ? "/file-id/permanentDelete" : "/file-id"), "Wrong delete endpoint.");
            Assert(model.HasSucceeded && fixture.Drive.SelectedItem == null && fixture.Drive.Files.All(file => file.Id != "file-id"), "Deleted item still selected/visible.");
        }
    }

    private static async Task RefreshFailure()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var file = fixture.Drive.Files[1];
        fixture.Drive.SelectedItem = file;
        fixture.Handler.ListStatus = HttpStatusCode.Forbidden;
        var model = new DeleteFileViewModel(file);
        await model.DeleteFile();
        await model.DeleteFile();
        Assert(model.HasSucceeded && !model.HasError && !model.CanSubmit, "Refresh failure incorrectly marked mutation failed.");
        Assert(fixture.Drive.ErrorMessage == "FileOperation_RefreshFailed".GetLocalized() && fixture.Drive.SelectedItem == null, "Missing refresh warning/stale selection.");
        Assert(fixture.Handler.Requests.Count(request => request.Method == "DELETE") == 1, "Deletion repeated.");
    }

    private static async Task CancelConversion()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        int requests = fixture.Handler.Requests.Count;
        var model = new ConvertFileFormatViewModel(fixture.Drive.Files[1], () => Task.FromResult<StorageFile>(null));
        await model.ConvertFileFormat();
        Assert(fixture.Handler.Requests.Count == requests && !model.HasError && !model.IsBusy && model.SavedFilePath == "", "Cancelled conversion made a request.");
    }

    private static async Task ConvertRequest()
    {
        using var fixture = new GraphFixture("conversion-drive");
        await fixture.Drive.GetFiles();
        var file = new StorageFile("converted.pdf", 0);
        var picker = new TaskCompletionSource<StorageFile>();
        var model = new ConvertFileFormatViewModel(fixture.Drive.Files[1], () => picker.Task);
        Task first = model.ConvertFileFormat();
        await model.ConvertFileFormat();
        Assert(model.IsBusy && !model.ConvertFileFormatCommand.CanExecute(null), "Conversion was not guarded.");
        picker.SetResult(file);
        await first;
        var request = fixture.Handler.Requests.Single(request => request.Path.EndsWith("/content"));
        Assert(request.Path == "/v1.0/drives/conversion-drive/items/file-id/content" && request.Query == "?format=pdf", "Wrong conversion URL.");
        Assert(Encoding.UTF8.GetString(file.WrittenContent) == "%PDF-test" && !model.HasError && !model.IsBusy, "Conversion output/state invalid.");
    }

    private static async Task UnsupportedConversion()
    {
        using var fixture = new GraphFixture();
        await fixture.Drive.GetFiles();
        var model = new ConvertFileFormatViewModel(fixture.Drive.Files[0], () => throw new Exception("Picker must not open"));
        await model.ConvertFileFormat();
        Assert(!model.HasError && !model.CanConvert, "Folder conversion was offered.");
    }

    private static Task Resources()
    {
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            ResourceHelper.Language = language;
            foreach (string key in new[] { "FileOperation_RefreshFailed", "FileOperation_NameConflict", "FileOperation_InvalidResponse", "FileOperation_NameHint.Text", "FileConvert_Completed" })
                Assert(!string.IsNullOrWhiteSpace(key.GetLocalized()), "Missing resource.");
        }
        ResourceHelper.Language = "en-US";
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestServices : IServiceProvider
    {
        public object GetService(Type serviceType) => null;
    }
}
