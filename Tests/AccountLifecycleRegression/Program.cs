using Microsoft.Identity.Client;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

internal static class Program
{
    private static int _passed;
    private static async Task Main()
    {
        await Case("Sign-out persists, pauses both account drives, retains bookmarks/files and isolates account B", async f =>
        {
            await f.Service.ExecuteAsync(new("A"));
            Check((await f.Drives.LoadAsync()).Data.Count == 3, "Drive configuration was lost");
            Check((await f.Bookmarks.LoadAsync()).Count == 3, "Bookmarks were lost");
            var bindings = (await f.SyncStore.LoadAsync(default)).Bindings;
            Check(bindings.Where(x => x.AccountId == "A").All(x => !x.Enabled) && bindings.Single(x => x.AccountId == "B").Enabled, "Sync scope was wrong");
            Check(f.Auth.Removed.SequenceEqual(new[] { "A" }), "Wrong credential removal");
            Check(!File.Exists(f.CacheFiles["A/1"]) && !File.Exists(f.CacheFiles["A/2"]) && File.Exists(f.CacheFiles["B/1"]), "Cache scope was wrong");
            var reopened = new AccountAccessState(f.Paths); await reopened.InitializeAsync();
            Check(reopened.IsBlocked("A", "1") && !reopened.IsBlocked("B", "1") && reopened.Pending.Count == 0, "Restart revived signed-out account");
            await reopened.ResumeAccountAsync("A", reopened.Generation);
            Check(!reopened.IsBlocked("A", "1") && (await f.SyncStore.LoadAsync(default)).Bindings.Where(x => x.AccountId == "A").All(x => !x.Enabled), "Login resumed sync without consent");
            f.CheckFiles();
        });
        await Case("Signed-out sync cannot be manually enabled before a new sign-in", async f =>
        {
            await f.Service.ExecuteAsync(new("A"));
            var job = f.Sync.Jobs.First(x => x.Binding.AccountId == "A");
            await Throws<AccountAccessException>(() => job.SetEnabledAsync(true));
            Check(!job.Binding.Enabled, "Resume persisted while account was signed out");
            await f.Access.ResumeAccountAsync("A", f.Access.Generation);
            await job.SetEnabledAsync(true);
            Check(job.Binding.Enabled, "Explicit resume after sign-in failed");
        });
        await Case("Removing one drive preserves same-account siblings, credentials and bookmarks", async f =>
        {
            await f.Service.ExecuteAsync(new("A", "1"));
            Check((await f.Drives.LoadAsync()).Data.Select(DriveConfigurationStore.Identity).ToHashSet().SetEquals(new[] { ("A", "2"), ("B", "1") }), "Wrong drives removed");
            Check(f.Sync.Jobs.Count == 2 && (await f.SyncStore.LoadAsync(default)).Bindings.Count == 2, "Bindings resurrected");
            Check((await f.Bookmarks.LoadAsync()).Count == 3 && f.Auth.Removed.Count == 0, "Credentials or bookmarks removed");
            Check(f.Access.IsBlocked("A", "1") && !f.Access.IsBlocked("A", "2") && !f.Access.IsBlocked("B", "1"), "Access scope was wrong");
            Check(!File.Exists(f.CacheFiles["A/1"]) && File.Exists(f.CacheFiles["A/2"]) && File.Exists(f.CacheFiles["B/1"]), "Wrong cache cleared");
            f.CheckFiles();
        });
        await Case("Last-drive removal clears only its credentials and optional bookmarks", async f =>
        {
            await f.Service.ExecuteAsync(new("B", "1"), true);
            Check(f.Auth.Removed.SequenceEqual(new[] { "B" }), "Wrong credentials");
            Check((await f.Bookmarks.LoadAsync()).All(x => x.AccountId == "A"), "Wrong bookmarks");
            Check(!f.Access.IsBlocked("A", "1") && f.Access.IsBlocked("B", "1"), "Another account was blocked");
            f.CheckFiles();
        });
        await Case("Scoped HTTP requests cancel, drain, reject late responses, and keep other requests alive", async f =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new AccountRequestHandler(f.Access, "A", "1")
            { InnerHandler = new BoundaryHandler(async _ => { started.SetResult(); await release.Task; return new(HttpStatusCode.OK); }) };
            using var client = new HttpClient(handler);
            Task request = client.GetAsync("https://graph.microsoft.com/v1.0/drives/1");
            await started.Task;
            using var other = f.Access.Enter("B", "1");
            await f.Access.BeginAsync(new("A", "1"), false);
            Task drain = f.Access.DrainAsync(new("A", "1"));
            Check(!drain.IsCompleted && !other.Token.IsCancellationRequested, "Drain or isolation failed");
            release.SetResult();
            await Throws<OperationCanceledException>(() => request);
            await drain;
            await Throws<AccountAccessException>(() => client.GetAsync("https://graph.microsoft.com/v1.0/drives/1"));
        });
        await Case("A late interactive result cannot undo exit; another account can still sign in", async f =>
        {
            long started = f.Access.Generation;
            await f.Service.ExecuteAsync(new("A"));
            await Throws<AccountAccessException>(() => f.Access.ResumeAccountAsync("A", started));
            await f.Access.ResumeAccountAsync("B", started);
            Check(f.Access.IsBlocked("A") && !f.Access.IsBlocked("B"), "Late login revived A");
        });
        await Case("Exit cancels pending sign-in and waits for it, without cancelling another account", async f =>
        {
            var signingIn = f.Access.EnterInteractive("A", default);
            using var other = f.Access.EnterInteractive("B", default);
            await f.Access.BeginAsync(new("A", "1"), false);
            Task drain = f.Access.DrainAsync(new("A", "1"));
            Check(signingIn.Token.IsCancellationRequested && !other.Token.IsCancellationRequested && !drain.IsCompleted, "Sign-in cancellation scope was wrong");
            signingIn.Dispose(); await drain;
        });
        await Case("Credential removal precedes a cache failure and denial survives the unfinished cleanup", async f =>
        {
            f.Effects.FailStage = "Cache";
            await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("B")));
            Check(f.Auth.Removed.Contains("B") && f.Access.IsBlocked("B") && f.Access.Pending.Count == 1, "Cache failure retained usable credentials");
        });
        await Case("Pending cleanup survives restart and retries idempotently without resuming access", async f =>
        {
            f.Effects.FailStage = "Cache";
            var error = await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("A", "1"), true));
            Check(error.Stage == "Cache" && f.Access.Pending.Count == 1, "Failure was hidden");
            Check((await f.Drives.LoadAsync()).Data.Count == 3 && f.Sync.Jobs.Count == 2, "Unexpected partial commit");
            var restarted = new AccountAccessState(f.Paths); await restarted.InitializeAsync();
            await Throws<AccountAccessException>(() => restarted.ResumeAccountAsync("A", restarted.Generation));
            f.Effects.FailStage = null;
            var service = new AccountLifecycleService(restarted, f.Drives, f.Auth, f.Effects);
            await service.RetryPendingAsync(); await service.RetryPendingAsync();
            Check(restarted.Pending.Count == 0 && restarted.IsBlocked("A", "1") && (await f.Drives.LoadAsync()).Data.Count == 2, "Retry did not converge");
            Check((await f.Bookmarks.LoadAsync()).Count == 2, "Bookmark removal not idempotent");
            f.CheckFiles();
        });
        await Case("Credential cleanup failure keeps access denied until retry", async f =>
        {
            f.Auth.Fail = true;
            Check((await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("B", "1")))).Stage == "Credentials", "Wrong failure stage");
            Check(f.Access.IsBlocked("B") && f.Access.Pending.Count == 1 && (await f.Drives.LoadAsync()).Data.Count == 3, "Failed credential cleanup reported success");
            f.Auth.Fail = false; await f.Service.RetryPendingAsync();
            Check((await f.Drives.LoadAsync()).Data.Count == 2 && f.Access.Pending.Count == 0, "Retry failed");
        });
        await Case("Each cleanup stage failure remains recoverable and preserves other-account files", async f =>
        {
            foreach (string stage in new[] { "Stop", "Sync", "Bookmarks" })
            {
                f.Effects.FailStage = stage;
                var error = await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("A", "1"), true));
                Check(error.Stage == stage && f.Access.Pending.Count == 1, "Missing stage failure");
                f.CheckFiles();
            }
            f.Effects.FailStage = null; await f.Service.RetryPendingAsync();
        });
        await Case("Locked cache retains ownership and retries without deleting unrelated or user files", async f =>
        {
            using (var locked = File.Open(f.CacheFiles["A/1"], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check((await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("A", "1")))).Stage == "Cache", "Locked cache silently succeeded");
                Check(File.Exists(f.CacheFiles["B/1"]), "Other cache removed");
            }
            await f.Service.RetryPendingAsync();
            f.CheckFiles();
        });
        await Case("Corrupt cache index fails closed without guessing ownership", async f =>
        {
            await File.WriteAllTextAsync(f.Paths.ReaderCacheIndex, "broken");
            await Throws<AccountLifecycleException>(() => f.Service.ExecuteAsync(new("A", "1")));
            Check(f.CacheFiles.Values.All(File.Exists), "Files removed without ownership index");
            Check(await File.ReadAllTextAsync(f.Paths.ReaderCacheIndex) == "broken", "Evidence overwritten");
        });
        await Case("Re-adding a removed drive requires explicit successful cleanup and preserves bookmarks", async f =>
        {
            await f.Service.ExecuteAsync(new("B", "1"));
            await f.Access.ResumeAccountAsync("B", f.Access.Generation);
            Check(f.Access.IsBlocked("B", "1"), "Sign-in restored removed drive implicitly");
            var current = await f.Drives.LoadAsync();
            await f.Drives.AddAsync(Drive("B", "1"), current.Revision);
            await f.Access.RestoreDriveAsync("B", "1");
            Check(!f.Access.IsBlocked("B", "1") && (await f.Bookmarks.LoadAsync()).Count == 3, "Re-add failed");
        });
        await Case("MSAL sign-out removes only the exact home account and is repeatable", async f =>
        {
            var app = DispatchProxy.Create<IPublicClientApplication, MsalBoundary>();
            var boundary = (MsalBoundary)(object)app;
            boundary.Accounts.AddRange(new IAccount[] { new FakeAccount("A"), new FakeAccount("B") });
            var authentication = new MsalAccountAuthenticationService(app);
            await authentication.SignOutAsync("A", default); await authentication.SignOutAsync("A", default);
            Check(boundary.Accounts.Single().HomeAccountId.Identifier == "B" && boundary.Removed.SequenceEqual(new[] { "A" }), "MSAL account isolation failed");
            Check((await authentication.GetAccountIdsAsync(default)).SequenceEqual(new[] { "B" }), "Cached accounts were not listed");
        });
        await Case("Broken access journal cannot silently become an active session", async f =>
        {
            await f.Service.ExecuteAsync(new("A"));
            string path = Path.Combine(f.Paths.Root, "Configuration", "account-access.dat");
            await File.WriteAllTextAsync(path, "broken");
            var reopened = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => reopened.InitializeAsync());
            Check(reopened.IsBlocked("A") && reopened.IsBlocked("B"), "Corrupt security state failed open");
        });
        await Case("Rebuilding a broken journal keeps the damaged copy and signs out every known account", async f =>
        {
            f.Auth.Accounts.AddRange(new[] { "A", "C" });
            string path = AccessPath(f);
            await File.WriteAllTextAsync(path, "broken");
            var reopened = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => reopened.InitializeAsync());
            var service = new AccountLifecycleService(reopened, f.Drives, f.Auth, f.Effects);
            await Throws<ConfigurationException>(() => reopened.ReadBackupAsync());
            await Throws<ConfigurationException>(() => service.RepairAccessAsync(true, Array.Empty<string>()));
            Check(await File.ReadAllTextAsync(path) == "broken" && reopened.IsBlocked("A"), "Failed restore changed the record or opened access");
            var syncAccounts = (await f.SyncStore.LoadAsync(default)).Bindings.Select(x => x.AccountId).Append("S");
            await service.RepairAccessAsync(false, syncAccounts);
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "account-access.dat.recovery-*").Length == 1, "Damaged record was not preserved");
            Check(reopened.Pending.Select(x => x.Scope).ToHashSet().SetEquals(new AccountScope[] { new("A"), new("B"), new("C"), new("S") }) &&
                reopened.Pending.All(x => x.ClearCredentials && !x.RemoveBookmarks), "Known accounts were not all journaled for sign-out");
            await Throws<AccountAccessException>(() => reopened.ResumeAccountAsync("A", reopened.Generation));
            await service.RetryPendingAsync();
            Check(reopened.Pending.Count == 0 && f.Auth.Removed.ToHashSet().SetEquals(new[] { "A", "B", "C", "S" }), "Credentials were not removed");
            Check((await f.SyncStore.LoadAsync(default)).Bindings.All(x => !x.Enabled), "Sync was not paused");
            Check((await f.Drives.LoadAsync()).Data.Count == 3 && (await f.Bookmarks.LoadAsync()).Count == 3, "Repair removed drives or bookmarks");
            var restarted = new AccountAccessState(f.Paths); await restarted.InitializeAsync();
            Check(new[] { "A", "B", "C" }.All(x => restarted.IsBlocked(x, "1")), "Repair restored access without sign-in");
            await restarted.ResumeAccountAsync("A", restarted.Generation);
            Check(!restarted.IsBlocked("A", "1") && (await f.SyncStore.LoadAsync(default)).Bindings.All(x => !x.Enabled), "Sign-in resumed sync automatically");
            f.CheckFiles();
        });
        await Case("Restoring a broken journal from backup keeps drive removals and still requires sign-in", async f =>
        {
            await f.Service.ExecuteAsync(new("A", "1"));
            await f.Service.ExecuteAsync(new("B", "1")); // Later writes move the A removal into the backup.
            string path = AccessPath(f);
            await File.WriteAllTextAsync(path, "broken");
            var reopened = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => reopened.InitializeAsync());
            Check((await reopened.ReadBackupAsync()).Any(x => x.Scope == new AccountScope("A", "1")), "Backup could not be inspected");
            var service = new AccountLifecycleService(reopened, f.Drives, f.Auth, f.Effects);
            await service.RepairAccessAsync(true, Array.Empty<string>());
            Check(reopened.IsRemoved("A", "1") && reopened.Pending.Any(x => x.Scope == new AccountScope("A")), "Backup removal or sign-out missing");
            await service.RetryPendingAsync();
            Check(reopened.Pending.Count == 0 && reopened.IsRemoved("A", "1") && reopened.IsBlocked("A", "2") && reopened.IsBlocked("B", "1"),
                "Restored record lost a removal or allowed access");
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "account-access.dat.recovery-*").Length == 1, "Damaged record was not preserved");
            f.CheckFiles();
        });
        await Case("A null payload is treated as damage and can be restored from backup", async f =>
        {
            await f.Service.ExecuteAsync(new("A", "1"));
            await f.Service.ExecuteAsync(new("B", "1")); // Later writes move the A removal into the backup.
            string path = AccessPath(f);
            await File.WriteAllTextAsync(path, "{\"Format\":\"OneDriveConfiguration\",\"Version\":1,\"Protection\":\"DPAPI-CurrentUser\",\"Payload\":null}");
            var reopened = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => reopened.InitializeAsync());
            await new AccountLifecycleService(reopened, f.Drives, f.Auth, f.Effects).RepairAccessAsync(true, Array.Empty<string>());
            Check(reopened.IsRemoved("A", "1") && reopened.Pending.Any(x => x.Scope == new AccountScope("A")), "Null payload blocked restore");
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "account-access.dat.recovery-*").Length == 1, "Damaged record was not preserved");
        });
        await Case("Oversized damaged files are set aside unchanged so restore and rebuild still work", async f =>
        {
            await f.Service.ExecuteAsync(new("A", "1"));
            await f.Service.ExecuteAsync(new("B", "1"));
            string path = AccessPath(f), directory = Path.GetDirectoryName(path);
            byte[] oversized = new byte[4 * 1024 * 1024 + 1];
            oversized[0] = (byte)'x';
            await File.WriteAllBytesAsync(path, oversized);
            var reopened = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => reopened.InitializeAsync());
            await new AccountLifecycleService(reopened, f.Drives, f.Auth, f.Effects).RepairAccessAsync(true, Array.Empty<string>());
            var archived = Directory.GetFiles(directory, "account-access.dat.recovery-*");
            Check(archived.Length == 1 && (await File.ReadAllBytesAsync(archived[0])).SequenceEqual(oversized), "Oversized original was not kept unchanged");
            Check(reopened.IsRemoved("A", "1"), "Restore from backup failed");
            // Rebuild must also get past an oversized backup that it does not use.
            await File.WriteAllBytesAsync(path, oversized);
            await File.WriteAllBytesAsync(path + ".bak", oversized);
            var again = new AccountAccessState(f.Paths);
            await Throws<ConfigurationException>(() => again.InitializeAsync());
            await new AccountLifecycleService(again, f.Drives, f.Auth, f.Effects).RepairAccessAsync(false, Array.Empty<string>());
            Check(Directory.GetFiles(directory, "account-access.dat.recovery-*").Length == 3 && !File.Exists(path + ".bak"), "Oversized files were not both set aside");
            Check(again.Pending.Any(x => x.Scope == new AccountScope("A")) && again.IsBlocked("A", "2"), "Rebuild did not require sign-in");
        });
        await Case("Repair refuses a readable journal and leaves it unchanged", async f =>
        {
            await f.Service.ExecuteAsync(new("A"));
            byte[] before = await File.ReadAllBytesAsync(AccessPath(f));
            var error = await Throws<ConfigurationException>(() => f.Service.RepairAccessAsync(false, Array.Empty<string>()));
            Check(error.Failure == ConfigurationFailure.Conflict && before.SequenceEqual(await File.ReadAllBytesAsync(AccessPath(f))), "Readable journal was overwritten");
        });
        await Case("Both languages contain the full lifecycle explanations and failure actions", f =>
        {
            foreach (var language in new[] { "en-US", "zh-CN" })
            {
                var data = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw")).Root.Elements("data")
                    .ToDictionary(x => (string)x.Attribute("name"), x => x.Element("value").Value);
                foreach (string key in new[] { "SignOutExplanation", "RemoveExplanation", "FileNotice", "Pending", "FailedStop", "FailedSync", "FailedBookmarks", "FailedCache", "FailedCredentials", "FailedConfiguration" })
                    Check(!string.IsNullOrWhiteSpace(data["AccountLifecycle_" + key]), "Missing localized lifecycle text");
                _ = string.Format(data["AccountLifecycle_SignOutExplanation"], "fictional", 2, 3, 4);
                foreach (string key in new[] { "Repair", "CheckBackup", "Restore", "Rebuild", "BackupReady", "BackupUnavailable", "RepairExplanation", "RepairAction", "RepairFailed", "Repaired" })
                    Check(!string.IsNullOrWhiteSpace(data["Startup_Access" + key]), "Missing localized access repair text");
                _ = string.Format(data["Startup_AccessBackupReady"], 2, 1);
            }
            return Task.CompletedTask;
        });
        Console.WriteLine($"Passed {_passed} account lifecycle regression checks.");
    }

    private static async Task Case(string name, Func<Fixture, Task> test)
    {
        await using var fixture = new Fixture(); await fixture.Initialize(); await test(fixture);
        Console.WriteLine("PASS " + name); _passed++;
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static string AccessPath(Fixture f) => Path.Combine(f.Paths.Root, "Configuration", "account-access.dat");
    private static DriveDTO Drive(string account, string drive) => new() { DisplayName = "Same name", Provider = new() { HomeAccountId = account, DriveId = drive } };

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDataPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "OneDriveLifecycleRegression", Guid.NewGuid().ToString("N")));
        public AccountAccessState Access;
        public DriveConfigurationStore Drives;
        public BookmarkStore Bookmarks;
        public FolderSyncStore SyncStore;
        public FolderSyncService Sync;
        public FakeAuthentication Auth = new();
        public Effects Effects;
        public AccountLifecycleService Service;
        public Dictionary<string, string> CacheFiles = new();
        private readonly List<string> _userFiles = new();
        public async Task Initialize()
        {
            Access = new(Paths); await Access.InitializeAsync();
            Drives = new(Paths); Bookmarks = new(Paths); SyncStore = new(Paths.FolderSync);
            var entries = new List<ReaderCacheEntry>();
            foreach (var (account, drive) in new[] { ("A", "1"), ("A", "2"), ("B", "1") })
            {
                var current = await Drives.LoadAsync(); await Drives.AddAsync(Drive(account, drive), current.Revision);
                await Bookmarks.AddAsync(new() { AccountId = account, DriveId = drive, ItemId = "book", Name = "Book", DriveName = "Same name", AddedAt = DateTimeOffset.UtcNow });
                string local = Path.Combine(Paths.Root, "UserFiles", account + drive); Directory.CreateDirectory(local);
                string sentinel = Path.Combine(local, "keep.txt"); await File.WriteAllTextAsync(sentinel, "untouched"); _userFiles.Add(sentinel);
                await SyncStore.SaveAsync(new() { AccountId = account, DriveId = drive, FolderName = account + drive, LocalPath = local,
                    RemoteFolderId = "root", RemotePath = "/", Enabled = true }, default);
                Directory.CreateDirectory(Paths.ReaderCache);
                string cacheName = Guid.NewGuid().ToString("N") + ".epub";
                string cache = Path.Combine(Paths.ReaderCache, cacheName); await File.WriteAllTextAsync(cache, "cache"); CacheFiles[account + "/" + drive] = cache;
                var identity = new ReaderIdentity("onedrive", account, drive, "book");
                entries.Add(new(identity, "version", cacheName, new string('A', 64), 5, DateTimeOffset.UtcNow));
                await new ReaderStore(Paths).SaveProgressAsync(new(identity, "version", new(null, 0.5), DateTimeOffset.UtcNow), default);
            }
            var index = new ProtectedConfigurationFile<List<ReaderCacheEntry>>(Paths.ReaderCacheIndex, "reader-cache", ReaderJsonContext.Default.ListReaderCacheEntry, _ => { }, 4 * 1024 * 1024);
            await index.WriteAsync(entries, 1, false, default);
            Sync = new(SyncStore, _ => new Target(), Access); await Sync.InitializeAsync();
            Effects = new(Sync, Bookmarks, new(Paths));
            Service = new(Access, Drives, Auth, Effects);
        }
        public void CheckFiles()
        {
            Check(_userFiles.All(x => File.ReadAllText(x) == "untouched"), "User file changed or deleted");
            Check(File.Exists(Paths.ReaderProgress), "Reading progress deleted");
        }
        public async ValueTask DisposeAsync()
        {
            if (Sync != null) await Task.WhenAll(Sync.Jobs.Select(x => x.StopAsync()));
            string root = Path.GetFullPath(Paths.Root);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OneDriveLifecycleRegression")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test path");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class Effects(FolderSyncService sync, IBookmarkStore bookmarks, ReaderCacheService cache) : IAccountLifecycleEffects
    {
        public string FailStage;
        private void Fail(string stage) { if (FailStage == stage) throw new IOException(); }
        public Task StopAsync(AccountScope scope) { Fail("Stop"); return Task.CompletedTask; }
        public Task UpdateSyncAsync(AccountScope scope, bool remove) { Fail("Sync"); return sync.UpdateAccountAsync(scope.AccountId, scope.DriveId, remove); }
        public async Task RemoveBookmarksAsync(AccountScope scope)
        {
            Fail("Bookmarks");
            foreach (var item in (await bookmarks.LoadAsync()).Where(x => scope.Contains(x.AccountId, x.DriveId))) await bookmarks.RemoveAsync(item);
        }
        public async Task ClearCacheAsync(AccountScope scope)
        {
            Fail("Cache"); if (await cache.ClearScopeAsync(scope.AccountId, scope.DriveId) != 0) throw new IOException();
        }
    }
    private sealed class Target : IFolderSyncTarget
    {
        public Task ValidateRootAsync(CancellationToken token) => Task.CompletedTask;
        public Task EnsureFolderAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task UploadAsync(string path, Stream content, IProgress<long> progress, CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class FakeAuthentication : IAccountAuthenticationService
    {
        public bool Fail;
        public List<string> Removed = new();
        public List<string> Accounts = new();
        public Task<IReadOnlyList<string>> GetAccountIdsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Accounts.ToArray());
        public Task<AccountToken> AcquireSilentAsync(string account, CancellationToken token) => throw new NotSupportedException();
        public Task<AccountToken> AcquireInteractiveAsync(string account, CancellationToken token) => throw new NotSupportedException();
        public Task SignOutAsync(string account, CancellationToken token) { if (Fail) throw new IOException(); Removed.Add(account); return Task.CompletedTask; }
    }
    private sealed class BoundaryHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }
    private sealed class FakeAccount(string id) : IAccount
    {
        public string Username => "fictional";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => new(id);
    }
}

public class MsalBoundary : DispatchProxy
{
    public List<IAccount> Accounts = new();
    public List<string> Removed = new();
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "GetAccountsAsync") return Task.FromResult<IEnumerable<IAccount>>(Accounts.ToArray());
        if (method.Name == "RemoveAsync") { var account = (IAccount)args[0]; Removed.Add(account.HomeAccountId.Identifier); Accounts.Remove(account); return Task.CompletedTask; }
        throw new NotSupportedException(method.Name);
    }
}
