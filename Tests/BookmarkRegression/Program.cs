using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

int passed = 0;
await Check("First run is empty and creates no bookmark data", async () =>
{
    using var f = new Fixture();
    Assert((await f.Store.LoadAsync()).Count == 0 && !File.Exists(f.Paths.Bookmarks));
});
await Check("Real DPAPI persists names and exact identities across store instances", async () =>
{
    using var f = new Fixture(new WindowsConfigurationProtector());
    var original = Entry();
    await f.Store.AddAsync(original);
    var loaded = await new BookmarkStore(f.Paths).LoadAsync();
    Assert(loaded.Single() == original);
    string disk = await File.ReadAllTextAsync(f.Paths.Bookmarks);
    Assert(!disk.Contains(original.Name) && !disk.Contains(original.AccountId));
});
await Check("Concurrent adds and duplicate adds preserve every account/drive/item identity", async () =>
{
    using var f = new Fixture();
    var other = new BookmarkStore(f.Paths, f.Protector);
    var a = Entry();
    var b = a with { AccountId = "account-B" };
    var c = a with { DriveId = "drive-B" };
    await Task.WhenAll(f.Store.AddAsync(a), other.AddAsync(b), f.Store.AddAsync(c), other.AddAsync(a));
    Assert((await f.Store.LoadAsync()).Count == 3);
    await f.Store.RemoveAsync(b);
    Assert((await f.Store.LoadAsync()).Select(x => x.Identity).ToHashSet().SetEquals(new[] { a.Identity, c.Identity }));
    await f.Store.RemoveAsync(b);
    Assert((await f.Store.LoadAsync()).Count == 2);
});
await Check("Metadata updates preserve saved time and cannot resurrect a removed bookmark", async () =>
{
    using var f = new Fixture();
    var a = Entry();
    await f.Store.AddAsync(a);
    await f.Store.UpdateMetadataAsync(a with { Name = "renamed.txt", AddedAt = a.AddedAt.AddHours(1) });
    Assert((await f.Store.LoadAsync()).Single() == a with { Name = "renamed.txt" });
    await f.Store.RemoveAsync(a);
    await f.Store.UpdateMetadataAsync(a with { Name = "late.txt" });
    Assert((await f.Store.LoadAsync()).Count == 0);
});
await Check("Failed encryption or destination replacement preserves prior bytes; retry succeeds", async () =>
{
    var protector = new Protector();
    using var f = new Fixture(protector);
    await f.Store.AddAsync(Entry());
    byte[] before = await File.ReadAllBytesAsync(f.Paths.Bookmarks);
    protector.Fail = true;
    await Throws<ConfigurationException>(() => f.Store.AddAsync(Entry("second")));
    Assert(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Bookmarks)));
    protector.Fail = false;
    using (var locked = new FileStream(f.Paths.Bookmarks, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { await f.Store.AddAsync(Entry("second")); throw new Exception("Expected replacement failure"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    Assert(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Bookmarks)));
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.Bookmarks), "*.tmp").Length == 0);
    await f.Store.AddAsync(Entry("second"));
    Assert((await f.Store.LoadAsync()).Count == 2);
});
await Check("Corrupt data blocks writes without overwriting; previous backup restores explicitly", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    await f.Store.AddAsync(Entry("second"));
    await File.WriteAllTextAsync(f.Paths.Bookmarks, "broken");
    await Throws<ConfigurationException>(() => f.Store.LoadAsync());
    await Throws<ConfigurationException>(() => f.Store.AddAsync(Entry("third")));
    Assert(await File.ReadAllTextAsync(f.Paths.Bookmarks) == "broken");
    await f.Store.RestoreBackupAsync();
    Assert((await f.Store.LoadAsync()).Single().ItemId == "item-A");
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.Bookmarks), "bookmarks.dat.recovery-*").Length == 1);
});
await Check("Rebuild requires damaged data and preserves both recovery copies", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    await Throws<ConfigurationException>(() => f.Store.RebuildAsync());
    await f.Store.AddAsync(Entry("second"));
    await File.WriteAllTextAsync(f.Paths.Bookmarks, "broken");
    await f.Store.RebuildAsync();
    Assert((await f.Store.LoadAsync()).Count == 0);
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.Bookmarks), "bookmarks.dat.recovery-*").Length == 2);
});
await Check("Unknown versions, required fields and duplicate records are rejected", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    string valid = await File.ReadAllTextAsync(f.Paths.Bookmarks);
    var envelope = JsonNode.Parse(valid);
    envelope["Version"] = 99;
    await File.WriteAllTextAsync(f.Paths.Bookmarks, envelope.ToJsonString());
    await Throws<ConfigurationException>(() => f.Store.LoadAsync());
    foreach (bool duplicate in new[] { false, true })
    {
        envelope = JsonNode.Parse(valid);
        var inner = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"].GetValue<string>()));
        if (duplicate) inner["Data"].AsArray().Add(inner["Data"][0].DeepClone());
        else inner["Data"][0].AsObject().Remove("IsFolder");
        envelope["Payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner.ToJsonString()));
        await File.WriteAllTextAsync(f.Paths.Bookmarks, envelope.ToJsonString());
        await Throws<ConfigurationException>(() => f.Store.LoadAsync());
    }
});
await Check("Invalid bookmarks and precancelled calls never commit", async () =>
{
    using var f = new Fixture();
    foreach (var invalid in new[] { Entry() with { AccountId = "" }, Entry() with { ItemId = "" }, Entry() with { AddedAt = default } })
        await Throws<ConfigurationException>(() => f.Store.AddAsync(invalid));
    using var token = new CancellationTokenSource();
    token.Cancel();
    await Throws<OperationCanceledException>(() => f.Store.AddAsync(Entry(), token.Token));
    Assert(!File.Exists(f.Paths.Bookmarks));
});
await Check("Offline page lists, orders, searches and removes only local bookmarks", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry() with { Name = "Report.txt", DriveName = "Personal" });
    await f.Store.AddAsync(Entry("other") with { Name = "Plan.pdf", DriveName = "Work", AddedAt = Entry().AddedAt.AddDays(1) });
    var resolver = new Resolver { Resolve = (_, _) => throw new Exception("No network for listing") };
    var model = new BookmarkViewModel(f.Store, resolver);
    await model.ActivateAsync();
    Assert(model.Items.Count == 2 && model.Items[0].Name == "Plan.pdf");
    model.Query = "  personal  ";
    Assert(model.Items.Single().Name == "Report.txt");
    model.Query = "absent";
    Assert(model.HasNoMatches && !model.IsEmpty);
    model.Query = "";
    await model.RemoveAsync(model.Items[0]);
    Assert(model.Items.Count == 1 && (await f.Store.LoadAsync()).Count == 1);
    model.Deactivate();
});
await Check("Read failure is not an empty list; explicit recovery enables the page", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    await File.WriteAllTextAsync(f.Paths.Bookmarks, "broken");
    var model = new BookmarkViewModel(f.Store, new Resolver());
    await model.ActivateAsync();
    Assert(model.HasError && model.NeedsRecovery && !model.IsEmpty && !model.CanUseItems);
    await model.RebuildAsync();
    Assert(!model.HasError && !model.NeedsRecovery && model.IsEmpty);
    model.Deactivate();
});
await Check("Remove failure leaves the row and allows retry", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    var store = new SwitchableStore(f.Store);
    var model = new BookmarkViewModel(store, new Resolver());
    await model.ActivateAsync();
    store.Fail = true;
    await model.RemoveAsync(model.Items[0]);
    Assert(model.HasError && model.Items.Count == 1 && (await f.Store.LoadAsync()).Count == 1);
    store.Fail = false;
    await model.RemoveAsync(model.Items[0]);
    Assert(model.IsEmpty && !model.HasError);
    model.Deactivate();
});
await Check("Invalid target stays saved with per-item error; renamed target persists and navigates", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    var resolver = new Resolver { Resolve = (_, _) => throw new BookmarkException("Bookmarks_NotFound") };
    var model = new BookmarkViewModel(f.Store, resolver);
    BookmarkLocation navigation = null;
    model.NavigationRequested += target => navigation = target;
    await model.ActivateAsync();
    await model.OpenAsync(model.Items[0]);
    Assert(model.Items[0].HasError && navigation == null && (await f.Store.LoadAsync()).Count == 1);
    resolver.Resolve = (b, _) => Task.FromResult(new BookmarkLocation(b with { Name = "renamed.txt" }, "moved-parent", b.ItemId));
    await model.OpenAsync(model.Items[0]);
    Assert(navigation.FolderId == "moved-parent" && (await f.Store.LoadAsync()).Single().Name == "renamed.txt");
    model.Deactivate();
});
await Check("Busy double-open is ignored; navigating away cancels and suppresses late navigation", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    var pending = new TaskCompletionSource<BookmarkLocation>();
    int calls = 0, navigations = 0;
    CancellationToken received = default;
    var resolver = new Resolver { Resolve = (_, token) => { calls++; received = token; return pending.Task; } };
    var model = new BookmarkViewModel(f.Store, resolver);
    model.NavigationRequested += _ => navigations++;
    await model.ActivateAsync();
    var open = model.OpenAsync(model.Items[0]);
    await model.OpenAsync(model.Items[0]);
    Assert(model.IsBusy && calls == 1 && !model.CanUseItems);
    model.Deactivate();
    await open.WaitAsync(TimeSpan.FromSeconds(2));
    pending.SetResult(new(Entry(), "parent", "item-A"));
    await Task.Delay(20);
    Assert(received.IsCancellationRequested && navigations == 0 && !model.HasError);
});
await Check("Metadata write failure does not block opening and reports a notice", async () =>
{
    using var f = new Fixture();
    await f.Store.AddAsync(Entry());
    var store = new SwitchableStore(f.Store);
    var resolver = new Resolver { Resolve = (b, _) => Task.FromResult(new BookmarkLocation(b with { Name = "new.txt" }, "parent", b.ItemId)) };
    var model = new BookmarkViewModel(store, resolver);
    BookmarkLocation navigation = null;
    model.NavigationRequested += value => navigation = value;
    await model.ActivateAsync();
    store.Fail = true;
    await model.OpenAsync(model.Items[0]);
    Assert(navigation?.Notice == "Bookmarks_MetadataNotSaved".GetLocalized());
    Assert((await f.Store.LoadAsync()).Single().Name == Entry().Name);
    model.Deactivate();
});
await Check("Resolver uses original account and current parent/name, never a saved path", async () =>
{
    using var f = new Fixture();
    await f.Drives.AddAsync(Drive("account-A"), 0);
    await f.Drives.AddAsync(Drive("account-B"), 1);
    var auth = new Authentication();
    var body = "{\"id\":\"item-A\",\"name\":\"renamed.txt\",\"file\":{},\"parentReference\":{\"id\":\"moved-parent\",\"driveId\":\"drive-A\"}}";
    var resolver = ResolverFor(f, auth, request =>
    {
        Assert(request.RequestUri.AbsolutePath.EndsWith("/drives/drive-A/items/item-A"));
        return Response(HttpStatusCode.OK, body);
    });
    var result = await resolver.ResolveAsync(Entry(), default);
    Assert(result.FolderId == "moved-parent" && result.SelectedItemId == "item-A" && result.Bookmark.Name == "renamed.txt");
    await resolver.ResolveAsync(Entry() with { AccountId = "account-B" }, default);
    Assert(auth.Accounts.SequenceEqual(new[] { "account-A", "account-B" }));
    body = "{\"id\":\"item-A\",\"name\":\"Folder\",\"folder\":{},\"parentReference\":{\"id\":\"root\",\"driveId\":\"drive-A\"}}";
    result = await resolver.ResolveAsync(Entry(), default);
    Assert(result.FolderId == "item-A" && result.SelectedItemId == null && result.Bookmark.IsFolder);
});
await Check("Missing account, expired sign-in and mismatched tokens never fall back", async () =>
{
    using var f = new Fixture();
    var auth = new Authentication();
    int requests = 0;
    var resolver = ResolverFor(f, auth, _ => { requests++; throw new Exception("No HTTP expected"); });
    await Error(() => resolver.ResolveAsync(Entry(), default), "Bookmarks_AccountUnavailable");
    await f.Drives.AddAsync(Drive("account-A"), 0);
    auth.Failure = AuthenticationFailure.RequiresSignIn;
    await Error(() => resolver.ResolveAsync(Entry(), default), "Bookmarks_SignInRequired");
    auth.Failure = null;
    auth.WrongAccount = true;
    await Error(() => resolver.ResolveAsync(Entry(), default), "Bookmarks_OpenFailed");
    Assert(requests == 0);
});
await Check("Resolver rejects foreign IDs, remote shortcuts, deleted items and missing parent", async () =>
{
    using var f = new Fixture();
    await f.Drives.AddAsync(Drive("account-A"), 0);
    foreach (var (body, key) in new[]
    {
        ("{\"id\":\"wrong\",\"name\":\"x\",\"file\":{}}", "Bookmarks_InvalidTarget"),
        ("{\"id\":\"item-A\",\"name\":\"x\",\"remoteItem\":{},\"folder\":{}}", "Bookmarks_InvalidTarget"),
        ("{\"id\":\"item-A\",\"name\":\"x\",\"folder\":{},\"parentReference\":{\"driveId\":\"foreign\"}}", "Bookmarks_InvalidTarget"),
        ("{\"id\":\"item-A\",\"name\":\"x\",\"file\":{}}", "Bookmarks_InvalidTarget"),
        ("{\"id\":\"item-A\",\"deleted\":{}}", "Bookmarks_NotFound")
    })
        await Error(() => ResolverFor(f, new Authentication(), _ => Response(HttpStatusCode.OK, body)).ResolveAsync(Entry(), default), key);
});
await Check("HTTP failures and cancellation remain actionable without exposing server details", async () =>
{
    using var f = new Fixture();
    await f.Drives.AddAsync(Drive("account-A"), 0);
    foreach (var (status, key) in new[] { (401, "Bookmarks_SignInRequired"), (403, "Bookmarks_AccessDenied"), (404, "Bookmarks_NotFound"), (429, "Bookmarks_Throttled") })
        await Error(() => ResolverFor(f, new Authentication(), _ => Response((HttpStatusCode)status,
            "{\"error\":{\"code\":\"failure\",\"message\":\"private server detail\"}}")).ResolveAsync(Entry(), default), key);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => ResolverFor(f, new Authentication(), _ => throw new Exception()).ResolveAsync(Entry(), cancellation.Token));
});
await Check("Both languages cover every bookmark UID and status", () =>
{
    var languages = new[] { "en-US", "zh-CN" }.Select(lang => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", lang, "Resources.resw"))
        .Root.Elements("data").ToDictionary(x => (string)x.Attribute("name"), x => x.Element("value").Value)).ToArray();
    Assert(languages[0].Keys.Where(k => k.StartsWith("Bookmarks_")).ToHashSet().SetEquals(languages[1].Keys.Where(k => k.StartsWith("Bookmarks_"))));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    foreach (var uid in XDocument.Load(Path.Combine(AppContext.BaseDirectory, "BookmarkPage.xaml")).Descendants().Attributes(x + "Uid"))
        foreach (var language in languages) Assert(language.Any(pair => pair.Key.StartsWith(uid.Value + ".") && pair.Value.Length > 0));
    foreach (var language in languages) Assert(string.Format(language["Bookmarks_Count"], 2, 3).Contains('3'));
    return Task.CompletedTask;
});
Console.WriteLine($"PASS: {passed} bookmark regression checks");

