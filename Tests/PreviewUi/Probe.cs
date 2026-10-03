using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using OneDrive_Simple_Management_Tool.Views.Preview;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class PreviewUiProbe
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
        private StackPanel _panel;
        private TextBlock _results;
        private LoopbackServer _pdf;
        private LoopbackServer _media;
        private LoopbackServer _document;
        private bool _running;
        private readonly string _log = Path.Combine(AppContext.BaseDirectory, "preview-ui-results.log");
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=");
        private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" width="240" height="120"><rect width="240" height="120" fill="#1976d2"/><circle cx="120" cy="60" r="40" fill="#ffffff"/></svg>""");
        private static readonly byte[] Markdown = Encoding.UTF8.GetBytes(
            "# Local preview\n\n中文 Markdown **预览可靠性测试**。\n\n- Loading and close\n- Retry and download\n\n| File | Result |\n| --- | --- |\n| Notes.md | Ready |\n");
        private static readonly PreviewOptions Options = new()
        {
            RequestTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(3),
            RenderTimeout = TimeSpan.FromSeconds(10), RetryDelay = TimeSpan.FromMilliseconds(100)
        };

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _pdf = new() { Bytes = Pdf(), ContentType = "application/pdf", DelayMilliseconds = 0 };
            _media = new() { Bytes = Wav(), ContentType = "audio/wav", DelayMilliseconds = 0 };
            _document = new() { Bytes = Markdown, ContentType = "text/markdown", DelayMilliseconds = 0 };
            Ioc.Default.ConfigureServices(new TestServices());
            _panel = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
            _results = new TextBlock { Text = "Synthetic files only; no account or cloud data.", TextWrapping = TextWrapping.Wrap };
            _panel.Children.Add(_results);
            Add("Run preview regression", RunAsync);
            Add("Markdown compatibility", () => ReadingProbe.ValidateMarkdownAsync(_panel.XamlRoot, Record));
            Add("Run reading regression", () => ReadingProbe.ValidateReadingAsync(_panel.XamlRoot, Record, _window));
            Add("Reading Markdown", () => ShowAsync("基础阅读_代码与表格_很长的文件名_Reading.md", PreviewKind.Markdown, Encoding.UTF8.GetBytes(ReadingProbe.Sample)));
            Add("Reading TXT", () => ShowAsync("中文文本.txt", PreviewKind.Text, Encoding.UTF8.GetBytes("中文文本选择复制 😀\r\n" + new string('字', 500))));
            Add("GBK TXT", () => ShowAsync("旧编码.txt", PreviewKind.Text, CodePagesEncodingProvider.Instance.GetEncoding(936).GetBytes("中文旧编码文件\r\n第二行")));
            Add("Large image", async () => await ShowAsync("Large.png", PreviewKind.Image, await ReadingProbe.LargePngAsync()));
            Add("Markdown", () => ShowAsync("Notes.md", PreviewKind.Markdown, Markdown));
            Add("Image PNG", () => ShowAsync("Pixel.png", PreviewKind.Image, Png));
            Add("Image SVG", () => ShowAsync("Shapes.svg", PreviewKind.Image, Svg));
            Add("PDF", () => ShowAsync("Sample.pdf", PreviewKind.Pdf, null));
            Add("Audio WAV", () => ShowAsync("Silence.wav", PreviewKind.Media, null));
            Add("Broken image / retry", () => ShowAsync("Broken.png", PreviewKind.Image, [1, 2, 3]));
            Add("Oversized Markdown", () => ShowAsync("Large.md", PreviewKind.Markdown, new byte[2 * 1024 * 1024 + 1]));
            Add("Slow request / close", () => ShowAsync("Slow.md", PreviewKind.Markdown, Markdown, 5000));
            Add("Failed preview -> download picker", async () =>
            {
                using var client = new GraphServiceClient(new HttpClient(new DownloadHandler(_document)), new AnonymousAuthenticationProvider());
                var provider = new OneDrive("test-drive", "local") { IsAuthenticated = true };
                typeof(OneDrive).GetField("graphClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, client);
                var file = new FileViewModel(new DriveViewModel(provider, "Local test"), new Microsoft.Graph.Models.DriveItem
                {
                    Id = "download-item", Name = "Notes-download.md", File = new Microsoft.Graph.Models.FileObject(), Size = Markdown.Length
                });
                await FileActions.ExecuteAsync(FileAction.Open, _panel, file);
                Record("Download fallback returned: " + file.Drive.ErrorMessage);
            });
            Add("Narrow / wide", () =>
            {
                _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(_window.AppWindow.Size.Width > 700 ? 540 : 1100, 800));
                return Task.CompletedTask;
            });
            Add("Dark / light", () =>
            {
                _panel.RequestedTheme = _panel.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                _panel.Background = new SolidColorBrush(_panel.RequestedTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
                return Task.CompletedTask;
            });
            _window = new Window { Title = "Preview UI regression — LOCAL", Content = new ScrollViewer { Content = _panel } };
            typeof(App).GetField("m_window", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, _window);
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
            _window.Closed += async (_, _) => { await _pdf.DisposeAsync(); await _media.DisposeAsync(); await _document.DisposeAsync(); };
            _window.Activate();
            File.WriteAllText(_log, "Started\n");
            if (Environment.GetEnvironmentVariable("CLOUDFLOW_PREVIEW_AUTORUN") == "1")
                _panel.Loaded += async (_, _) => { if (!_running) await RunAsync(); };
            if (Environment.GetEnvironmentVariable("CLOUDFLOW_PREVIEW_AUTORUN") == "reading")
                _panel.Loaded += async (_, _) =>
                {
                    try { await ReadingProbe.ValidateReadingAsync(_panel.XamlRoot, Record, _window); }
                    catch (Exception exception) { Record("FAIL reading: " + exception); }
                };
        }

        private void Add(string label, Func<Task> action)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception exception) { Record("FAIL " + label + ": " + exception); }
            };
            _panel.Children.Add(button);
        }

        private (ContentDialog Dialog, PreviewViewModel Vm) Create(string name, PreviewKind kind, byte[] bytes, int delay = 0,
            Func<bool> reject = null)
        {
            var loader = new PreviewContentLoader(kind, async token =>
            {
                if (delay > 0) await Task.Delay(delay, token);
                if (reject?.Invoke() == true) throw new PreviewException(PreviewFailure.AccessDenied);
                return new PreviewMetadata(bytes?.Length ?? (kind == PreviewKind.Pdf ? _pdf.Bytes.Length : _media.Bytes.Length),
                    kind == PreviewKind.Pdf ? _pdf.Url : _media.Url);
            }, _ => Task.FromResult<Stream>(new MemoryStream(bytes ?? [])), Options);
            var vm = new PreviewViewModel(name, loader, Options);
            ContentDialog dialog = kind switch
            {
                PreviewKind.Text => new TextPreviewView(),
                PreviewKind.Markdown => new MarkdownPreviewView(),
                PreviewKind.Image => new ImagePreviewView(),
                PreviewKind.Pdf => new PdfPreviewView(),
                _ => new MediaPreviewView()
            };
            dialog.DataContext = vm;
            dialog.XamlRoot = _panel.XamlRoot;
            dialog.RequestedTheme = _panel.ActualTheme;
            return (dialog, vm);
        }

        private async Task ShowAsync(string name, PreviewKind kind, byte[] bytes, int delay = 0)
        {
            var (dialog, vm) = Create(name, kind, bytes, delay);
            await dialog.ShowAsync();
            await vm.CloseAsync();
            Record(name + " closed; download requested: " + vm.DownloadRequested);
        }

        private async Task CheckAsync(string name, PreviewKind kind, byte[] bytes, PreviewState expected)
        {
            var (dialog, vm) = Create(name, kind, bytes);
            var showing = dialog.ShowAsync().AsTask();
            try
            {
                await WaitAsync(() => vm.State is PreviewState.Ready or PreviewState.Empty or PreviewState.Failed);
                if (vm.State != expected) throw new Exception(name + ": " + vm.State + " " + vm.ErrorMessage);
                if (name == "TooManyPixels.bmp" && vm.ErrorMessage != "Preview_Error_TooLarge".GetLocalized())
                    throw new Exception("Image pixel limit was not enforced before decoding");
                if (vm.HasError != dialog.IsPrimaryButtonEnabled) throw new Exception("Retry button state mismatch");
                await Task.Delay(120);
            }
            finally
            {
                dialog.Hide();
                await showing;
                await vm.CloseAsync();
            }
            if (vm.State != PreviewState.Closed) throw new Exception("Close was not terminal");
            Record("PASS " + name + " -> " + expected);
        }

        private async Task RunAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                File.WriteAllText(_log, "Running\n");
                await CheckAsync("Notes.md", PreviewKind.Markdown, Markdown, PreviewState.Ready);
                await CheckAsync("Empty.md", PreviewKind.Markdown, [], PreviewState.Empty);
                await CheckAsync("Pixel.png", PreviewKind.Image, Png, PreviewState.Ready);
                await CheckAsync("Shapes.svg", PreviewKind.Image, Svg, PreviewState.Ready);
                await CheckAsync("Broken.png", PreviewKind.Image, [1, 2, 3], PreviewState.Failed);
                await CheckAsync("Large.md", PreviewKind.Markdown, new byte[2 * 1024 * 1024 + 1], PreviewState.Failed);
                await CheckAsync("Sample.pdf", PreviewKind.Pdf, null, PreviewState.Ready);
                _pdf.Bytes = Encoding.UTF8.GetBytes("<html>Server error</html>");
                await CheckAsync("Invalid.pdf", PreviewKind.Pdf, null, PreviewState.Failed);
                _pdf.Bytes = Pdf();
                _pdf.StatusCode = 403;
                await CheckAsync("Denied.pdf", PreviewKind.Pdf, null, PreviewState.Failed);
                _pdf.StatusCode = 200;
                _pdf.Attachment = true;
                await CheckAsync("Attachment.pdf", PreviewKind.Pdf, null, PreviewState.Failed);
                _pdf.Attachment = false;
                await CheckAsync("Silence.wav", PreviewKind.Media, null, PreviewState.Ready);
                _media.Bytes = [1, 2, 3];
                await CheckAsync("Broken.wav", PreviewKind.Media, null, PreviewState.Failed);
                _media.Bytes = Wav();
                await CheckAsync("TooManyPixels.bmp", PreviewKind.Image, LargeBitmap(), PreviewState.Failed);
                _document.Bytes = Png;
                _document.ContentType = "image/png";
                await CheckAsync("InlineImage.md", PreviewKind.Markdown,
                    Encoding.UTF8.GetBytes("# Image\n\n![local image](" + _document.Url + ")"), PreviewState.Ready);
                _document.Bytes = Markdown;
                _document.ContentType = "text/markdown";
                var (audio, audioVm) = Create("Playing.wav", PreviewKind.Media, null);
                var audioShowing = audio.ShowAsync().AsTask();
                await WaitAsync(() => audioVm.State == PreviewState.Ready);
                var audioHost = (Grid)audio.FindName("ContentHost");
                var playerElement = (MediaPlayerElement)audioHost.Children.Single();
                var player = playerElement.MediaPlayer;
                player.Play();
                await WaitAsync(() => player.PlaybackSession.Position > TimeSpan.FromMilliseconds(100));
                audio.Hide();
                await audioShowing;
                await audioVm.CloseAsync();
                if (audioHost.Children.Count != 0 || playerElement.MediaPlayer != null) throw new Exception("Player was not detached");
                Record("PASS close while playing disposes and detaches player");

                var (slow, slowVm) = Create("Slow.md", PreviewKind.Markdown, Markdown, 5000);
                var slowShowing = slow.ShowAsync().AsTask();
                await Task.Delay(200);
                slow.Hide();
                await slowShowing;
                await slowVm.CloseAsync();
                Record("PASS close during request");

                bool reject = true;
                var (retry, retryVm) = Create("Retry.md", PreviewKind.Markdown, Markdown, reject: () => reject);
                var retryShowing = retry.ShowAsync().AsTask();
                await WaitAsync(() => retryVm.State == PreviewState.Failed);
                reject = false;
                await retryVm.RetryCommand.ExecuteAsync(null);
                if (retryVm.State != PreviewState.Ready) throw new Exception("Retry failed");
                retry.Hide();
                await retryShowing;
                Record("PASS retry in same dialog");

                for (int i = 0; i < 3; i++) await CheckAsync("Warmup.png", PreviewKind.Image, Png, PreviewState.Ready);
                GC.Collect(); GC.WaitForPendingFinalizers();
                var process = Process.GetCurrentProcess();
                process.Refresh();
                long before = process.PrivateMemorySize64;
                int handlesBefore = process.HandleCount;
                for (int i = 0; i < 20; i++)
                {
                    await CheckAsync("Repeat.png", PreviewKind.Image, Png, PreviewState.Ready);
                    await CheckAsync("Repeat.pdf", PreviewKind.Pdf, null, PreviewState.Ready);
                    await CheckAsync("Repeat.wav", PreviewKind.Media, null, PreviewState.Ready);
                    if ((i + 1) % 5 == 0)
                    {
                        GC.Collect();
                        await Task.Delay(200);
                        process.Refresh();
                        Record($"Cycle {i + 1}: private bytes {process.PrivateMemorySize64}; handles {process.HandleCount}");
                    }
                }
                GC.Collect(); GC.WaitForPendingFinalizers();
                process.Refresh();
                Record($"20 reopen cycles: private bytes {before} -> {process.PrivateMemorySize64}; handles {handlesBefore} -> {process.HandleCount}");
                Record("ALL AUTOMATED UI CHECKS PASSED");
                _window.Title = "Preview UI regression — LOCAL — PASSED";
            }
            catch (Exception exception)
            {
                Record("FAIL " + exception);
                _window.Title = "Preview UI regression — LOCAL — FAILED";
            }
            finally { _running = false; _pdf.StatusCode = 200; _pdf.Attachment = false; }
        }

        private void Record(string line)
        {
            File.AppendAllText(_log, line + Environment.NewLine);
            _results.Text = line;
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            for (int i = 0; !condition() && i < 600; i++) await Task.Delay(50);
            if (!condition()) throw new TimeoutException("UI did not reach a terminal loading state.");
        }

        private static byte[] Wav()
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);
            const int length = 16000 * 2 * 3;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length); writer.Write(new byte[length]);
            return memory.ToArray();
        }

        private static byte[] LargeBitmap()
        {
            // A 48 MP monochrome BMP is only 6 MB on disk; the decoder must check pixels before allocation.
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);
            writer.Write((ushort)0x4D42); writer.Write(62 + 6_000_000); writer.Write(0); writer.Write(62);
            writer.Write(40); writer.Write(8000); writer.Write(6000); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(0); writer.Write(6_000_000); writer.Write(0); writer.Write(0); writer.Write(2); writer.Write(0);
            writer.Write(0); writer.Write(0x00FFFFFF); writer.Write(new byte[6_000_000]);
            return memory.ToArray();
        }

        private static byte[] Pdf()
        {
            string stream = "BT /F1 24 Tf 35 100 Td (Local PDF preview) Tj ET";
            string[] objects =
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 360 220] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream"
            ];
            var text = new StringBuilder("%PDF-1.4\n");
            var offsets = new System.Collections.Generic.List<int>();
            for (int i = 0; i < objects.Length; i++)
            {
                offsets.Add(text.Length);
                text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            int xref = text.Length;
            text.Append("xref\n0 6\n0000000000 65535 f \n");
            foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
            text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
            return Encoding.ASCII.GetBytes(text.ToString());
        }
    }

    private sealed class TestServices : IServiceProvider
    {
        private readonly TaskManagerViewModel _manager = new();
        public object GetService(Type type) => type == typeof(TaskManagerViewModel) ? _manager : null;
    }

    private sealed class DownloadHandler(LoopbackServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/content"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF]) });
            string json = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["id"] = "download-item", ["name"] = "Notes-download.md", ["size"] = server.Bytes.Length,
                ["cTag"] = "v1", ["file"] = new { mimeType = "text/markdown" }, ["@microsoft.graph.downloadUrl"] = server.Url
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
