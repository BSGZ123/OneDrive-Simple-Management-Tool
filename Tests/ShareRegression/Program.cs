using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Tests.ShareRegression;

internal static class Program
{
    private const string Link = "https://example.com/share";

    private static async Task Main()
    {
        Ioc.Default.ConfigureServices(new TestServices());
        (string Name, Func<Task> Run)[] cases =
        [
            ("File and folder sharing keep the selected drive and existing permissions", RequestTargets),
            ("Reopening accepts an existing link returned with HTTP 200", ExistingLink),
            ("Invalid item IDs never send a request", InvalidItem),
            ("Missing drive never sends a request", MissingDrive),
            ("Empty and malformed sharing responses remain failures", InvalidResponses),
            ("Busy state blocks duplicate requests and premature copy", BusyState),
            ("API failures show errors and allow retry", ApiFailureRetry),
            ("Network failure allows retry", NetworkFailureRetry),
            ("Copy failure preserves the link for manual copy and retry", ClipboardFailure),
            ("Chinese sharing status and errors resolve from resources", ChineseResources)
        ];
        foreach (var test in cases)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {cases.Length} sharing regression checks.");
    }

    private static async Task RequestTargets()
    {
        foreach (bool folder in new[] { false, true })
        {
            using var fixture = new ShareFixture(folder ? "folder-drive" : "file-drive");
            string copied = null;
            var file = fixture.File(folder ? "folder-id" : "file-id", folder);
            var model = new ShareFileViewModel(file, value => copied = value);
            Assert(!model.CopyLinkCommand.CanExecute(null), "Copy must start disabled.");
            Assert(fixture.Handler.Requests.Count == 0, "Opening the dialog must not share anything.");
            await model.GenerateLinkCommand.ExecuteAsync(null);
            Assert(model.SharingLink == Link && !model.HasError, "Expected a valid sharing result.");
            Assert(model.FileName == file.Name, "Wrong target name.");
            var request = fixture.Handler.Requests.Single();
            Assert(request.Path == $"/v1.0/drives/{fixture.Provider.DriveId}/items/{file.Id}/createLink", "The wrong drive or item was shared.");
            using var body = JsonDocument.Parse(request.Body);
            var root = body.RootElement;
            Assert(root.GetProperty("type").GetString() == "view", "The basic link must be read-only.");
            Assert(root.GetProperty("scope").GetString() == "anonymous", "Wrong sharing scope.");
            Assert(root.GetProperty("retainInheritedPermissions").GetBoolean(), "Existing permissions must be preserved.");
            Assert(!root.TryGetProperty("password", out var password) || password.ValueKind == JsonValueKind.Null, "Unexpected password.");
            model.CopyLinkCommand.Execute(null);
            Assert(copied == Link && model.StatusMessage == "ShareFile_Copied".GetLocalized(), "Copy must use the returned link.");
        }
    }

    private static async Task ExistingLink()
    {
        using var fixture = new ShareFixture();
        await fixture.Provider.CreateLink("file-id");
        fixture.Handler.Status = HttpStatusCode.OK;
        var model = new ShareFileViewModel(fixture.File(), _ => { });
        await model.GenerateLink();
        Assert(model.SharingLink == Link && !model.HasError, "An existing link should be usable.");
    }

    private static async Task InvalidItem()
    {
        using var fixture = new ShareFixture();
        foreach (string id in new[] { null, "", " " })
        {
            await ExpectFailure<ArgumentException>(() => fixture.Provider.CreateLink(id));
        }
        Assert(fixture.Handler.Requests.Count == 0, "An invalid item ID reached Graph.");
    }

    private static async Task MissingDrive()
    {
        using var fixture = new ShareFixture("");
        await ExpectFailure<InvalidOperationException>(() => fixture.Provider.CreateLink("file-id"));
        Assert(fixture.Handler.Requests.Count == 0, "A missing drive reached Graph.");
    }

    private static async Task InvalidResponses()
    {
        using var fixture = new ShareFixture();
        foreach (string response in new[] { "{}", "{\"link\":{}}", "{\"link\":{\"webUrl\":\"\"}}", "{\"link\":{\"webUrl\":\"relative/link\"}}", "{\"link\":{\"webUrl\":\"http://example.com/link\"}}" })
        {
            fixture.Handler.Response = response;
            await ExpectFailure<InvalidDataException>(() => fixture.Provider.CreateLink("file-id"));
        }
        fixture.Handler.Status = HttpStatusCode.NoContent;
        var model = new ShareFileViewModel(fixture.File(), _ => { });
        await model.GenerateLink();
        AssertFailed(model, "ShareFile_InvalidResponse");
    }

