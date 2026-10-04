using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;

internal static class HomeBookmarkChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases => new (string, Func<Task>)[]
    {
        ("Recent bookmarks are local, newest first, capped at three, with exact account identity", Recent),
        ("Local bookmarks remain usable while sync initialization is pending", IndependentLoad),
        ("Bookmark damage does not hide quota or transfer summaries; refresh recovers", IndependentError),
        ("Drive configuration errors do not hide saved bookmarks or bypass account resolution", Resolution),
        ("Returning Home reloads added/removed bookmarks; old resolution cannot navigate", Reentry),
        ("Leaving Home cancels pending bookmark loading; late rows cannot overwrite reentry", LateLoad)
    };

    private static async Task Recent()
    {
        using var f = new Fixture();
        f.BookmarkStore.Records = Enumerable.Range(0, 5).Select(Entry).ToList();
        f.BookmarkStore.Records[4] = Entry(4) with { AccountId = "account-B", ItemId = "item-3" };
        await f.Model.ActivateAsync();
        var items = f.Model.Bookmarks.RecentItems;
        Assert(items.Count == 3 && items[0].Bookmark.AccountId == "account-B" && items[1].Bookmark.AccountId == "account-A");
        Assert(items[0].Bookmark.ItemId == items[1].Bookmark.ItemId && f.BookmarkResolver.Calls == 0 && f.BookmarkStore.Loads == 1);
        f.Model.Bookmarks.Query = "not-found";
        Assert(f.Model.Bookmarks.Items.Count == 0 && f.Model.Bookmarks.RecentItems.Count == 3);
    }

    private static async Task IndependentLoad()
    {
        using var f = new Fixture();
        f.BookmarkStore.Records.Add(Entry(0));
        var pending = new TaskCompletionSource();
        f.Sync.Initialize = () => pending.Task;
        var loading = f.Model.ActivateAsync();
        Assert(!loading.IsCompleted && f.Model.Bookmarks.CanUseItems && f.Model.Bookmarks.RecentItems.Count == 1);
        f.Model.Deactivate();
        pending.SetResult();
        await loading;
        Assert(f.Service.LoadCalls == 0);
    }

    private static async Task IndependentError()
    {
        using var f = new Fixture();
        f.Service.Drives = new[] { new HomeDrive("account-A", "drive-A", "Example") };
        f.Tasks.UploadTasks.Add(new() { State = UploadTaskState.Uploading });
        f.BookmarkStore.Load = _ => throw new ConfigurationException(ConfigurationFailure.Invalid);
        await f.Model.ActivateAsync();
        Assert(f.Model.Bookmarks.HasError && !f.Model.Bookmarks.IsEmpty && f.Model.Bookmarks.NeedsRecovery);
        Assert(!f.Model.HasError && f.Model.Drives[0].UsagePercent == 10 && f.Model.ActiveTransfers == 1);
        f.BookmarkStore.Load = null;
        f.BookmarkStore.Records.Add(Entry(1));
        await f.Model.RefreshCommand.ExecuteAsync(null);
        Assert(!f.Model.Bookmarks.HasError && f.Model.Bookmarks.CanUseItems && f.Model.Bookmarks.RecentItems.Count == 1);
    }

    private static async Task Resolution()
    {
        using var f = new Fixture();
        f.Service.LoadError = new HomeOverviewException("Configuration_Invalid");
        f.BookmarkStore.Records.Add(Entry(0));
        f.BookmarkResolver.Resolve = (_, _) => throw new BookmarkException("Bookmarks_AccountConfiguration");
        await f.Model.ActivateAsync();
        var row = f.Model.Bookmarks.RecentItems.Single();
        int navigation = 0;
        f.Model.Bookmarks.NavigationRequested += _ => navigation++;
        await f.Model.Bookmarks.OpenAsync(row);
        Assert(f.Model.HasError && row.HasError && navigation == 0 && f.BookmarkStore.Records.Count == 1);
        f.BookmarkResolver.Resolve = (entry, _) => Task.FromResult(new BookmarkLocation(entry with { Name = "Moved.txt" }, "new-parent", entry.ItemId));
        await f.Model.Bookmarks.OpenAsync(row);
        Assert(navigation == 1 && !row.HasError && row.Name == "Moved.txt" && f.BookmarkStore.Records.Single().Name == "Moved.txt");
    }

    private static async Task Reentry()
    {
        using var f = new Fixture();
        f.BookmarkStore.Records.Add(Entry(0));
        await f.Model.ActivateAsync();
        var pending = new TaskCompletionSource<BookmarkLocation>();
        CancellationToken received = default;
        f.BookmarkResolver.Resolve = (_, token) => { received = token; return pending.Task; };
        int navigations = 0;
        f.Model.Bookmarks.NavigationRequested += _ => navigations++;
        var opening = f.Model.Bookmarks.OpenAsync(f.Model.Bookmarks.RecentItems[0]);
        f.Model.Deactivate();
        await opening.WaitAsync(TimeSpan.FromSeconds(2));
        f.BookmarkStore.Records = new() { Entry(1) };
        await f.Model.ActivateAsync();
        pending.SetResult(new(Entry(0), "parent", "item-0"));
        Assert(received.IsCancellationRequested && navigations == 0 && f.Model.Bookmarks.RecentItems.Single().Bookmark.ItemId == "item-1");
    }

    private static async Task LateLoad()
    {
        using var f = new Fixture();
        var pending = new TaskCompletionSource<IReadOnlyList<Bookmark>>();
        f.BookmarkStore.Load = _ => pending.Task;
        var loading = f.Model.ActivateAsync();
        f.Model.Deactivate();
        await loading.WaitAsync(TimeSpan.FromSeconds(2));
        f.BookmarkStore.Load = null;
        f.BookmarkStore.Records.Add(Entry(2));
        await f.Model.ActivateAsync();
        pending.SetResult(new[] { Entry(0) });
        Assert(f.Model.Bookmarks.RecentItems.Single().Bookmark.ItemId == "item-2" && !f.Model.Bookmarks.HasError);
    }

    private static Bookmark Entry(int index) => new()
    {
        AccountId = "account-A", DriveId = "drive-A", ItemId = "item-" + index,
        Name = "Example " + index, DriveName = "Example", AddedAt = DateTimeOffset.UnixEpoch.AddMinutes(index)
    };
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Home bookmark assertion failed"); }
}

