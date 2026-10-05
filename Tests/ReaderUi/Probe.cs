using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views.Layout;
using OneDrive_Simple_Management_Tool.Helpers;

internal static class ReaderUiProbe
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new ProbeApp();
        });
    }
    private sealed class ProbeApp : App
    {
        private Window _window;
        private ReaderPage _reader;
        private Grid _host;
        private TextBlock _status;
        private bool _running;
        private readonly string _root = Environment.GetEnvironmentVariable("CLOUDFLOW_READER_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "ReaderTestData");
        private readonly string _log = Path.Combine(AppContext.BaseDirectory, "reader-ui-results.log");
        private string Book => Environment.GetEnvironmentVariable("CLOUDFLOW_READER_BOOK");
        private ApplicationDataPaths Paths => new(_root);
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Ioc.Default.ConfigureServices(new ProbeServices());
            File.WriteAllText(_log, "Reader UI probe: isolated local data only\n");
            _host = new Grid(); _host.RowDefinitions.Add(new() { Height = GridLength.Auto }); _host.RowDefinitions.Add(new());
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(8) };
            var run = new Button { Content = "Run reader checks" };
            run.Click += async (_, _) => await RunAsync();
            var open = new Button { Content = "Open local EPUB" };
            open.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
                picker.FileTypeFilter.Add(".epub");
                var file = await picker.PickSingleFileAsync();
                if (file != null) { await NewPageAsync(); await _reader.OpenLocalAsync(file.Path); }
            };
            var size = new Button { Content = "Narrow / wide" };
            size.Click += (_, _) => _window.AppWindow.Resize(new(_window.AppWindow.Size.Width > 700 ? 560 : 1100, 800));
            var theme = new Button { Content = "Dark / light" };
            theme.Click += (_, _) => _host.RequestedTheme = _host.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            _status = new TextBlock { Text = "Local EPUB only", VerticalAlignment = VerticalAlignment.Center };
            foreach (var item in new UIElement[] { run, open, size, theme, _status }) toolbar.Children.Add(item);
            _host.Children.Add(toolbar);
            _window = new Window { Title = "EPUB Reader — LOCAL TEST", Content = _host };
            _window.AppWindow.Resize(new(1100, 800));
            _window.Activate();
            if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") != null)
                _host.Loaded += async (_, _) => { if (!_running) await RunAsync(); };
        }
        private async Task NewPageAsync()
        {
            if (_reader != null) { await _reader.CloseAsync(); _host.Children.Remove(_reader); }
            _reader = new ReaderPage(Paths); Grid.SetRow(_reader, 1); _host.Children.Add(_reader);
            await Task.Delay(100);
        }
        private void Record(string value) { File.AppendAllText(_log, value + "\n"); _status.Text = value; }
        private static void Require(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
        private async Task RunAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") == "cloud")
                {
                    VerifyMenus();
                    await RunCloudAsync(); Record("ALL READER UI CHECKS PASSED"); return;
                }
                await NewPageAsync();
                await _reader.OpenLocalAsync(Book);
                var session = _reader.ViewModel.Session;
                Require(session.State == ReaderState.Ready, "Open:" + session.ErrorCode);
                Require(session.Toc.Count > 0 && session.Location?.Cfi != null, "MissingContentsOrPosition");
                Record("PASS native WebView2 ready, TOC and confirmed location");
                Record("WebView2 runtime: " + _reader.Controller.RuntimeVersion);
                if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") == "restore")
                {
                    Require(session.Location.Fraction > 0.1, "CrossProcessProgressNotRestored");
                    Require(session.Settings.FontSize == 26 && session.Settings.Theme == "dark", "CrossProcessSettingsNotRestored");
                    Record("PASS cross-process DPAPI progress and settings restore");
                }
                else
                {
                    if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") == "bookscan")
                    {
                        foreach (var entry in session.Toc)
                        {
                            await session.NavigateAsync(entry.Id);
                            Require(session.State == ReaderState.Ready && session.Location?.Cfi != null, "BookContentsNavigation");
                        }
                        Record("PASS all " + session.Toc.Count + " real-book TOC destinations");
                    }
                    await session.NavigateAsync(session.Toc.Last().Id);
                    Require(session.State == ReaderState.Ready, "TocNavigation");
                    await session.TurnAsync("next");
                    await session.ApplySettingsAsync(new() { FontSize = 26, LineHeight = 1.8, Theme = "dark", Zoom = "fit-width" });
                    Require(session.State == ReaderState.Ready && session.Settings.FontSize == 26, "Settings");
                    if (session.FixedLayout)
                    {
                        Require(_reader.ViewModel.IsFixedReady && !_reader.ViewModel.IsReflowableReady && session.Settings.Zoom == "fit-width", "FixedLayoutControls");
                        Record("PASS fixed-layout zoom and disabled reflow controls");
                    }
                    await session.FlushAsync(); Require(!session.SaveFailed, "Storage");
                    Record("PASS TOC, turn, settings and protected persistence");
                }
                var core = _reader.Controller.Core;
                using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "reader-body.png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream.AsRandomAccessStream());
                await CaptureLayoutAsync();
                if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") == "full")
                {
                    using (var dom = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":-1,\"pierce\":true}")))
                    {
                        var frames = Frames(dom.RootElement).ToArray();
                        Require(frames.Length > 0 && frames.All(node =>
                        {
                            var attributes = node.GetProperty("attributes").EnumerateArray().Select(x => x.GetString()).ToArray();
                            int sandbox = Array.IndexOf(attributes, "sandbox");
                            return sandbox >= 0 && attributes[sandbox + 1] == "allow-same-origin";
                        }), "BookScriptSandbox");
                    }
                    string script = """
                        (async () => {
                            window.readerProbeExecutions = 0;
                            const violations = [];
                            const listener = e => violations.push(e.effectiveDirective);
                            document.addEventListener('securitypolicyviolation', listener);
                            const script = document.createElement('script');
                            script.textContent = 'window.readerProbeExecutions++'; document.head.append(script);
                            const image = new Image(); image.src = 'https://example.com/reader-image-probe'; document.body.append(image);
                            await fetch('https://example.com/reader-connect-probe').catch(() => {});
                            const response = await fetch('/reader/index.html');
                            const refused = await fetch('/reader/unknown');
                            await new Promise(resolve => setTimeout(resolve, 150));
                            document.removeEventListener('securitypolicyviolation', listener); script.remove(); image.remove();
                            return readerProbeExecutions === 0 && violations.includes('script-src-elem')
                                && violations.includes('img-src') && violations.includes('connect-src')
                                && response.headers.get('content-security-policy').includes("script-src 'self'") && refused.status === 403;
                        })()
                        """;
                    using (var check = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }))))
                        Require(check.RootElement.GetProperty("result").GetProperty("value").GetBoolean(), "NativeCsp");
                    Record("PASS native CSP response, script sandbox, external resource blocks and unknown route refusal");
                    int hostBack = 0;
                    session.BackRequested += () => hostBack++;
                    var created = new TaskCompletionSource<CoreWebView2Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.FrameCreated += (_, args) => created.TrySetResult(args.Frame);
                    await core.ExecuteScriptAsync("window.nativeProbeFrame=document.createElement('iframe');nativeProbeFrame.name='native-probe';nativeProbeFrame.sandbox='allow-same-origin allow-scripts';nativeProbeFrame.src=URL.createObjectURL(new Blob(['<html><body>Probe</body></html>'],{type:'text/html'}));document.body.append(nativeProbeFrame);");
                    var frame = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    int frameMessages = 0;
                    frame.WebMessageReceived += (_, _) => frameMessages++;
                    await Task.Delay(250);
                    string forged = JsonSerializer.Serialize(new { version = 1, sessionId = session.SessionId, requestId = (string)null, type = "hostCommand", payload = new { command = "back" } });
                    await frame.ExecuteScriptAsync("chrome.webview.postMessage(" + forged + ");");
                    await Task.Delay(150);
                    Require(frameMessages == 1 && hostBack == 0 && session.State == ReaderState.Ready, "FrameMessageIsolation:" + frameMessages + ":" + hostBack + ":" + session.State);
                    await core.ExecuteScriptAsync("URL.revokeObjectURL(nativeProbeFrame.src);nativeProbeFrame.remove();delete window.nativeProbeFrame;");
                    Record("PASS native frame messages cannot invoke host commands");

                    string allowed = core.Source;
                    core.Navigate("https://example.com/forbidden");
                    await Task.Delay(150);
                    Require(core.Source == allowed && session.State == ReaderState.Ready, "ExternalNavigation");
                    Record("PASS external top navigation blocked");

                    var position = session.Location;
                    string oldSession = session.SessionId;
                    try { await core.CallDevToolsProtocolMethodAsync("Page.crash", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                    await WaitAsync(() => session.State == ReaderState.Failed);
                    Require(session.ErrorCode == "ProcessFailed", "CrashNotReported");
                    await session.RetryAsync();
                    Require(session.State == ReaderState.Ready && session.SessionId != oldSession, "CrashRetry");
                    Require(Math.Abs(session.Location.Fraction.Value - position.Fraction.Value) < 0.02, "CrashPositionLost");
                    Record("PASS owned renderer crash, new control and position restore");

                    _window.AppWindow.Resize(new(560, 720)); _host.RequestedTheme = ElementTheme.Dark;
                    await Task.Delay(300);
                    Require(_reader.ActualWidth <= 560 && _reader.ActualWidth > 300, "NarrowLayout");
                    await session.ApplySettingsAsync(session.Settings with { Flow = "scrolled", FontSize = 24 });
                    Require(session.State == ReaderState.Ready, "NarrowSettings");
                    Record("PASS narrow window, dark host and scrolled content");
                    await CaptureLayoutAsync();
                }
                await _reader.CloseAsync();
                Require(_reader.Controller.ResponseStreamCount == 0 && _reader.Controller.OutstandingRequests == 0, "ResourceCleanup");
                Record("PASS close releases native responses and deferrals");
                if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") == "full")
                {
                    for (int cycle = 0; cycle < 5; cycle++)
                    {
                        await NewPageAsync(); await _reader.OpenLocalAsync(Book);
                        Require(_reader.ViewModel.Session.State == ReaderState.Ready, "Reopen");
                        await _reader.CloseAsync();
                        Require(_reader.Controller.ResponseStreamCount == 0 && _reader.Controller.OutstandingRequests == 0, "CycleResources");
                    }
                    Record("PASS five normal native open-close cycles");
                    await NewPageAsync();
                    var opening = _reader.OpenLocalAsync(Book);
                    await WaitAsync(() => _reader.ViewModel.Session.State is ReaderState.Loading or ReaderState.Restoring or ReaderState.Ready);
                    await _reader.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4));
                    await opening;
                    Require(_reader.ViewModel.Session.State == ReaderState.Closed, "LateInitialization");
                    Record("PASS close during native initialization");
                }
                Record("ALL READER UI CHECKS PASSED");
            }
            catch (Exception error) { Record("FAIL " + error.GetType().Name + ": " + (error is InvalidOperationException ? error.Message : "OperationFailed")); }
            finally
            {
                if (_reader != null) await _reader.CloseAsync();
                if (Environment.GetEnvironmentVariable("CLOUDFLOW_READER_AUTORUN") != null) _window.Close();
                _running = false;
            }
        }
        private static async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(8000);
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        private async Task RunCloudAsync()
        {
            await using var cloud = new ReaderCloudFixture(Book);
            await NewPageAsync(); await _reader.OpenAsync(cloud.Request);
            var session = _reader.ViewModel.Session;
            Require(session.State == ReaderState.Ready, "CloudOpen:" + session.ErrorCode);
            Record("PASS native download, protected cache index and cloud identity open");
            await session.NavigateAsync(session.Toc.Last().Id); var saved = session.Location; await _reader.CloseAsync();
            int downloads = cloud.Gets;
            await NewPageAsync(); await _reader.OpenAsync(cloud.Request);
            session = _reader.ViewModel.Session;
            Require(session.State == ReaderState.Ready && cloud.Gets == downloads && Math.Abs(session.Location.Fraction.Value - saved.Fraction.Value) < .02, "CloudReopen");
            Record("PASS cache hit, new page and cloud reading progress restore");
            await _reader.CloseAsync(); cloud.Version = "ctag:native-v2";
            await NewPageAsync(); await _reader.OpenAsync(cloud.Request); session = _reader.ViewModel.Session;
            Require(session.State == ReaderState.Ready && cloud.Gets > downloads && session.NoticeKey == "Reader_Approximate", "CloudVersionChange");
            Record("PASS changed cloud version downloads and restores approximate position");
            await _reader.CloseAsync(); cloud.Failure = "AccessDenied";
            await NewPageAsync(); await _reader.OpenAsync(cloud.Request); session = _reader.ViewModel.Session;
            Require(session.State == ReaderState.Failed && session.ErrorCode == "AccessDenied", "CloudPermission");
            cloud.Failure = null; await session.RetryAsync();
            Require(session.State == ReaderState.Ready, "CloudPermissionRetry");
            Record("PASS permission failure blocks cache and explicit retry recovers");
            await CaptureLayoutAsync();
            await _reader.CloseAsync();
            await new ReaderCacheService(Paths).ClearAsync();
            Require(File.Exists(Paths.ReaderProgress) && !Directory.EnumerateFiles(Paths.ReaderCache, "*.epub").Any(), "CloudCacheClear");
            Record("PASS native close and cache clear preserve encrypted progress");
        }
        private void VerifyMenus()
        {
            var drive = new DriveViewModel(new OneDrive("test-drive", "test-account", null));
            foreach (var view in new UserControl[] { new ColumnFileView(), new GirdFileView() })
            {
                foreach (var (item, expected) in new[]
                {
                    (new Microsoft.Graph.Models.DriveItem { Id = "book", Name = "sample.EPUB", File = new() }, true),
                    (new Microsoft.Graph.Models.DriveItem { Id = "text", Name = "sample.txt", File = new() }, false),
                    (new Microsoft.Graph.Models.DriveItem { Id = "folder", Name = "sample.epub", Folder = new() }, false),
                    (new Microsoft.Graph.Models.DriveItem { Id = "remote", Name = "sample.epub", File = new(), RemoteItem = new() }, false)
                })
                {
                    var file = new FileViewModel(drive, item); view.DataContext = file;
                    Require(file.CanRead == expected && ((MenuFlyout)view.ContextFlyout).Items.OfType<MenuFlyoutItem>()
                        .Any(entry => entry.Text == "FileView_Flyout_Read/Text".GetLocalized()) == expected, "ReaderMenuScope");
                }
            }
            Record("PASS native list/grid menus restrict reader entry to ordinary EPUB files");
        }
        private sealed class ProbeServices : IServiceProvider { public object GetService(Type serviceType) => null; }
        private static IEnumerable<JsonElement> Frames(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("nodeName", out var name) && name.GetString() == "IFRAME") yield return element;
                foreach (var property in element.EnumerateObject()) foreach (var frame in Frames(property.Value)) yield return frame;
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) foreach (var frame in Frames(item)) yield return frame;
        }
        private async Task CaptureLayoutAsync()
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(_reader);
            var pixels = await bitmap.GetPixelsAsync();
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "reader-layout.png"));
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
    }
}
