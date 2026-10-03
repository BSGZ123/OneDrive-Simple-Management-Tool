using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System.Net;
using System.Reflection;
using System.Text;

internal static class Program
{
    private static readonly PreviewOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromMilliseconds(80),
        ReadTimeout = TimeSpan.FromMilliseconds(80),
        RenderTimeout = TimeSpan.FromMilliseconds(80),
        RetryDelay = TimeSpan.FromMilliseconds(10),
        MaxTextBytes = 128,
        MaxImageBytes = 256
    };

    private static async Task Main()
    {
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.ConfigureServices(new EmptyServices());
        (string, Func<Task>)[] cases =
        [
            ("UTF-8, UTF-16 BOM and empty documents", Text),
            ("Metadata limit prevents the content request", MetadataLimit),
            ("Actual byte limits reject unknown and dishonest sizes", ActualLimit),
            ("Truncated, null, invalid encoding and empty image content", InvalidContent),
            ("Invalid and missing streaming URLs are rejected", InvalidUrls),
            ("Metadata and stalled body timeouts cancel their operations", Timeouts),
            ("Late streams after close are disposed", LateStream),
            ("Repeated start shares one active operation", Duplicate),
            ("Recoverable render failure refreshes URL once", RefreshAddress),
            ("Automatic retry is bounded and manual retry recovers", RetryBudget),
            ("Permission, not-found, oversized and invalid files never auto retry", PermanentFailures),
            ("Close during fetch, render and retry delay is terminal", CloseStages),
            ("Renderer completion is required and renderer timeout is bounded", RenderDeadline),
            ("Failure during playback resets resources and requires manual retry", PlaybackFailure),
            ("Empty documents skip rendering and reset on close", Empty),
            ("ViewModel commands and both language resources agree with state", ViewModel),
            ("Graph requests use selected drive/item and refresh metadata", GraphTargets),
            ("Graph metadata rejects folders, shortcuts and foreign items", GraphInvalid),
            ("Graph cancellation and permission errors reach preview", GraphErrors),
            ("PDF header probe rejects error pages, limits reads, and classifies URL failures", PdfProbe)
        ];
        foreach (var (name, run) in cases)
        {
            await Microsoft.UI.Dispatching.TestUiContext.Run(run);
            Console.WriteLine("PASS " + name);
        }
        Console.WriteLine($"Passed {cases.Length} preview regression checks.");
    }

    private static PreviewContentLoader Loader(byte[] bytes, long? size = null, PreviewKind kind = PreviewKind.Markdown) =>
        new(kind, _ => Task.FromResult(new PreviewMetadata(size, "https://example.test/file")),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes)), Fast);

    private static PreviewSession Session(Func<CancellationToken, Task<PreviewContent>> load,
        Func<PreviewContent, CancellationToken, Task> render = null, Action reset = null, PreviewOptions options = null) =>
        new(load, render ?? ((_, _) => Task.CompletedTask), reset ?? (() => { }), options ?? Fast);

    private static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Expect(PreviewFailure failure, Func<Task> action)
    {
        try { await action(); }
        catch (PreviewException error) { Assert(error.Failure == failure, error.Failure.ToString()); return; }
        throw new InvalidOperationException("Expected " + failure);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; !condition() && i < 200; i++) await Task.Delay(2);
        Assert(condition(), "Condition did not become true");
    }

    private static async Task Text()
    {
        foreach (byte[] bytes in new[]
        {
            Encoding.UTF8.GetBytes("# 中文"),
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# 中文")).ToArray()
        })
            Assert((await Loader(bytes, bytes.Length).LoadAsync(default)).Text == "# 中文");
        Assert((await Loader([]).LoadAsync(default)).IsEmpty);
    }

    private static async Task MetadataLimit()
    {
        int calls = 0;
        var loader = new PreviewContentLoader(PreviewKind.Markdown, _ => Task.FromResult(new PreviewMetadata(129, null)),
            _ => { calls++; return Task.FromResult<Stream>(Stream.Null); }, Fast);
        await Expect(PreviewFailure.TooLarge, () => loader.LoadAsync(default));
        Assert(calls == 0);
    }

    private static async Task ActualLimit()
    {
        foreach (long? size in new long?[] { null, 1 })
            await Expect(PreviewFailure.TooLarge, () => Loader(new byte[129], size).LoadAsync(default));
        Assert((await Loader(Encoding.UTF8.GetBytes(new string('a', 128))).LoadAsync(default)).Text.Length == 128);
    }

    private static async Task InvalidContent()
    {
        await Expect(PreviewFailure.InvalidContent, () => Loader([1], 2).LoadAsync(default));
        await Expect(PreviewFailure.InvalidContent, () => Loader([0xFF]).LoadAsync(default));
        await Expect(PreviewFailure.InvalidContent, () => Loader([], 0, PreviewKind.Image).LoadAsync(default));
        var loader = new PreviewContentLoader(PreviewKind.Image, _ => Task.FromResult(new PreviewMetadata(null, null)),
            _ => Task.FromResult<Stream>(null), Fast);
        await Expect(PreviewFailure.InvalidContent, () => loader.LoadAsync(default));
    }

    private static async Task InvalidUrls()
    {
        foreach (string url in new[] { null, "", "/relative", "file:///c:/test.pdf", "https://user:password@example.test/file" })
        {
            var loader = new PreviewContentLoader(PreviewKind.Pdf, _ => Task.FromResult(new PreviewMetadata(5, url)),
                _ => throw new Exception("Streaming should not fetch the body"), Fast);
            await Expect(PreviewFailure.InvalidContent, () => loader.LoadAsync(default));
        }
    }

    private static async Task Timeouts()
    {
        bool cancelled = false;
        var loader = new PreviewContentLoader(PreviewKind.Markdown, async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
            return null;
        }, _ => throw new Exception(), Fast);
        await Expect(PreviewFailure.Timeout, () => loader.LoadAsync(default));
        await Until(() => cancelled);
        using var stalled = new StalledStream();
        await Expect(PreviewFailure.Timeout, () => PreviewContentLoader.ReadLimitedAsync(stalled, 128, Fast.ReadTimeout, default));
        await Until(() => stalled.Cancelled);
    }

    private static async Task LateStream()
    {
        var gate = new TaskCompletionSource<Stream>();
        var loader = new PreviewContentLoader(PreviewKind.Image, _ => Task.FromResult(new PreviewMetadata(null, null)),
            _ => gate.Task, Fast);
        using var cancellation = new CancellationTokenSource();
        Task operation = loader.LoadAsync(cancellation.Token);
        cancellation.Cancel();
        try { await operation; } catch (OperationCanceledException) { }
        var late = new TrackedStream();
        gate.SetResult(late);
        await Until(() => late.Disposed);
    }

    private static async Task Duplicate()
    {
        var gate = new TaskCompletionSource<PreviewContent>();
        int calls = 0;
        var session = Session(_ => { calls++; return gate.Task; });
        Task first = session.StartAsync();
        Assert(ReferenceEquals(first, session.StartAsync()));
        await Until(() => calls == 1);
        gate.SetResult(new(PreviewKind.Markdown, Text: "one"));
        await first;
        Assert(session.State == PreviewState.Ready && calls == 1);
        await session.CloseAsync();
    }

    private static async Task RefreshAddress()
    {
        int loads = 0;
        var seen = new List<string>();
        var loader = new PreviewContentLoader(PreviewKind.Pdf,
            _ => Task.FromResult(new PreviewMetadata(5, "https://example.test/" + ++loads)),
            _ => throw new Exception(), Fast);
        var session = Session(loader.LoadAsync, (content, _) =>
        {
            seen.Add(content.Uri.AbsolutePath);
            return loads == 1 ? Task.FromException(new PreviewException(PreviewFailure.AccessDenied, true)) : Task.CompletedTask;
        });
        await session.StartAsync();
        Assert(session.State == PreviewState.Ready && seen.SequenceEqual(new[] { "/1", "/2" }));
        await session.CloseAsync();
    }

    private static async Task RetryBudget()
    {
        int calls = 0;
        bool offline = true;
        var session = Session(_ =>
        {
            calls++;
            return offline ? Task.FromException<PreviewContent>(new HttpRequestException()) :
                Task.FromResult(new PreviewContent(PreviewKind.Markdown, Text: "ok"));
        });
        await session.StartAsync();
        Assert(session.State == PreviewState.Failed && calls == 2);
        offline = false;
        await session.StartAsync();
        Assert(session.State == PreviewState.Ready && calls == 3);
        await session.CloseAsync();
    }

    private static async Task PermanentFailures()
    {
        foreach (Exception error in new Exception[]
        {
            new ApiException { ResponseStatusCode = 401 }, new ApiException { ResponseStatusCode = 403 },
            new ApiException { ResponseStatusCode = 404 }, new PreviewException(PreviewFailure.TooLarge),
            new PreviewException(PreviewFailure.InvalidContent), new PreviewException(PreviewFailure.Unsupported)
        })
        {
            int calls = 0;
            var session = Session(_ => { calls++; return Task.FromException<PreviewContent>(error); });
            await session.StartAsync();
            Assert(session.State == PreviewState.Failed && calls == 1);
            await session.CloseAsync();
        }
    }

    private static async Task CloseStages()
    {
        foreach (string stage in new[] { "fetch", "render", "retry" })
        {
            var gate = new TaskCompletionSource<PreviewContent>();
            var rendered = new TaskCompletionSource();
            int calls = 0;
            var session = Session(_ =>
            {
                calls++;
                return stage == "fetch" ? gate.Task : stage == "retry"
                    ? Task.FromException<PreviewContent>(new HttpRequestException())
                    : Task.FromResult(new PreviewContent(PreviewKind.Image, Bytes: [1]));
            }, (_, _) => rendered.Task, options: new PreviewOptions { RetryDelay = TimeSpan.FromSeconds(2) });
            Task running = session.StartAsync();
            await Until(() => calls > 0 && (stage == "fetch" || session.State ==
                (stage == "render" ? PreviewState.Loading : PreviewState.Retrying)));
            await session.CloseAsync();
            gate.TrySetResult(new(PreviewKind.Image, Bytes: [2]));
            rendered.TrySetResult();
            await running;
            await session.StartAsync();
            Assert(session.State == PreviewState.Closed && calls == 1);
        }
    }

    private static async Task RenderDeadline()
    {
        int renders = 0;
        var session = Session(_ => Task.FromResult(new PreviewContent(PreviewKind.Image, Bytes: [1])),
            async (_, token) => { renders++; await Task.Delay(Timeout.Infinite, token); });
        Task running = session.StartAsync();
        await Until(() => session.State == PreviewState.Loading);
        Assert(session.State != PreviewState.Ready);
        await running;
        Assert(session.State == PreviewState.Failed && session.Failure == PreviewFailure.Timeout && renders == 2);
        await session.CloseAsync();
    }

    private static async Task PlaybackFailure()
    {
        int calls = 0, resets = 0;
        var session = Session(_ => { calls++; return Task.FromResult(new PreviewContent(PreviewKind.Media)); }, reset: () => resets++);
        await session.StartAsync();
        int previous = resets;
        session.ReportFailure(PreviewFailure.Network);
        Assert(session.State == PreviewState.Failed && resets > previous && calls == 1);
        await session.StartAsync();
        Assert(session.State == PreviewState.Ready && calls == 2);
        await session.CloseAsync();
        session.ReportFailure(PreviewFailure.Network);
        Assert(session.State == PreviewState.Closed);
        PreviewSession racing = null;
        racing = Session(_ => Task.FromResult(new PreviewContent(PreviewKind.Media)), (_, _) =>
        {
            racing.ReportFailure(PreviewFailure.InvalidContent);
            return Task.CompletedTask;
        });
        await racing.StartAsync();
        Assert(racing.State == PreviewState.Failed && racing.Failure == PreviewFailure.InvalidContent && racing.Attempt == 1);
        await racing.CloseAsync();
    }

    private static async Task Empty()
    {
        var session = Session(Loader([]).LoadAsync, (_, _) => throw new Exception("Empty content was rendered"));
        await session.StartAsync();
        Assert(session.State == PreviewState.Empty);
        await session.CloseAsync();
    }

    private static async Task ViewModel()
    {
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            ResourceHelper.Language = language;
            foreach (var failure in Enum.GetValues<PreviewFailure>().Where(f => f != PreviewFailure.None))
                Assert(!string.IsNullOrWhiteSpace(("Preview_Error_" + failure).GetLocalized()));
            foreach (var state in Enum.GetValues<PreviewState>())
                _ = ("Preview_Status_" + state).GetLocalized();
            var vm = new PreviewViewModel("bad.md", Loader([0xFF]), Fast);
            vm.Configure((_, _) => Task.CompletedTask, () => { });
            Assert(!vm.CanRetry && !vm.RetryCommand.CanExecute(null));
            await vm.StartAsync();
            Assert(vm.HasError && vm.CanRetry && vm.RetryCommand.CanExecute(null) && vm.ErrorMessage.Length > 0);
            await vm.CloseAsync();
            Assert(!vm.HasError && !vm.CanRetry && !vm.IsLoading && vm.StatusText == "");
        }
    }

    private static async Task GraphTargets()
    {
        using var fixture = new GraphFixture();
        var loader = GraphPreviewSource.Create(fixture.Provider, "chosen-item", PreviewKind.Pdf, Fast);
        string first = (await loader.LoadAsync(default)).Uri.AbsoluteUri;
        string second = (await loader.LoadAsync(default)).Uri.AbsoluteUri;
        Assert(first != second && fixture.Handler.Paths.All(p => p == "/v1.0/drives/chosen-drive/items/chosen-item"));
        fixture.Handler.Paths.Clear();
        var text = GraphPreviewSource.Create(fixture.Provider, "chosen-item", PreviewKind.Markdown, Fast);
        Assert((await text.LoadAsync(default)).Text == "abc");
        Assert(fixture.Handler.Paths.Last().EndsWith("/chosen-item/content"));
    }

    private static async Task GraphInvalid()
    {
        foreach (string response in new[]
        {
            """{"id":"wrong","file":{},"size":3}""",
            """{"id":"chosen-item","folder":{},"size":3}""",
            """{"id":"chosen-item","file":{},"remoteItem":{},"size":3}""",
            """{"id":"chosen-item","file":{},"parentReference":{"driveId":"foreign"},"size":3}"""
        })
        {
            using var fixture = new GraphFixture();
            fixture.Handler.Response = response;
            await Expect(PreviewFailure.InvalidContent, () =>
                GraphPreviewSource.Create(fixture.Provider, "chosen-item", PreviewKind.Pdf, Fast).LoadAsync(default));
        }
    }

    private static async Task GraphErrors()
    {
        using var fixture = new GraphFixture();
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        var session = Session(GraphPreviewSource.Create(fixture.Provider, "chosen-item", PreviewKind.Pdf, Fast).LoadAsync);
        await session.StartAsync();
        Assert(session.Failure == PreviewFailure.AccessDenied && fixture.Handler.Paths.Count == 1);
        await session.CloseAsync();
        fixture.Handler.Status = HttpStatusCode.OK;
        fixture.Handler.Stall = true;
        using var ct = new CancellationTokenSource();
        Task load = GraphPreviewSource.Create(fixture.Provider, "chosen-item", PreviewKind.Pdf).LoadAsync(ct.Token);
        ct.Cancel();
        try { await load; throw new Exception("Cancellation was lost"); } catch (OperationCanceledException) { }
    }

    private static async Task PdfProbe()
    {
        using var handler = new ProbeHandler();
        using var client = new HttpClient(handler);
        var uri = new Uri("https://example.test/document");
        await PreviewPdfProbe.ValidateAsync(uri, Fast, default, client);
        Assert(handler.ReadBytes <= 1024 && handler.Range == "bytes=0-1023");
        handler.Body = "<html>Server error</html>";
        await Expect(PreviewFailure.InvalidContent, () => PreviewPdfProbe.ValidateAsync(uri, Fast, default, client));
        foreach (var pair in new[] { (401, PreviewFailure.AccessDenied), (403, PreviewFailure.AccessDenied),
            (404, PreviewFailure.NotFound), (500, PreviewFailure.Network) })
        {
            handler.Status = pair.Item1;
            await Expect(pair.Item2, () => PreviewPdfProbe.ValidateAsync(uri, Fast, default, client));
        }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public string Body = "%PDF-1.4" + new string('x', 4096);
        public string Range;
        public int Status = 200;
        public long ReadBytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Range = request.Headers.Range?.ToString();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status)
            {
                Content = new StreamContent(new ProbeStream(Encoding.ASCII.GetBytes(Body), count => ReadBytes += count))
            });
        }
    }

    private sealed class ProbeStream(byte[] bytes, Action<int> read) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int count = await base.ReadAsync(buffer, token);
            read(count);
            return count;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object GetService(Type type) => null;
    }

    private sealed class TrackedStream : MemoryStream
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class StalledStream : MemoryStream
    {
        public bool Cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
            return 0;
        }
    }

    private sealed class GraphFixture : IDisposable
    {
        public readonly GraphHandler Handler = new();
        public readonly OneDrive Provider = new("chosen-drive", "local-account");
        private readonly GraphServiceClient _client;
        public GraphFixture()
        {
            _client = new(new HttpClient(Handler), new AnonymousAuthenticationProvider());
            typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Provider, _client);
        }
        public void Dispose() => _client.Dispose();
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        public List<string> Paths = [];
        public string Response;
        public bool Stall;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri.AbsolutePath);
            if (Stall) await Task.Delay(Timeout.Infinite, token);
            string body = request.RequestUri.AbsolutePath.EndsWith("/content") ? "abc" : Response ??
                $$"""{"id":"chosen-item","file":{},"size":3,"@microsoft.graph.downloadUrl":"https://example.test/{{Paths.Count}}"}""";
            if (Status != HttpStatusCode.OK) body = """{"error":{"code":"accessDenied","message":"Simulated rejection"}}""";
            return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