internal sealed class MemoryBookmarks : IBookmarkStore
{
    public List<Bookmark> Records = new();
    public int Loads;
    public Func<CancellationToken, Task<IReadOnlyList<Bookmark>>> Load;
    public Task<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken token = default)
    { Loads++; return Load?.Invoke(token) ?? Task.FromResult<IReadOnlyList<Bookmark>>(Records.ToArray()); }
    public Task AddAsync(Bookmark bookmark, CancellationToken token = default) { Records.Add(bookmark); return Task.CompletedTask; }
    public Task RemoveAsync(Bookmark bookmark, CancellationToken token = default) { Records.RemoveAll(b => b.Identity == bookmark.Identity); return Task.CompletedTask; }
    public Task UpdateMetadataAsync(Bookmark bookmark, CancellationToken token = default)
    { int index = Records.FindIndex(b => b.Identity == bookmark.Identity); if (index >= 0) Records[index] = bookmark; return Task.CompletedTask; }
    public Task RestoreBackupAsync(CancellationToken token = default) => throw new NotSupportedException();
    public Task RebuildAsync(CancellationToken token = default) => throw new NotSupportedException();
}

internal sealed class MemoryBookmarkResolver : IBookmarkResolver
{
    public int Calls;
    public Func<Bookmark, CancellationToken, Task<BookmarkLocation>> Resolve = (b, _) => Task.FromResult(new BookmarkLocation(b, "parent", b.ItemId));
    public Task<BookmarkLocation> ResolveAsync(Bookmark bookmark, CancellationToken token) { Calls++; return Resolve(bookmark, token); }
}