async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static Bookmark Entry(string id = "item-A") => new() { AccountId = "account-A", DriveId = "drive-A", ItemId = id, Name = "Fictional report.txt", DriveName = "Example drive", AddedAt = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero) };
static DriveDTO Drive(string account) => new() { DisplayName = "Same name", Provider = new() { HomeAccountId = account, DriveId = "drive-A" } };
static BookmarkResolver ResolverFor(Fixture f, Authentication auth, Func<HttpRequestMessage, HttpResponseMessage> reply) => new(f.Drives, auth,
    (authentication, account) => new GraphServiceClient(new HttpClient(new Handler(reply)), new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(authentication, account))));
static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { } }
static async Task Error(Func<Task> action, string key) { try { await action(); throw new Exception("Expected " + key); } catch (BookmarkException e) { Assert(e.ResourceKey == key); } }

sealed class Fixture : IDisposable
{
    public ApplicationDataPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "bookmark-test-" + Guid.NewGuid().ToString("N")), Path.Combine(Path.GetTempPath(), "no-legacy-" + Guid.NewGuid().ToString("N")));
    public IConfigurationProtector Protector { get; }
    public BookmarkStore Store { get; }
    public DriveConfigurationStore Drives { get; }
    public Fixture(IConfigurationProtector protector = null) { Protector = protector ?? new Protector(); Store = new(Paths, Protector); Drives = new(Paths, Protector); }
    public void Dispose() { if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true); }
}
sealed class Protector : IConfigurationProtector
{
    public bool Fail;
    public byte[] Protect(byte[] data, string purpose) => Fail ? throw new CryptographicException() : data.ToArray();
    public byte[] Unprotect(byte[] data, string purpose) => data.ToArray();
}
sealed class Resolver : IBookmarkResolver
{
    public Func<Bookmark, CancellationToken, Task<BookmarkLocation>> Resolve = (_, _) => throw new Exception("Not configured");
    public Task<BookmarkLocation> ResolveAsync(Bookmark bookmark, CancellationToken token) => Resolve(bookmark, token);
}
sealed class SwitchableStore(IBookmarkStore inner) : IBookmarkStore
{
    public bool Fail;
    public Task<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken token = default) => inner.LoadAsync(token);
    public Task AddAsync(Bookmark b, CancellationToken token = default) => Fail ? throw new IOException() : inner.AddAsync(b, token);
    public Task RemoveAsync(Bookmark b, CancellationToken token = default) => Fail ? throw new IOException() : inner.RemoveAsync(b, token);
    public Task UpdateMetadataAsync(Bookmark b, CancellationToken token = default) => Fail ? throw new IOException() : inner.UpdateMetadataAsync(b, token);
    public Task RestoreBackupAsync(CancellationToken token = default) => inner.RestoreBackupAsync(token);
    public Task RebuildAsync(CancellationToken token = default) => inner.RebuildAsync(token);
}
sealed class Authentication : IAccountAuthenticationService
{
    public List<string> Accounts { get; } = new();
    public AuthenticationFailure? Failure;
    public bool WrongAccount;
    public Task<AccountToken> AcquireSilentAsync(string account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Accounts.Add(account);
        if (Failure.HasValue) throw new AccountAuthenticationException(Failure.Value);
        return Task.FromResult(new AccountToken(WrongAccount ? "wrong-account" : account, "fictional-token"));
    }
    public Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token) => throw new Exception("No interactive auth expected");
}
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
}
