using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Text;
using System.Xml.Linq;

int passed = 0;
await Check("New profile shows distinct empty states without quota requests", async () =>
{
    using var f = new Fixture();
    await f.Model.ActivateAsync();
    Assert(f.Model.IsEmpty && f.Model.HasNoTransfers && f.Model.HasNoSyncs && !f.Model.HasError);
    Assert(f.Service.QuotaCalls == 0 && !f.Model.IsLoading && f.Model.RefreshCommand.CanExecute(null));
});
await Check("Multiple drives finish independently with at most three requests", async () =>
{
    using var f = new Fixture();
    f.Service.Drives = Enumerable.Range(0, 8).Select(i => Drive(i.ToString())).ToArray();
    var first = new TaskCompletionSource<HomeQuota>();
    int active = 0, peak = 0;
    f.Service.Quota = async (drive, token) =>
    {
        peak = Math.Max(peak, ++active);
        try
        {
            if (drive.DriveId == "0") return await first.Task.WaitAsync(token);
            await Task.Delay(10, token);
            if (drive.DriveId == "1") throw new HomeOverviewException("Home_SignInRequired");
            return new HomeQuota(100, 20, 80);
        }
        finally { active--; }
    };
    var load = f.Model.ActivateAsync();
    await Until(() => f.Model.Drives.Count == 8 && !f.Model.Drives[7].IsLoading);
    Assert(f.Model.IsLoading && f.Model.Drives[0].IsLoading && f.Model.Drives[1].HasError);
    Assert(f.Model.Drives[2].UsagePercent == 20 && !f.Model.RefreshCommand.CanExecute(null));
    first.SetResult(new HomeQuota(100, 60, 40));
    await load;
    Assert(peak == 3 && !f.Model.IsLoading && !f.Model.HasError && f.Model.Drives[0].UsagePercent == 60);
});
await Check("Configuration failure is not a new empty profile and can recover", async () =>
{
    using var f = new Fixture();
    f.Service.LoadError = new HomeOverviewException("Configuration_Protection");
    await f.Model.ActivateAsync();
    Assert(f.Model.HasError && !f.Model.IsEmpty && f.Service.QuotaCalls == 0);
    f.Service.LoadError = null;
    f.Service.Drives = new[] { Drive("recovered") };
    await f.Model.RefreshCommand.ExecuteAsync(null);
    Assert(!f.Model.HasError && f.Model.Drives.Count == 1);
});
await Check("Navigation cancels a hung request; late results cannot overwrite a new visit", async () =>
{
    using var f = new Fixture();
    f.Service.Drives = new[] { Drive("old") };
    var late = new TaskCompletionSource<HomeQuota>();
    CancellationToken oldToken = default;
    f.Service.Quota = (_, token) => { oldToken = token; return late.Task; };
    var oldLoad = f.Model.ActivateAsync();
    await Until(() => oldToken.CanBeCanceled);
    f.Model.Deactivate();
    await oldLoad.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(oldToken.IsCancellationRequested && !f.Model.IsLoading);
    f.Service.Drives = new[] { Drive("new") };
    f.Service.Quota = (_, _) => Task.FromResult(new HomeQuota(100, 70, 30));
    await f.Model.ActivateAsync();
    late.SetResult(new HomeQuota(100, 99, 1));
    await Task.Delay(20);
    Assert(f.Model.Drives.Single().Drive.DriveId == "new" && f.Model.Drives[0].UsagePercent == 70);
});
await Check("Slow initialization does not trigger an extra refresh after reactivation", async () =>
{
    using var f = new Fixture();
    var pending = new TaskCompletionSource();
    f.Sync.Initialize = () => pending.Task;
    var old = f.Model.ActivateAsync();
    f.Model.Deactivate();
    var current = f.Model.ActivateAsync();
    pending.SetResult();
    await Task.WhenAll(old, current);
    Assert(f.Service.LoadCalls == 1);
});
await Check("Every transfer state belongs to one group; state changes and removals update counts", async () =>
{
    using var f = new Fixture();
    foreach (var state in Enum.GetValues<DownloadTaskState>()) f.Tasks.DownloadTasks.Add(new() { State = state });
    foreach (var state in Enum.GetValues<UploadTaskState>()) f.Tasks.UploadTasks.Add(new() { State = state });
    await f.Model.ActivateAsync();
    Assert(f.Model.ActiveTransfers == 9 && f.Model.WaitingTransfers == 3 && f.Model.FailedTransfers == 2 &&
        f.Model.CompletedTransfers == 2 && f.Model.CancelledTransfers == 2 && f.Model.TotalTransfers == 18);
    var task = f.Tasks.DownloadTasks[0];
    task.State = DownloadTaskState.Downloading;
    Assert(f.Model.ActiveTransfers == 10 && f.Model.WaitingTransfers == 2);
    f.Tasks.DownloadTasks.Remove(task);
    int notifications = 0;
    f.Model.PropertyChanged += (_, _) => notifications++;
    task.State = DownloadTaskState.Completed;
    Assert(notifications == 0 && f.Model.ActiveTransfers == 9);
    f.Tasks.DownloadTasks.Clear();
    f.Tasks.UploadTasks.Clear();
    Assert(f.Model.TotalTransfers == 0 && f.Model.HasNoTransfers && f.Model.ActiveTransfers == 0);
});
await Check("Inactive pages release collection and item subscriptions and resnapshot on return", async () =>
{
    using var f = new Fixture();
    var task = new UploadTaskViewModel { State = UploadTaskState.Uploading };
    f.Tasks.UploadTasks.Add(task);
    await f.Model.ActivateAsync();
    f.Model.Deactivate();
    int notifications = 0;
    f.Model.PropertyChanged += (_, _) => notifications++;
    task.State = UploadTaskState.Completed;
    f.Tasks.UploadTasks.Clear();
    f.Sync.ErrorMessage = "fake sync error";
    Assert(notifications == 0);
    await f.Model.ActivateAsync();
    Assert(f.Model.TotalTransfers == 0 && f.Model.HasSyncError);
});
await Check("Sync activity, pause and attention update without treating config failure as empty", async () =>
{
    using var f = new Fixture();
    await f.Model.ActivateAsync();
    var job = new FolderSyncItemViewModel { IsActive = true };
    f.Sync.Bindings.Add(job);
    Assert(f.Model.SyncCount == 1 && f.Model.ActiveSyncs == 1 && !f.Model.HasNoSyncs);
    job.IsActive = false;
    job.Enabled = false;
    job.HasIssues = true;
    job.CommandError = "failed";
    Assert(f.Model.PausedSyncs == 1 && f.Model.SyncIssues == 1 && f.Model.ActiveSyncs == 0);
    f.Sync.Bindings.Clear();
    f.Sync.ErrorMessage = "configuration failed";
    Assert(f.Model.HasSyncError && !f.Model.HasNoSyncs);
    f.Sync.ErrorMessage = "";
    Assert(!f.Model.HasSyncError && f.Model.HasNoSyncs);
});
await Check("Missing, zero, negative and over-capacity quotas are presented accurately", () =>
{
    var card = new HomeDriveViewModel(Drive("a"));
    card.Apply(new(null, null, null));
    Assert(!card.HasUsage && card.CapacityText == "Home_QuotaUnavailable".GetLocalized());
    card.Apply(new(100, 0, 100));
    Assert(card.HasUsage && card.UsagePercent == 0 && card.CapacityText.Contains("0 B"));
    card.Apply(new(0, 0, 0));
    Assert(!card.HasUsage && card.CapacityText.Contains("0 B") && card.RemainingText.Contains("0 B"));
    card.Apply(new(-1, -2, -3));
    Assert(!card.HasUsage && card.RemainingText == "" && card.CapacityText == "Home_QuotaUnavailable".GetLocalized());
    card.Apply(new(100, 120, 0));
    Assert(card.UsagePercent == 100 && card.CapacityText.Contains("120 B"));
    card.Apply(new(long.MaxValue, long.MaxValue, 0));
    Assert(card.UsagePercent == 100 && card.CapacityText.Contains("EB"));
    card.Apply(new(null, 1024, null));
    Assert(!card.HasUsage && card.CapacityText.Contains("1 KB") && card.CapacityText.Contains("Unknown"));
    return Task.CompletedTask;
});
await Check("Card failure clears old usage and a later refresh recovers", async () =>
{
    using var f = new Fixture();
    f.Service.Drives = new[] { Drive("a") };
    f.Service.Quota = (_, _) => throw new OperationCanceledException();
    await f.Model.ActivateAsync();
    Assert(f.Model.Drives[0].ErrorMessage == "Home_QuotaTimeout".GetLocalized() && !f.Model.Drives[0].IsLoading);
    f.Service.Quota = (_, _) => Task.FromResult(new HomeQuota(100, 50, 50));
    await f.Model.RefreshCommand.ExecuteAsync(null);
    Assert(!f.Model.Drives[0].HasError && f.Model.Drives[0].UsagePercent == 50);
});
await Check("Service preserves account/drive identity even for same-named drives and uses silent tokens", async () =>
{
    using var f = new StoreFixture();
    await f.Store.AddAsync(Record("A"), 0);
    await f.Store.AddAsync(Record("B"), 1);
    var auth = new Authentication();
    var seen = new List<string>();
    var service = new HomeDriveService(f.Store, auth, (authentication, account) =>
        new GraphServiceClient(new HttpClient(new Handler(request =>
        {
            seen.Add(account);
            Assert(request.RequestUri.AbsolutePath.EndsWith("/drives/shared-id"));
            Assert(Uri.UnescapeDataString(request.RequestUri.Query).Contains("id,quota"));
            Assert(request.Headers.Authorization.Parameter == "fake-token-" + account);
            return Response(HttpStatusCode.OK, "{\"id\":\"shared-id\",\"quota\":{\"total\":100,\"used\":25,\"remaining\":75}}");
        })), new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(authentication, account))));
    var loaded = await service.LoadDrivesAsync(default);
    Assert(loaded.Count == 2 && loaded[0].DisplayName == loaded[1].DisplayName);
    foreach (var drive in loaded) Assert(await service.GetQuotaAsync(drive, default) == new HomeQuota(100, 25, 75));
    Assert(seen.SequenceEqual(new[] { "A", "B" }) && auth.Accounts.SequenceEqual(new[] { "A", "B" }));
});
await Check("Service handles absent quota, mismatched identity, access failures and expired sign-in", async () =>
{
    using var f = new StoreFixture();
    var auth = new Authentication();
    var body = "{\"id\":\"a\"}";
    var status = HttpStatusCode.OK;
    var service = new HomeDriveService(f.Store, auth, (authentication, account) =>
        new GraphServiceClient(new HttpClient(new Handler(_ => Response(status, body))),
            new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(authentication, account))));
    Assert(await service.GetQuotaAsync(Drive("a"), default) == new HomeQuota(null, null, null));
    body = "{\"id\":\"wrong-drive\"}";
    await Error(() => service.GetQuotaAsync(Drive("a"), default), "Home_QuotaUnavailable");
    body = "{\"error\":{\"code\":\"accessDenied\",\"message\":\"private server detail\"}}";
    foreach (var (code, key) in new[] { (401, "Home_SignInRequired"), (403, "Home_QuotaAccessDenied"), (404, "Home_DriveNotFound"), (429, "Home_QuotaThrottled") })
    {
        status = (HttpStatusCode)code;
        await Error(() => service.GetQuotaAsync(Drive("a"), default), key);
    }
    auth.Failure = AuthenticationFailure.RequiresSignIn;
    await Error(() => service.GetQuotaAsync(Drive("a"), default), "Home_SignInRequired");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await service.GetQuotaAsync(Drive("a"), cancelled.Token); throw new Exception("Cancellation lost"); }
    catch (OperationCanceledException) { }
});
await Check("Configuration corruption is surfaced without overwriting the file", async () =>
{
    using var f = new StoreFixture();
    Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.Drives));
    await File.WriteAllTextAsync(f.Paths.Drives, "broken");
    var service = new HomeDriveService(f.Store, new Authentication());
    await Error(() => service.LoadDrivesAsync(default), "Configuration_Invalid");
    Assert(await File.ReadAllTextAsync(f.Paths.Drives) == "broken");
});
await Check("Both locales cover all Home keys and XAML UIDs", () =>
{
    var languages = new[] { "en-US", "zh-CN" }.Select(language => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw"))
        .Root.Elements("data").ToDictionary(e => (string)e.Attribute("name"), e => e.Element("value").Value)).ToArray();
    var english = languages[0].Keys.Where(k => k.StartsWith("Home_")).ToHashSet();
    Assert(english.SetEquals(languages[1].Keys.Where(k => k.StartsWith("Home_"))));
    var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "HomePage.xaml"));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    foreach (string uid in xaml.Descendants().Attributes(x + "Uid").Select(a => a.Value))
        foreach (var resource in languages) Assert(resource.Any(r => r.Key.StartsWith(uid + ".") && !string.IsNullOrWhiteSpace(r.Value)));
    foreach (var resource in languages)
    {
        Assert(string.Format(resource["Home_TransferSummary"], 1, 2, 3, 4, 5).Contains('5'));
        Assert(string.Format(resource["Home_SyncSummary"], 1, 2, 3, 4).Contains('4'));
    }
    return Task.CompletedTask;
});
Console.WriteLine($"PASS: {passed} home regression checks");