    private static async Task BusyState()
    {
        using var fixture = new ShareFixture();
        fixture.Handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ShareFileViewModel(fixture.File(), _ => throw new Exception("Copy must be disabled."));
        var pending = model.GenerateLinkCommand.ExecuteAsync(null);
        Assert(model.IsGenerating && !model.GenerateLinkCommand.CanExecute(null) && !model.CopyLinkCommand.CanExecute(null), "Incorrect busy state.");
        await model.GenerateLink();
        model.CopyLinkCommand.Execute(null);
        Assert(fixture.Handler.Requests.Count == 1, "A duplicate request was sent.");
        fixture.Handler.Gate.SetResult();
        await pending;
        Assert(!model.IsGenerating && model.CopyLinkCommand.CanExecute(null), "Result must become copyable.");
        await model.GenerateLink();
        Assert(fixture.Handler.Requests.Count == 1, "An already generated link should not be regenerated in the same dialog.");
    }

    private static async Task ApiFailureRetry()
    {
        foreach (var (status, key) in new[]
        {
            (HttpStatusCode.Forbidden, "ShareFile_AccessDenied"),
            (HttpStatusCode.NotFound, "ShareFile_NotFound"),
            (HttpStatusCode.Unauthorized, "ShareFile_AuthenticationFailed"),
            (HttpStatusCode.TooManyRequests, "ShareFile_TooManyRequests"),
            (HttpStatusCode.InternalServerError, "ShareFile_Failed")
        })
        {
            using var fixture = new ShareFixture();
            fixture.Handler.Status = status;
            var model = new ShareFileViewModel(fixture.File(), _ => { });
            await model.GenerateLinkCommand.ExecuteAsync(null);
            AssertFailed(model, key);
            fixture.Handler.Status = HttpStatusCode.OK;
            await model.GenerateLinkCommand.ExecuteAsync(null);
            Assert(model.SharingLink == Link && !model.HasError, "Retry did not recover.");
        }
    }

    private static async Task NetworkFailureRetry()
    {
        using var fixture = new ShareFixture();
        fixture.Handler.Error = new HttpRequestException("Offline test.");
        var model = new ShareFileViewModel(fixture.File(), _ => { });
        await model.GenerateLink();
        AssertFailed(model, "ShareFile_NetworkFailed");
        fixture.Handler.Error = null;
        await model.GenerateLink();
        Assert(!model.HasError && model.SharingLink == Link, "Network retry did not recover.");
    }

    private static async Task ClipboardFailure()
    {
        using var fixture = new ShareFixture();
        bool fail = true;
        string copied = null;
        var model = new ShareFileViewModel(fixture.File(), value =>
        {
            if (fail) throw new InvalidOperationException("Clipboard unavailable.");
            copied = value;
        });
        await model.GenerateLink();
        model.CopyLinkCommand.Execute(null);
        Assert(model.HasError && model.ErrorMessage == "ShareFile_CopyFailed".GetLocalized() && model.SharingLink == Link, "Copy failure must retain the link.");
        fail = false;
        model.CopyLinkCommand.Execute(null);
        Assert(!model.HasError && copied == Link, "Copy retry did not recover.");
    }

    private static async Task ChineseResources()
    {
        ResourceHelper.Language = "zh-CN";
        try
        {
            using var fixture = new ShareFixture();
            fixture.Handler.Status = HttpStatusCode.Forbidden;
            var model = new ShareFileViewModel(fixture.File(), _ => { });
            await model.GenerateLink();
            AssertFailed(model, "ShareFile_AccessDenied");
            fixture.Handler.Status = HttpStatusCode.OK;
            await model.GenerateLink();
            model.CopyLinkCommand.Execute(null);
            Assert(model.StatusMessage == "链接已复制。", "Chinese status is missing.");
        }
        finally
        {
            ResourceHelper.Language = "en-US";
        }
    }

    private static void AssertFailed(ShareFileViewModel model, string key)
    {
        Assert(model.HasError && model.ErrorMessage == key.GetLocalized(), "Missing or incorrect error feedback.");
        Assert(!model.IsGenerating && model.SharingLink == "" && model.GenerateLinkCommand.CanExecute(null) && !model.CopyLinkCommand.CanExecute(null), "Failure must allow retry without a usable link.");
    }

    private static async Task ExpectFailure<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
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
