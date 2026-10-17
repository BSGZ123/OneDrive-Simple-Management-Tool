using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class FileTransferChecks
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Transfer picker navigates folders, keeps its last location on failure and excludes selected folders", Picker),
        ("Batch move captures its selection and moves files and folders to the real destination ID", Move),
        ("Move skips its current parent; copy keeps both in the current parent", SameParent),
        ("Keep-both handles case-insensitive names, extensions, long names and folder collisions", KeepBoth),
        ("Skip never mutates conflicting targets; its policy stays frozen for retries", Skip),
        ("Partial transfer retries failed items only and successful moves stay removed after refresh failure", PartialRetry),
        ("Selected parent and child are handled once; a failed parent prevents separate child mutations", Hierarchy),
        ("Transfers reject descendant targets, foreign selections, changed items and invalid ancestry", InvalidTargets),
        ("Concurrent transfer submission is rejected; stopping does not schedule the next item", Stop),
        ("Copy progress does not imply completion; retries poll the original receipt", ResumeCopy),
        ("Copy status failures and invalid completed resources retain the receipt without reposting", CopyFailure),
        ("Lost copy acknowledgement is uncertain and cannot be blindly retried", LostCopy),
        ("Explicitly ending an unreachable copy monitor permits a new batch without reposting it", EndTracking),
        ("Lost move acknowledgement is reconciled by ID; changed sources cannot be patched again", LostMove),
        ("Name races re-list and rename with a bounded retry count", ConflictRace),
        ("Async copy name-conflict failure applies keep-both or skip consistently", AsyncConflict),
        ("Graph transfers pin drive and root IDs, use If-Match and never request overwrite", GraphRequests),
        ("Graph copy requires a valid accepted receipt; explicit failures are classified", GraphErrors),
        ("Transfer folder listing traverses empty pages and rejects foreign or cyclic continuations", GraphPaging),
        ("Transfer metadata rejects foreign, deleted and remote items; account changes block requests", GraphIdentity),
        ("Copy monitor stays anonymous, bounds responses and validates status and redirect results", GraphMonitor),
        ("Transfer resources resolve in English and Chinese", Resources)
    ];

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        public GraphFixture Graph { get; } = new();
        public MemoryTransfer Service { get; } = new();
        public DriveViewModel Drive => Graph.Drive;
        public Fixture()
        {
            Graph.Handler.OnGet = (request, _) => Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/children")
                ? Json(new { value = Service.Data.Values.Where(i => i.ParentId == "root-id").Select(Raw).ToArray() }) : null);
        }
        public FileTransferViewModel Model(FileTransferKind kind = FileTransferKind.Move, params string[] ids) =>
            new((ids.Length == 0 ? new[] { "a", "b" } : ids).Select(id => new FileViewModel(Drive, new DriveItem
            { Id = id, Name = Service.Data[id].Name, Folder = Service.Data[id].IsFolder ? new Folder() : null })),
                kind, Service, TimeSpan.Zero, 1);
        public async Task Destination(FileTransferViewModel vm)
        {
            await vm.InitializeAsync();
            await vm.OpenFolderAsync(vm.Folders.Single(i => i.Id == "dest"));
        }
        public void Dispose() => Graph.Dispose();
    }

    private sealed class MemoryTransfer : IFileTransferService
    {
        public Dictionary<string, TransferItem> Data { get; } = new()
        {
            ["root-id"] = new("root-id", "Root", true, null, IsRoot: true),
            ["dest"] = new("dest", "Destination", true, "root-id", "d1"),
            ["a"] = new("a", "Report.txt", false, "root-id", "a1"),
            ["b"] = new("b", "Folder", true, "root-id", "b1"),
            ["child"] = new("child", "Inside.txt", false, "b", "c1")
        };
        public List<(string Kind, string Id, string Parent, string Name)> Calls { get; } = [];
        public int Polls;
        public Func<string, Exception> ReadError;
        public Func<string, CancellationToken, Task> BeforeMutation;
        public Func<string, Exception> AfterMutation;
        public Func<string, CancellationToken, Task<CopyStatus>> Poll;
        private readonly Dictionary<string, string> _copies = new();
        public Task<TransferItem> GetRootAsync(CancellationToken token) => GetItemAsync("root-id", token);
        public Task<TransferItem> GetItemAsync(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ReadError?.Invoke(id) is Exception error) throw error;
            return Task.FromResult(Data[id]);
        }
        public Task<IReadOnlyList<TransferItem>> GetChildrenAsync(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ReadError?.Invoke(id) is Exception error) throw error;
            return Task.FromResult<IReadOnlyList<TransferItem>>(Data.Values.Where(i => i.ParentId == id).ToArray());
        }
        public async Task MoveAsync(TransferItem source, string parent, string name, CancellationToken token)
        {
            Calls.Add(("move", source.Id, parent, name));
            if (BeforeMutation != null) await BeforeMutation(source.Id, token);
            Data[source.Id] = source with { ParentId = parent, Name = name, ETag = "changed" };
            if (AfterMutation?.Invoke(source.Id) is Exception error) throw error;
        }
        public async Task<string> CopyAsync(TransferItem source, string parent, string name, CancellationToken token)
        {
            Calls.Add(("copy", source.Id, parent, name));
            if (BeforeMutation != null) await BeforeMutation(source.Id, token);
            string id = "copy-" + Calls.Count, url = "https://api.onedrive.com/monitor/" + id;
            Data[id] = source with { Id = id, ParentId = parent, Name = name };
            _copies[url] = id;
            if (AfterMutation?.Invoke(source.Id) is Exception error) throw error;
            return url;
        }
        public Task<CopyStatus> GetCopyStatusAsync(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Polls++;
            return Poll != null ? Poll(url, token) : Task.FromResult(new CopyStatus(true, _copies[url], 100));
        }
    }

    private static async Task Picker()
    {
        using var f = new Fixture();
        var vm = f.Model();
        Check(!vm.CanSubmit, "Uninitialized destination is actionable.");
        f.Service.ReadError = _ => new IOException();
        await vm.InitializeAsync();
        Check(vm.HasError && !vm.CanSubmit && !vm.IsBusy, "Initial navigation failure.");
        f.Service.ReadError = null;
        await vm.ReloadAsync();
        Check(vm.Folders.Select(i => i.Id).SequenceEqual(new[] { "dest" }), "Selected folder was offered.");
        f.Service.ReadError = id => id == "dest" ? new IOException() : null;
        await vm.OpenFolderAsync(vm.Folders[0]);
        Check(vm.DestinationPath == "/" && vm.Folders.Count == 1 && vm.CanSubmit, "Failed navigation replaced destination.");
        f.Service.ReadError = null;
        await f.Destination(vm);
        Check(vm.DestinationPath == "/Destination" && vm.CanGoUp && vm.Folders.Count == 0, "Empty target folder.");
        await vm.GoUpAsync();
        Check(vm.DestinationPath == "/" && !vm.CanGoUp, "Up at root.");
    }

    private static async Task Move()
    {
        using var f = new Fixture();
        await f.Drive.GetFiles();
        var vm = f.Model(FileTransferKind.Move, "a", "b", "a");
        await f.Destination(vm);
        f.Drive.SetSelection(f.Drive.Files.Where(i => i.Id == "dest"));
        await vm.SubmitAsync();
        Check(vm.Items.Count == 2 && vm.HasSucceeded && !vm.CanSubmit && !vm.CanChoose, "Batch did not settle.");
        Check(f.Service.Calls.Count == 2 && f.Service.Calls.All(c => c.Parent == "dest"), "Wrong captured selection.");
        Check(f.Service.Data["child"].ParentId == "b", "Folder hierarchy changed.");
        await vm.SubmitAsync();
        Check(f.Service.Calls.Count == 2, "Repeated completed move.");
    }

    private static async Task SameParent()
    {
        using var f = new Fixture();
        var vm = f.Model();
        await vm.InitializeAsync();
        await vm.SubmitAsync();
        Check(vm.Items.All(i => i.State == FileTransferState.Skipped) && f.Service.Calls.Count == 0, "No-op move mutated.");
        vm = f.Model(FileTransferKind.Copy, "a");
        await vm.InitializeAsync();
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Single().Name == "Report (2).txt", "Same-folder copy did not keep both.");
    }

    private static async Task KeepBoth()
    {
        using var f = new Fixture();
        f.Service.Data["existing"] = new("existing", "REPORT.txt", false, "dest");
        f.Service.Data["existing2"] = new("existing2", "Report (2).txt", false, "dest");
        f.Service.Data["existing3"] = new("existing3", "Folder", true, "dest");
        var vm = f.Model();
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(f.Service.Calls.Select(c => c.Name).SequenceEqual(new[] { "Report (3).txt", "Folder (2)" }), "Conflict names incorrect.");
        Check(f.Service.Data["existing3"].Name == "Folder", "Existing folder merged or replaced.");
        f.Service.Data["long"] = new("long", new string('x', 251) + ".txt", false, "root-id", "etag");
        f.Service.Data["long-existing"] = f.Service.Data["long"] with { Id = "long-existing", ParentId = "dest" };
        vm = f.Model(FileTransferKind.Copy, "long");
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Last().Name.Length == 255 && f.Service.Calls.Last().Name.EndsWith(" (2).txt"), "Long extension lost.");
    }

    private static async Task Skip()
    {
        using var f = new Fixture();
        f.Service.Data["existing"] = new("existing", "report.txt", false, "dest");
        f.Service.BeforeMutation = (_, _) => throw new FileTransferException("FileOperation_AccessDenied");
        var vm = f.Model();
        vm.ConflictIndex = 1;
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.Items[0].State == FileTransferState.Skipped && vm.Items[1].State == FileTransferState.Failed, "Skip policy ignored.");
        vm.ConflictIndex = 0;
        f.Service.Data["existing2"] = new("existing2", "Folder", true, "dest");
        f.Service.BeforeMutation = null;
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Count == 1, "Retry changed confirmed conflict policy.");
    }

    private static async Task PartialRetry()
    {
        using var f = new Fixture();
        await f.Drive.GetFiles();
        f.Graph.Handler.OnGet = null;
        f.Graph.Handler.ListStatus = HttpStatusCode.ServiceUnavailable;
        f.Service.BeforeMutation = (id, _) => id == "b" ? throw new FileTransferException("FileOperation_AccessDenied") : Task.CompletedTask;
        var vm = f.Model();
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.Items[0].State == FileTransferState.Succeeded && vm.Items[1].State == FileTransferState.Failed && vm.CanSubmit, "Partial failure lost.");
        Check(f.Drive.Files.All(i => i.Id != "a") && f.Drive.HasError, "Refresh failure resurrected moved source.");
        f.Service.BeforeMutation = null;
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Count(c => c.Id == "a") == 1 && f.Service.Calls.Count(c => c.Id == "b") == 2, "Retry repeated success.");
    }

    private static async Task Hierarchy()
    {
        foreach (var kind in new[] { FileTransferKind.Move, FileTransferKind.Copy })
        {
            using var f = new Fixture();
            var vm = f.Model(kind, "child", "b");
            await f.Destination(vm);
            f.Service.BeforeMutation = (_, _) => throw new FileTransferException("FileOperation_AccessDenied");
            await vm.SubmitAsync();
            Check(vm.Items.All(i => i.State == FileTransferState.Failed) && f.Service.Calls.Single().Id == "b", "Child operated after parent failure.");
            f.Service.BeforeMutation = null;
            await vm.SubmitAsync();
            Check(vm.HasSucceeded && vm.Items[0].State == FileTransferState.Skipped && f.Service.Calls.All(c => c.Id == "b"), "Selected hierarchy duplicated.");
        }
    }

    private static async Task InvalidTargets()
    {
        using var f = new Fixture();
        var vm = f.Model(FileTransferKind.Move, "b");
        await f.Destination(vm);
        f.Service.Data["dest"] = f.Service.Data["dest"] with { ParentId = "b" };
        await vm.SubmitAsync();
        Check(vm.HasError && f.Service.Calls.Count == 0 && vm.CanChoose, "Descendant target accepted.");
        f.Service.Data["dest"] = f.Service.Data["dest"] with { ParentId = "dest" };
        await vm.SubmitAsync();
        Check(vm.HasError && f.Service.Calls.Count == 0, "Cycle accepted.");
        f.Service.Data["dest"] = f.Service.Data["dest"] with { ParentId = "root-id", IsUsable = false };
        await vm.SubmitAsync();
        Check(vm.HasError && f.Service.Calls.Count == 0, "Unavailable target accepted.");
        using var other = new Fixture();
        await Throws<ArgumentException>(() => Task.FromResult(new FileTransferViewModel(new[] {
            new FileViewModel(f.Drive, new DriveItem { Id = "a" }),
            new FileViewModel(other.Drive, new DriveItem { Id = "b" }) }, FileTransferKind.Move, f.Service)));
        f.Service.Data["dest"] = f.Service.Data["dest"] with { IsUsable = true };
        int reads = 0;
        f.Service.ReadError = id =>
        {
            if (id == "b" && ++reads == 2) f.Service.Data["b"] = f.Service.Data["b"] with { Name = "Changed" };
            return null;
        };
        await vm.SubmitAsync();
        Check(vm.Items.Single().State == FileTransferState.Failed && f.Service.Calls.Count == 0, "Changed item moved.");
    }

    private static async Task Stop()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        f.Service.BeforeMutation = async (_, token) => { entered.SetResult(); await gate.Task.WaitAsync(token); };
        var vm = f.Model(FileTransferKind.Copy);
        await f.Destination(vm);
        var run = vm.SubmitAsync();
        await entered.Task;
        await vm.SubmitAsync();
        Check(f.Service.Calls.Count == 1 && vm.IsBusy && !vm.CanChoose, "Concurrent submit.");
        vm.Stop();
        gate.SetResult();
        await run;
        Check(vm.HasPendingCopy && vm.Items[0].State == FileTransferState.Waiting && vm.Items[1].State == FileTransferState.Pending, "Cancellation discarded receipt or scheduled later item.");
        f.Service.BeforeMutation = null;
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Count == 2, "Stopped copy reposted.");
    }

    private static async Task ResumeCopy()
    {
        using var f = new Fixture();
        var vm = f.Model(FileTransferKind.Copy, "a");
        f.Service.Poll = (_, _) => Task.FromResult(new CopyStatus(false, null, 99));
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.HasPendingCopy && vm.CanSubmit && !vm.HasSucceeded && vm.Items[0].Percentage == 99, "Progress confused with completion.");
        f.Service.Poll = null;
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && !vm.HasPendingCopy && f.Service.Calls.Count == 1, "Status retry repeated POST.");
    }

    private static async Task CopyFailure()
    {
        using var f = new Fixture();
        var vm = f.Model(FileTransferKind.Copy, "a");
        f.Service.Poll = (_, _) => throw new IOException("offline");
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.HasPendingCopy && vm.Items[0].State == FileTransferState.Waiting, "Read failure lost receipt.");
        f.Service.Poll = (_, _) => Task.FromResult(new CopyStatus(true, "a", 100));
        await vm.SubmitAsync();
        Check(!vm.HasSucceeded && f.Service.Calls.Count == 1, "Source accepted as copy.");
        f.Service.Poll = null;
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Count == 1, "Failed confirmation reposted.");
    }

    private static async Task LostCopy()
    {
        foreach (Exception error in new Exception[] { new FileTransferException("Transfer_Uncertain", uncertain: true), new IOException() })
        {
            using var f = new Fixture();
            f.Service.AfterMutation = _ => error;
            var vm = f.Model(FileTransferKind.Copy, "a");
            await f.Destination(vm);
            await vm.SubmitAsync();
            await vm.SubmitAsync();
            Check(vm.Items[0].State == FileTransferState.Uncertain && !vm.CanSubmit && f.Service.Calls.Count == 1, "Unknown copy blindly repeated.");
        }
    }

    private static async Task EndTracking()
    {
        using var f = new Fixture();
        var vm = f.Model(FileTransferKind.Copy, "a");
        f.Service.Poll = (_, _) => throw new IOException();
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.CanStopTracking, "Unreachable copy cannot be released.");
        vm.StopTracking();
        await vm.SubmitAsync();
        Check(!vm.HasPendingCopy && !vm.CanSubmit && vm.Items[0].State == FileTransferState.Uncertain && f.Service.Calls.Count == 1,
            "Ending tracking repeated the copy or reported success.");
    }

    private static async Task LostMove()
    {
        using var f = new Fixture();
        f.Service.AfterMutation = _ => new FileTransferException("Transfer_Uncertain", uncertain: true);
        var vm = f.Model(FileTransferKind.Move, "a");
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.Items[0].State == FileTransferState.Waiting, "Move receipt loss not retained.");
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Count == 1, "Successful move repeated.");
        vm = f.Model(FileTransferKind.Move, "b");
        await f.Destination(vm);
        f.Service.BeforeMutation = (_, _) => throw new FileTransferException("Transfer_Uncertain", uncertain: true);
        await vm.SubmitAsync();
        f.Service.Data["b"] = f.Service.Data["b"] with { ETag = "external-change" };
        await vm.SubmitAsync();
        Check(vm.Items[0].State == FileTransferState.Uncertain && f.Service.Calls.Count == 2, "Changed uncertain move repeated.");
    }

    private static async Task ConflictRace()
    {
        using var f = new Fixture();
        f.Service.BeforeMutation = (_, _) =>
        {
            if (f.Service.Calls.Count == 1)
            {
                f.Service.Data["racer"] = new("racer", "Report.txt", false, "dest");
                throw new FileTransferException("FileOperation_NameConflict", nameConflict: true);
            }
            return Task.CompletedTask;
        };
        var vm = f.Model(FileTransferKind.Move, "a");
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.HasSucceeded && f.Service.Calls.Last().Name == "Report (2).txt", "Race did not re-list.");
        f.Service.BeforeMutation = (_, _) => throw new FileTransferException("FileOperation_NameConflict", nameConflict: true);
        vm = f.Model(FileTransferKind.Copy, "b");
        await f.Destination(vm);
        await vm.SubmitAsync();
        Check(vm.Items[0].State == FileTransferState.Failed && f.Service.Calls.Count == 7, "Name race was not bounded.");
    }

    private static async Task AsyncConflict()
    {
        foreach (int policy in new[] { 0, 1 })
        {
            using var f = new Fixture();
            f.Service.Poll = (_, _) =>
            {
                f.Service.Poll = null;
                return Task.FromResult(new CopyStatus(false, null, 0, "FileOperation_NameConflict", true));
            };
            var vm = f.Model(FileTransferKind.Copy, "a");
            vm.ConflictIndex = policy;
            await f.Destination(vm);
            await vm.SubmitAsync();
            Check(vm.HasSucceeded && f.Service.Calls.Count == (policy == 0 ? 2 : 1), "Async conflict policy incorrect.");
            Check(!vm.HasPendingCopy, "Terminal conflict retained monitor.");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body, string ETag, string Auth)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => Json(new { });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method, request.RequestUri, request.Content == null ? "" : await request.Content.ReadAsStringAsync(token),
                request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null, request.Headers.Authorization?.ToString()));
            return Respond(request);
        }
    }
    private sealed class Protocol : IDisposable
    {
        public Handler Graph { get; } = new();
        public Handler Monitor { get; } = new();
        public GraphServiceClient Client { get; }
        public GraphFileTransferService Service { get; }
        private readonly HttpClient _monitor;
        public Protocol()
        {
            var http = new HttpClient(Graph);
            http.DefaultRequestHeaders.Authorization = new("Bearer", "simulated-token");
            Client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());
            _monitor = new HttpClient(Monitor);
            Service = new(Client, "selected-drive", _monitor);
        }
        public void Dispose() { Client.Dispose(); _monitor.Dispose(); }
    }
    private static TransferItem Source => new("file", "Report.txt", false, "source-parent", "\"etag\"");
    private static object Raw(TransferItem i) => new { id = i.Id, name = i.Name, eTag = i.ETag,
        parentReference = i.IsRoot ? null : new { id = i.ParentId, driveId = "selected-drive" },
        root = i.IsRoot ? new { } : null, folder = i.IsFolder ? new { } : null, file = !i.IsFolder ? new { } : null };
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Accepted(string url = "https://api.onedrive.com/monitor/1")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        if (url != null) response.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static async Task GraphRequests()
    {
        using var p = new Protocol();
        p.Graph.Respond = r => r.Method == HttpMethod.Patch ? Json(Raw(Source with { ParentId = "real-root" })) : Accepted();
        await p.Service.MoveAsync(Source, "real-root", Source.Name, default);
        await p.Service.CopyAsync(Source, "real-root", "Report (2).txt", default);
        Check(p.Graph.Requests.Count == 2 && p.Graph.Requests[0].ETag == Source.ETag, "ETag missing.");
        foreach (var r in p.Graph.Requests)
        {
            using var json = JsonDocument.Parse(r.Body);
            var parent = json.RootElement.GetProperty("parentReference");
            Check(parent.GetProperty("id").GetString() == "real-root" && parent.GetProperty("driveId").GetString() == "selected-drive", "Transfer targeted wrong drive/root.");
            Check(r.Uri.AbsolutePath.StartsWith("/v1.0/drives/selected-drive/items/file") && r.Uri.Query == "" && !r.Body.Contains("replace"), "Unexpected transfer endpoint/policy.");
        }
        await Throws<FileTransferException>(() => p.Service.MoveAsync(Source with { ETag = null }, "real-root", Source.Name, default));
        Check(p.Graph.Requests.Count == 2, "Missing ETag sent PATCH.");
    }

    private static async Task GraphErrors()
    {
        using var p = new Protocol();
        foreach (string url in new[] { null, "/relative", "http://api.onedrive.com/monitor", "https://evil.example/monitor", "https://api.onedrive.com.evil.example/m", "https://user@api.onedrive.com/m", "https://api.onedrive.com:444/m" })
        {
            p.Graph.Respond = _ => Accepted(url);
            Check((await Throws<FileTransferException>(() => p.Service.CopyAsync(Source, "dest", Source.Name, default))).Uncertain, "Unsafe acknowledgement not uncertain.");
        }
        foreach (var (status, key) in new[] { (HttpStatusCode.Forbidden, "FileOperation_AccessDenied"), (HttpStatusCode.PreconditionFailed, "Transfer_Changed"), (HttpStatusCode.TooManyRequests, "FileOperation_TooManyRequests") })
        {
            p.Graph.Respond = _ => Json(new { error = new { code = "test" } }, status);
            var error = await Throws<FileTransferException>(() => p.Service.MoveAsync(Source, "dest", Source.Name, default));
            Check(error.Key == key && !error.Uncertain, "Known failure classification.");
        }
        p.Graph.Respond = _ => Json(new { error = new { code = "nameAlreadyExists" } }, HttpStatusCode.Conflict);
        Check((await Throws<FileTransferException>(() => p.Service.CopyAsync(Source, "dest", Source.Name, default))).NameConflict, "Conflict not classified.");
        p.Graph.Respond = _ => new(HttpStatusCode.ServiceUnavailable);
        Check((await Throws<FileTransferException>(() => p.Service.CopyAsync(Source, "dest", Source.Name, default))).Uncertain, "Server failure blindly retryable.");
    }

    private static async Task GraphPaging()
    {
        using var p = new Protocol();
        const string next = "https://graph.microsoft.com/v1.0/drives/selected-drive/items/dest/children?$skiptoken=2";
        p.Graph.Respond = r => r.RequestUri.Query.Contains("skiptoken")
            ? Json(new { value = new[] { Raw(Source with { ParentId = "dest" }) } })
            : Json(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next });
        Check((await p.Service.GetChildrenAsync("dest", default)).Single().Id == "file" && p.Graph.Requests.Count == 2, "Empty page ended listing.");
        foreach (string link in new[] { next.Replace("selected-drive", "foreign"), next.Replace("graph.microsoft.com", "evil.example"), next.Replace("/dest/", "/other/") })
        {
            int count = p.Graph.Requests.Count;
            p.Graph.Respond = _ => Json(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = link });
            await Throws<InvalidDataException>(() => p.Service.GetChildrenAsync("dest", default));
            Check(p.Graph.Requests.Count == count + 1, "Foreign continuation requested.");
        }
        p.Graph.Respond = _ => Json(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next });
        await Throws<InvalidDataException>(() => p.Service.GetChildrenAsync("dest", default));
        p.Graph.Respond = _ => Json(new { value = new[] { Raw(Source) } });
        await Throws<InvalidDataException>(() => p.Service.GetChildrenAsync("dest", default));
    }

    private static async Task GraphIdentity()
    {
        using var p = new Protocol();
        foreach (var extra in new[] { "remoteItem", "deleted", "foreign" })
        {
            p.Graph.Respond = _ => Json(new Dictionary<string, object>
            {
                ["id"] = "file", ["name"] = "Report.txt", ["file"] = new { },
                ["parentReference"] = new { id = "parent", driveId = extra == "foreign" ? "foreign-drive" : "selected-drive" },
                [extra] = new { }
            });
            await Throws<FileTransferException>(() => p.Service.GetItemAsync("file", default));
        }
        using var f = new Fixture();
        var service = f.Drive.Provider.CreateFileTransferService();
        f.Drive.Provider.IsAuthenticated = false;
        await Throws<FileTransferException>(() => service.GetRootAsync(default));
        Check(f.Graph.Handler.Requests.Count == 0, "Logged-out identity still requested.");
    }

    private static async Task GraphMonitor()
    {
        using var p = new Protocol();
        const string url = "https://tenant.sharepoint.com/monitor/1?token=preauthenticated";
        p.Monitor.Respond = _ => Json(new { status = "inProgress", percentageComplete = 100 });
        var status = await p.Service.GetCopyStatusAsync(url, default);
        Check(!status.Completed && status.Percentage == 99 && p.Monitor.Requests.Single().Auth == null && p.Graph.Requests.Count == 0, "Monitor leaked credentials or finished early.");
        p.Monitor.Respond = _ => Json(new { status = "completed", resourceId = "new-item" });
        Check((await p.Service.GetCopyStatusAsync(url, default)).ResourceId == "new-item", "Completed ID lost.");
        p.Monitor.Respond = _ => Json(new { status = "failed", error = new { code = "nameAlreadyExists" } });
        Check((await p.Service.GetCopyStatusAsync(url, default)).NameConflict, "Async conflict lost.");
        foreach (object body in new object[] { new { status = "completed" }, new { status = "surprise" }, new { status = "inProgress", percentageComplete = "NaN" }, new { status = "inProgress", padding = new string('x', 70000) } })
        {
            p.Monitor.Respond = _ => Json(body);
            await Throws<InvalidDataException>(() => p.Service.GetCopyStatusAsync(url, default));
        }
        p.Monitor.Respond = _ => new(HttpStatusCode.SeeOther) { Headers = { Location = new Uri("https://graph.microsoft.com/v1.0/drives/selected-drive/items/new-id") } };
        int before = p.Monitor.Requests.Count;
        Check((await p.Service.GetCopyStatusAsync(url, default)).ResourceId == "new-id" && p.Monitor.Requests.Count == before + 1, "Redirect followed instead of validated.");
        await Throws<InvalidDataException>(() => p.Service.GetCopyStatusAsync("https://evil.example/m", default));
        Check(p.Monitor.Requests.Count == before + 1, "Unsafe monitor contacted.");
    }

    private static Task Resources()
    {
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                ResourceHelper.Language = language;
                var names = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw"))
                    .Root.Elements("data").Select(e => (string)e.Attribute("name")).ToArray();
                Check(names.Distinct().Count() == names.Length, "Duplicate resources.");
                foreach (string key in names.Where(n => n.StartsWith("Transfer_")).Concat(new[] { "FileView_Flyout_Move.Text", "FileView_Flyout_Copy.Text", "Drive_Organize.Content" }))
                    Check(!string.IsNullOrWhiteSpace(key.GetLocalized()), "Missing resource " + key);
                Check(string.Format("Transfer_Summary".GetLocalized(), 1, 2, 3, 6).Contains("6"), "Summary format.");
            }
        }
        finally { ResourceHelper.Language = "en-US"; }
        return Task.CompletedTask;
    }
}