async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static HomeDrive Drive(string id) => new("fictional-account", id, "Example drive");
static DriveDTO Record(string account) => new() { DisplayName = "Same name", Provider = new() { HomeAccountId = account, DriveId = "shared-id" } };
static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static async Task Until(Func<bool> ready)
{
    for (int i = 0; i < 200 && !ready(); i++) await Task.Delay(10);
    Assert(ready());
}
static async Task Error(Func<Task> action, string key)
{
    try { await action(); throw new Exception("Expected error " + key); }
    catch (HomeOverviewException error) { Assert(error.ResourceKey == key); }
}
sealed class Fixture : IDisposable
{
    public MemoryDrives Service { get; } = new();
    public TaskManagerViewModel Tasks { get; } = new();
    public FolderSyncViewModel Sync { get; } = new();
    public HomeViewModel Model { get; }
    public Fixture() => Model = new(Service, Tasks, Sync);
    public void Dispose() => Model.Deactivate();
}
sealed class MemoryDrives : IHomeDriveService
{
    public IReadOnlyList<HomeDrive> Drives = Array.Empty<HomeDrive>();
    public Exception LoadError;
    public int QuotaCalls, LoadCalls;
    public Func<HomeDrive, CancellationToken, Task<HomeQuota>> Quota = (_, _) => Task.FromResult(new HomeQuota(100, 10, 90));
    public Task<IReadOnlyList<HomeDrive>> LoadDrivesAsync(CancellationToken token)
    { LoadCalls++; return LoadError == null ? Task.FromResult(Drives) : Task.FromException<IReadOnlyList<HomeDrive>>(LoadError); }
    public Task<HomeQuota> GetQuotaAsync(HomeDrive drive, CancellationToken token) { QuotaCalls++; return Quota(drive, token); }
}
sealed class StoreFixture : IDisposable
{
    public ApplicationDataPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "home-regression-" + Guid.NewGuid().ToString("N")), Path.Combine(Path.GetTempPath(), "no-legacy-" + Guid.NewGuid().ToString("N")));
    public DriveConfigurationStore Store { get; }
    public StoreFixture() => Store = new(Paths, new PlaintextTestProtector());
    public void Dispose() { if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true); }
}
sealed class PlaintextTestProtector : IConfigurationProtector
{
    public byte[] Protect(byte[] bytes, string purpose) => bytes.ToArray();
    public byte[] Unprotect(byte[] bytes, string purpose) => bytes.ToArray();
}
sealed class Authentication : IAccountAuthenticationService
{
    public List<string> Accounts { get; } = new();
    public AuthenticationFailure? Failure;
    public Task<AccountToken> AcquireSilentAsync(string accountId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Accounts.Add(accountId);
        if (Failure.HasValue) throw new AccountAuthenticationException(Failure.Value);
        return Task.FromResult(new AccountToken(accountId, "fake-token-" + accountId));
    }
    public Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token) => throw new Exception("Unexpected interactive sign-in");
}
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
}
