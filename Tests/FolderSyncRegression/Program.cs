using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class Program
{
    private static int _checks;
    private static async Task<int> Main()
    {
        try { await RunAll(); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task RunAll()
    {
        await Case("Initial scan includes nested files, empty files and empty folders", async () =>
        {
            using var f = new Fixture();
            Directory.CreateDirectory(Path.Combine(f.Root, "nested", "empty"));
            await File.WriteAllTextAsync(Path.Combine(f.Root, "nested", "文档.txt"), "first");
            await File.WriteAllBytesAsync(Path.Combine(f.Root, "zero"), Array.Empty<byte>());
            var result = await f.Run();
            Assert(result.Issues.Count == 0 && result.Uploaded == 4, "Missing initial items");
            Assert(f.Target.Folders.Contains("nested/empty") && f.Target.Files["zero"].Length == 0, "Empty items missing");
            Assert(f.Target.Files.ContainsKey("nested/文档.txt"), "Incorrect relative path");
        });
        await Case("Unchanged files are skipped; same-size same-time edits are uploaded", async () =>
        {
            using var f = new Fixture();
            string file = Path.Combine(f.Root, "a.txt");
            await File.WriteAllTextAsync(file, "one");
            await f.Run();
            Assert((await f.Run()).Uploaded == 0, "Unchanged content uploaded");
            var time = File.GetLastWriteTimeUtc(file);
            await File.WriteAllTextAsync(file, "two");
            File.SetLastWriteTimeUtc(file, time);
            Assert((await f.Run()).Uploaded == 1 && Encoding.UTF8.GetString(f.Target.Files["a.txt"]) == "two", "Missed edit");
        });
        await Case("Deletion never removes cloud data; rename uploads new path", async () =>
        {
            using var f = new Fixture();
            string file = Path.Combine(f.Root, "a.txt");
            await File.WriteAllTextAsync(file, "one");
            await f.Run();
            File.Move(file, Path.Combine(f.Root, "b.txt"));
            await f.Run();
            File.Delete(Path.Combine(f.Root, "b.txt"));
            await f.Run();
            Assert(f.Target.Files.Count == 2 && f.Binding.Files.Count == 0, "Delete/rename semantics wrong");
            Assert(f.Binding.LastSuccess != null, "Missing success timestamp");
        });
        await Case("Cloud-only modifications do not download or trigger uploads", async () =>
        {
            using var f = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "local");
            await f.Run();
            f.Target.Files["a"] = Encoding.UTF8.GetBytes("remote");
            Assert((await f.Run()).Uploaded == 0 && await File.ReadAllTextAsync(Path.Combine(f.Root, "a")) == "local", "Cloud changes propagated");
        });
        await Case("Failed upload is not checkpointed and retries successfully", async () =>
        {
            using var f = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "local");
            f.Target.Fail = true;
            Assert((await f.Run()).Issues.Count == 1 && f.Binding.Files.Count == 0, "Failure marked successful");
            f.Target.Fail = false;
            Assert((await f.Run()).Uploaded == 1 && f.Binding.Files.Count == 1, "Retry failed");
        });
        await Case("Locked file is retained for retry", async () =>
        {
            using var f = new Fixture();
            string file = Path.Combine(f.Root, "a");
            await File.WriteAllTextAsync(file, "x");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
                Assert((await f.Run()).Issues.Count == 1 && f.Binding.Files.Count == 0, "Locked file skipped silently");
            Assert((await f.Run()).Uploaded == 1, "Unlock not recovered");
        });
        await Case("Root name mismatch and missing root stop before uploads", async () =>
        {
            using var f = new Fixture();
            f.Binding.FolderName = "Different";
            await Expect<FolderSyncException>(() => f.Run());
            f.Binding.LocalPath += "-missing";
            await Expect<FolderSyncException>(() => f.Run());
            Assert(f.Target.Files.Count == 0, "Invalid root uploaded");
        });
        await Case("Cancellation stops uploads without recording success", async () =>
        {
            using var f = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "x");
            using var cancel = new CancellationTokenSource();
            f.Target.BeforeUpload = () => cancel.Cancel();
            await Expect<OperationCanceledException>(() => f.Run(cancel.Token));
            Assert(f.Binding.Files.Count == 0, "Canceled item recorded");
        });
        await Case("Atomic persisted manifest resumes without uploading unchanged files", async () =>
        {
            using var f = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "x");
            await f.Run();
            var loaded = await f.Store.LoadAsync(default);
            Assert(loaded.Errors == 0 && loaded.Bindings.Count == 1, "Cannot restore manifest");
            f.Binding = loaded.Bindings[0];
            Assert((await f.Run()).Uploaded == 0, "Restore uploaded unchanged file");
            await File.WriteAllTextAsync(Path.Combine(f.Store.DirectoryPath, "bad.json"), "broken");
            Assert((await f.Store.LoadAsync(default)).Errors == 1, "Corruption not reported");
            Assert(await File.ReadAllTextAsync(Path.Combine(f.Store.DirectoryPath, "bad.json")) == "broken", "Corruption overwritten");
        });
        await Case("Unsafe names and escaped paths are rejected", () =>
        {
            using var f = new Fixture();
            foreach (string name in new[] { "..", "bad.", "x:y", "a/b", "CON.txt", "LPT1", "bad " })
                ExpectSync<FolderSyncException>(() => FolderSyncRules.ValidateName(name));
            ExpectSync<FolderSyncException>(() => FolderSyncRules.Resolve(f.Binding, "../escape"));
            Assert(FolderSyncRules.Overlaps(f.Root, Path.Combine(f.Root, "child")), "Overlap missed");
            Assert(!FolderSyncRules.Overlaps(f.Root, f.Root + "-sibling"), "Sibling rejected");
            return Task.CompletedTask;
        });
        await Case("Watcher catches edits; pause stops uploads; resume catches up", async () =>
        {
            using var f = new Fixture();
            var job = new FolderSyncJob(f.Binding, f.Store, _ => f.Target);
            job.Start();
            try
            {
                await Until(() => job.Progress.StateKey == "Sync_Monitoring");
                await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "one");
                await Until(() => f.Target.Files.ContainsKey("a"));
                await job.SetEnabledAsync(false);
                await File.WriteAllTextAsync(Path.Combine(f.Root, "b"), "two");
                job.RequestScan();
                await Task.Delay(1300);
                Assert(!f.Target.Files.ContainsKey("b"), "Paused job uploaded");
                await job.SetEnabledAsync(true);
                await Until(() => f.Target.Files.ContainsKey("b"));
            }
            finally { await job.StopAsync(); }
        });
        await Case("Graph empty upload uses fail for new names and replace for existing names", async () =>
        {
            using var f = new GraphFixture();
            await f.Target.ValidateRootAsync(default);
            await f.Target.UploadAsync("empty.txt", new MemoryStream(), null, default);
            Assert(f.Handler.Requests.Last().Query.Contains("fail"), "New empty file can overwrite a race");
            f.Handler.Existing = true;
            await f.Target.ValidateRootAsync(default);
            await f.Target.UploadAsync("empty.txt", new MemoryStream(), null, default);
            Assert(f.Handler.Requests.Last().Query.Contains("replace") && f.Handler.LastIfMatch == "etag", "Existing empty file lacks precondition");
            Assert(f.Handler.Requests.All(r => r.Method != "DELETE"), "Delete request issued");
        });
        await Case("Graph exact-name directory reuse and creation never rename", async () =>
        {
            using var f = new GraphFixture();
            await f.Target.ValidateRootAsync(default);
            await f.Target.EnsureFolderAsync("created", default);
            Assert(f.Handler.LastBody.Contains("fail") && !f.Handler.LastBody.Contains("rename"), "Auto-renaming requested");
            int count = f.Handler.Requests.Count;
            await f.Target.EnsureFolderAsync("created", default);
            Assert(f.Handler.Requests.Count == count, "Directory created twice");
        });
        await Case("Case-only names and file/folder collisions never overwrite", async () =>
        {
            using var f = new GraphFixture();
            await f.Target.ValidateRootAsync(default);
            f.Handler.Existing = true;
            f.Handler.ExistingName = "EMPTY.TXT";
            await Expect<FolderSyncException>(() => f.Target.UploadAsync("empty.txt", new MemoryStream(), null, default));
            Assert(f.Handler.Requests.All(r => r.Method == "GET"), "Name mismatch mutated cloud");
        });
        await Case("Graph pagination finds a same-name item on later pages", async () =>
        {
            using var f = new GraphFixture();
            f.Handler.Paginate = true;
            f.Handler.Existing = true;
            await f.Target.ValidateRootAsync(default);
            await f.Target.UploadAsync("empty.txt", new MemoryStream(), null, default);
            Assert(f.Handler.LastIfMatch == "etag", "Later-page item was missed");
        });
        await Case("Untrusted pagination and moved/renamed roots are rejected", async () =>
        {
            using var f = new GraphFixture();
            await f.Target.ValidateRootAsync(default);
            f.Handler.NextLink = "https://example.com/drives/drive/items/root/children";
            await Expect<InvalidDataException>(() => f.Target.BrowseAsync("root", default));
            f.Handler.RootName = "renamed";
            await Expect<FolderSyncException>(() => f.Target.ValidateRootAsync(default));
        });
        await Case("All sync messages exist in both languages", () =>
        {
            var sets = new List<HashSet<string>>();
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                var entries = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw"))
                    .Root!.Elements("data").Where(e => e.Attribute("name")!.Value.StartsWith("Sync_")).ToList();
                Assert(entries.All(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value)), "Empty translation");
                sets.Add(entries.Select(e => e.Attribute("name")!.Value).ToHashSet());
            }
            Assert(sets[0].SetEquals(sets[1]), "Translation keys differ");
            return Task.CompletedTask;
        });
        await Case("Chunked Graph upload is anonymous, cancellable and confirms exact name/length", async () =>
        {
            await using var server = new SliceServer();
            using var f = new GraphFixture();
            f.Handler.UploadUrl = server.Url;
            await f.Target.ValidateRootAsync(default);
            const int length = 320 * 1024 + 19;
            var progress = new List<long>();
            await f.Target.UploadAsync("large.bin", new MemoryStream(new byte[length]), new InlineProgress<long>(progress.Add), default);
            Assert(server.Headers.Count == 2 && server.Headers.All(h => !h.ContainsKey("Authorization")), "Slices leaked metadata authentication");
            Assert(progress.Last() == length && f.Handler.SessionBody.Contains("fail"), "Upload result/progress incorrect");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Expect<OperationCanceledException>(() => f.Target.UploadAsync("large.bin", new MemoryStream(new byte[1]), null, cancel.Token));
        });
        await Case("Mismatched upload response never counts as success", async () =>
        {
            await using var server = new SliceServer { Name = "large (1).bin" };
            using var f = new GraphFixture();
            f.Handler.UploadUrl = server.Url;
            await f.Target.ValidateRootAsync(default);
            await Expect<FolderSyncException>(() => f.Target.UploadAsync("large.bin", new MemoryStream(new byte[1]), null, default));
        });
        await Case("Service prevents overlapping bindings and unlink preserves local and cloud data", async () =>
        {
            using var f = new Fixture();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "a"), "original");
            var service = new FolderSyncService(f.Store, _ => f.Target);
            try
            {
                await service.AddAsync(f.Binding);
                await Until(() => f.Target.Files.ContainsKey("a"));
                await Expect<FolderSyncException>(() => service.AddAsync(new FolderSyncBinding
                {
                    LocalPath = f.Root, FolderName = f.Binding.FolderName, DriveId = "another-drive",
                    AccountId = "another-account", RemoteFolderId = "another-target", RemotePath = "/資料"
                }));
                await service.RemoveAsync(service.Jobs.Single());
                Assert(service.Jobs.Count == 0 && File.Exists(Path.Combine(f.Root, "a")) && f.Target.Files.ContainsKey("a"), "Unlink removed data");
                Assert((await f.Store.LoadAsync(default)).Bindings.Count == 0, "Unlinked configuration remains");
            }
            finally { service.Stop(); }
        });
        Console.WriteLine($"Passed {_checks} folder sync regression checks.");
    }

    private static async Task Case(string name, Func<Task> action) { await action(); _checks++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void ExpectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(50, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "CloudFlowSyncTests", Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public FolderSyncStore Store { get; }
        public FolderSyncBinding Binding { get; set; }
        public FakeTarget Target { get; } = new();
        public Fixture()
        {
            Root = Path.Combine(_base, "資料");
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(_base, "state"));
            Binding = new() { LocalPath = Root, FolderName = "資料", AccountId = "account", DriveId = "drive", RemoteFolderId = "target", RemotePath = "/資料" };
        }
        public Task<FolderSyncResult> Run(CancellationToken token = default) => new FolderSyncEngine().RunAsync(Binding, Target,
            t => Store.SaveAsync(Binding, t), null, token);
        public void Dispose()
        {
            string absolute = Path.GetFullPath(_base);
            if (!absolute.StartsWith(Path.Combine(Path.GetTempPath(), "CloudFlowSyncTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception();
            Directory.Delete(absolute, true);
        }
    }

    private sealed class FakeTarget : IFolderSyncTarget
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);
        public bool Fail;
        public Action BeforeUpload;
        public Task ValidateRootAsync(CancellationToken token) => Task.CompletedTask;
        public Task EnsureFolderAsync(string path, CancellationToken token) { Folders.Add(path); return Task.CompletedTask; }
        public async Task UploadAsync(string path, Stream stream, IProgress<long> progress, CancellationToken token)
        {
            BeforeUpload?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Fail) throw new IOException();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            Files[path] = output.ToArray();
        }
        public void Dispose() { }
    }

    private sealed class GraphFixture : IDisposable
    {
        public Handler Handler { get; } = new();
        public GraphFolderSyncTarget Target { get; }
        public GraphFixture() => Target = new(new GraphServiceClient(new HttpClient(Handler), new TestAuthentication()),
            new FolderSyncBinding { DriveId = "drive", RemoteFolderId = "target", FolderName = "資料", RemoteAncestorIds = new() { "root" } });
        public void Dispose() => Target.Dispose();
    }

    private sealed class TestAuthentication : IAuthenticationProvider
    {
        public Task AuthenticateRequestAsync(Microsoft.Kiota.Abstractions.RequestInformation request,
            Dictionary<string, object> additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            request.Headers.Add("Authorization", "Bearer local-test-not-a-credential");
            return Task.CompletedTask;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Existing, Paginate;
        public string ExistingName = "empty.txt", RootName = "資料", NextLink, LastBody, LastIfMatch;
        public string UploadUrl, SessionBody;
        public List<(string Method, string Query)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.Query)));
            LastIfMatch = request.Headers.TryGetValues("If-Match", out var matches) ? matches.FirstOrDefault() : null;
            LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            object data;
            string path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Put) data = new { id = "file", name = "empty.txt", file = new { }, size = 0 };
            else if (path.EndsWith("createUploadSession"))
            {
                SessionBody = LastBody;
                data = new { uploadUrl = UploadUrl, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1), nextExpectedRanges = new[] { "0-" } };
            }
            else if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(LastBody);
                data = new { id = "created", name = json.RootElement.GetProperty("name").GetString(), folder = new { } };
            }
            else if (path.EndsWith("children"))
            {
                bool firstPage = Paginate && !request.RequestUri.Query.Contains("page=2");
                var values = Existing && !firstPage ? new object[] { new { id = "existing", name = ExistingName, file = new { }, size = 0, eTag = "etag" } } : Array.Empty<object>();
                var response = new Dictionary<string, object> { ["value"] = values };
                if (firstPage || NextLink != null) response["@odata.nextLink"] = NextLink ?? "https://graph.microsoft.com/v1.0/drives/drive/items/target/children?page=2";
                data = response;
            }
            else data = new { id = "target", name = RootName, folder = new { }, parentReference = new { id = "root", driveId = "drive" } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }
    }
}
