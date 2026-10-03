using CommunityToolkit.Common.Parsers.Markdown;
using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.Views.Preview;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Graphics.Imaging;

internal static class ReadingProbe
{
    internal const string Sample = """
        # 阅读样本 Reading sample

        中文 **粗体**、*斜体*、`inline code` 与表情 😀。

        > 引用文字

        - 一级列表
            - 二级列表

        ```csharp
        public static string Hello() => "中文 hello";
        // LONG_CODE_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ
        ```

        ```unknown-language
        UNKNOWN_CODE stays readable
        ```

        | 第一列 | Second | Third | Fourth | Fifth | Sixth |
        | --- | --- | --- | --- | --- | --- |
        | TABLE_CELL_ABCDEFGHIJKLMNOPQRSTUVWXYZ | Table cell | Table cell | Table cell | Table cell | Table cell |
        """;

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static async Task ValidateMarkdownAsync(XamlRoot root, Action<string> record)
    {
#pragma warning disable CS0618 // Verify the exact legacy parser still used by production.
        var document = new MarkdownDocument();
#pragma warning restore CS0618
        document.Parse(Sample);
        var blocks = document.Blocks.Select(block => block.GetType().Name).ToArray();
        foreach (string expected in new[] { "HeaderBlock", "ParagraphBlock", "QuoteBlock", "ListBlock", "CodeBlock", "TableBlock" })
            if (!blocks.Contains(expected)) throw new Exception("Missing parsed block: " + expected);
        var rendered = new TaskCompletionSource();
        var markdown = new MarkdownTextBlock
        {
            IsTextSelectionEnabled = true, UseSyntaxHighlighting = true, WrapCodeBlock = false,
            TextWrapping = TextWrapping.Wrap, Width = 420
        };
        markdown.SetRenderer<ReadingMarkdownRenderer>();
        markdown.MarkdownRendered += (_, args) =>
        {
            if (args.Exception != null) rendered.TrySetException(args.Exception);
            else rendered.TrySetResult();
        };
        var scroll = new ScrollViewer { Content = markdown, Width = 420, Height = 500,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dialog = new ContentDialog { XamlRoot = root, Title = "Markdown compatibility", Content = scroll, CloseButtonText = "Close" };
        var showing = dialog.ShowAsync().AsTask();
        try
        {
            markdown.Text = Sample;
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var viewers = Descendants(markdown).OfType<ScrollViewer>().ToArray();
            record("Markdown parsed: " + string.Join(", ", blocks));
            record("Markdown horizontal viewers: " + string.Join("; ", viewers.Select(viewer => $"{viewer.HorizontalScrollBarVisibility}: {viewer.ViewportWidth}/{viewer.ExtentWidth}")));
            var text = string.Join("\n", Descendants(markdown).OfType<RichTextBlock>().SelectMany(b => b.Blocks)
                .OfType<Microsoft.UI.Xaml.Documents.Paragraph>().SelectMany(p => p.Inlines)
                .OfType<Microsoft.UI.Xaml.Documents.Run>().Select(r => r.Text));
            record("Markdown text rendered; rich blocks=" + Descendants(markdown).OfType<RichTextBlock>().Count());
            if (viewers.Count(v => v.ExtentWidth > v.ViewportWidth) < 2) throw new Exception("Long code and table must scroll independently");
            record("PASS Markdown 7.1.2 compatibility baseline");
        }
        finally { dialog.Hide(); await showing; }
    }

    internal static async Task ValidateReadingAsync(XamlRoot root, Action<string> record, Window window = null)
    {
        var drive = new OneDrive_Simple_Management_Tool.ViewModels.DriveViewModel(new OneDrive("local", "local"), "Local");
        var file = new OneDrive_Simple_Management_Tool.ViewModels.FileViewModel(drive, new Microsoft.Graph.Models.DriveItem
        {
            Id = "text", Name = "中文.TXT", File = new Microsoft.Graph.Models.FileObject()
        });
        if (!file.CanPreview || !file.CanOpen) throw new Exception("Uppercase TXT entry point");
        file.UpdateName("data.bin");
        if (file.CanPreview || file.CanOpen) throw new Exception("Stale preview capability after rename");
        file.UpdateName("again.txt");
        if (!file.CanPreview || !file.CanOpen) throw new Exception("Rename to TXT");
        record("PASS production TXT capability and rename notifications");
        var svgInches = await PreviewImageDecoder.ReadSizeAsync(Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" width="2in" height="1in"/>"""), true, default);
        if (svgInches != (192d, 96d)) throw new Exception("SVG absolute units");
        await ValidateMarkdownAsync(root, record);
        byte[] gbk = CodePagesEncodingProvider.Instance.GetEncoding(936).GetBytes("中文编码切换\r\n第二行");
        await Check("旧编码.txt", PreviewKind.Text, gbk, async (dialog, vm, calls) =>
        {
            if (!vm.HasError) throw new Exception("GBK auto detection must request explicit encoding; state=" + vm.State);
            await vm.ChangeEncodingAsync("GBK");
            var box = Descendants(dialog).OfType<TextBox>().Single(b => b.AcceptsReturn);
            if (Normalize(box.Text) != "中文编码切换\n第二行" || calls() != 1)
                throw new Exception("Cached GBK decoding failed: " + System.Text.Json.JsonSerializer.Serialize(box.Text) + "; requests=" + calls() + "; state=" + vm.State);
            box.SelectAll();
            if (box.SelectedText != box.Text || !box.IsReadOnly) throw new Exception("Selection failed");
            vm.ReadingFontSize = 24;
            vm.WrapText = false;
            if (box.FontSize != 24 || box.TextWrapping != TextWrapping.NoWrap) throw new Exception("Text settings did not apply");
            if (window != null)
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 800));
                await Task.Delay(400);
                if (dialog.ActualWidth > root.Size.Width + 1 || vm.State != PreviewState.Ready)
                    throw new Exception("Narrow layout overflowed or closed the session");
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
                await Task.Delay(300);
            }
            await vm.ChangeEncodingAsync("Auto");
            if (!vm.HasError) throw new Exception("Invalid decoding did not fail");
            await vm.ChangeEncodingAsync("GB18030");
            if (vm.State != PreviewState.Ready || calls() != 1) throw new Exception("Decode recovery failed");
            record("PASS TXT select-all, font, wrap, cached encoding recovery");
        });
        await Check("长文件名_基础阅读体验_窗口自适应_代码块与宽表格.md", PreviewKind.Markdown, Encoding.UTF8.GetBytes(Sample),
            async (dialog, vm, _) =>
            {
                if (vm.State != PreviewState.Ready) throw new Exception("Markdown failed");
                vm.ReadingFontSize = 22;
                await Task.Delay(300);
                if (Descendants(dialog).OfType<MarkdownTextBlock>().Single().FontSize != 22) throw new Exception("Markdown font");
                vm.ShowSource = true;
                var box = Descendants(dialog).OfType<TextBox>().Single(b => b.AcceptsReturn);
                box.SelectAll();
                if (Normalize(box.SelectedText) != Normalize(Sample)) throw new Exception("Markdown source is incomplete");
                vm.ShowSource = false;
                await Task.Delay(100);
                record("PASS Markdown font and complete source selection");
            });
        await Check("长文.md", PreviewKind.Markdown, Encoding.UTF8.GetBytes(new string('文', 210000)),
            (dialog, vm, _) =>
            {
                if (vm.State != PreviewState.Ready || Descendants(dialog).OfType<TextBox>().Single(b => b.AcceptsReturn).Text.Length != 210000)
                    throw new Exception("Long Markdown fallback was truncated");
                record("PASS long Markdown falls back to complete selectable text");
                return Task.CompletedTask;
            });
        await Check("Empty.txt", PreviewKind.Text, [],
            (dialog, vm, _) =>
            {
                if (vm.State != PreviewState.Empty) throw new Exception("Empty TXT");
                record("PASS empty TXT"); return Task.CompletedTask;
            });
        byte[] svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 240 120"><rect width="240" height="120" fill="royalblue"/></svg>""");
        await Check("ViewBox.svg", PreviewKind.Image, svg, async (dialog, vm, _) =>
        {
            if (vm.State != PreviewState.Ready) throw new Exception("SVG failed: " + vm.State + " " + vm.ErrorMessage);
            var surface = Descendants(dialog).OfType<PreviewImageSurface>().Single();
            surface.Zoom(1);
            await Task.Delay(250);
            var img = Descendants(surface).OfType<Image>().Single();
            if (img.Width != 240 || img.Height != 120) throw new Exception("SVG aspect ratio / original size");
            surface.Zoom(2);
            await Task.Delay(400);
            surface.Fit();
            record("PASS SVG fit, zoom, original size and aspect ratio");
        });
        await Check("Large.png", PreviewKind.Image, await LargePngAsync(), async (dialog, vm, _) =>
        {
            var surface = Descendants(dialog).OfType<PreviewImageSurface>().Single();
            surface.Zoom(1);
            await Task.Delay(700);
            var img = Descendants(surface).OfType<Image>().Single();
            if (Math.Abs(img.Width * root.RasterizationScale - 3000) > 1
                || Math.Abs(img.Height * root.RasterizationScale - 2000) > 1)
                throw new Exception("Raster original size was not DPI-aware");
            var source = (Microsoft.UI.Xaml.Media.Imaging.BitmapImage)img.Source;
            if (source.DecodePixelWidth != 3000) throw new Exception("Zoom did not request original resolution");
            for (int i = 0; i < 8; i++) surface.Zoom(i % 2 == 0 ? 0.5 : 2);
            surface.Fit();
            record("PASS 3000px image original resolution, DPI sizing and rapid zoom");
        });
        await Check("Tall.svg", PreviewKind.Image, Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="10000"><rect width="100" height="10000" fill="blue"/></svg>"""),
            async (dialog, vm, _) =>
            {
                if (vm.State != PreviewState.Ready) throw new Exception("Tall SVG did not render");
                var surface = Descendants(dialog).OfType<PreviewImageSurface>().Single();
                surface.Fit();
                await Task.Delay(250);
                var scroll = Descendants(surface).OfType<ScrollViewer>().First(s => s.Content is Image);
                if (scroll.ScrollableHeight > 2) throw new Exception("Tall image did not fit below 10%");
                surface.Zoom(1);
                await Task.Delay(250);
                surface.Fit();
                record("PASS tall image fits below native 10% limit and restores original ratio");
            });
        record("ALL READING UI CHECKS PASSED");

        async Task Check(string name, PreviewKind kind, byte[] bytes,
            Func<ContentDialog, PreviewViewModel, Func<int>, Task> assertions)
        {
            int requests = 0;
            var loader = new PreviewContentLoader(kind, _ => Task.FromResult(new PreviewMetadata(bytes.Length, null)),
                _ => { requests++; return Task.FromResult<Stream>(new MemoryStream(bytes)); });
            var vm = new PreviewViewModel(name, loader);
            ContentDialog dialog = kind switch
            {
                PreviewKind.Text => new TextPreviewView(),
                PreviewKind.Markdown => new MarkdownPreviewView(),
                _ => new ImagePreviewView()
            };
            dialog.XamlRoot = root;
            dialog.DataContext = vm;
            var showing = dialog.ShowAsync().AsTask();
            try
            {
                for (int i = 0; i < 200 && vm.IsLoading; i++) await Task.Delay(50);
                await Task.Delay(150);
                await assertions(dialog, vm, () => requests);
            }
            finally { dialog.Hide(); await showing; await vm.CloseAsync(); }
            if (vm.State != PreviewState.Closed || loader.HasTextBytes) throw new Exception("Reading resources survived close");
        }
    }

    internal static async Task<byte[]> LargePngAsync()
    {
        using var memory = new MemoryStream();
        using var stream = memory.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[3000 * 2000 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)((i / 4) % 256);
            pixels[i + 1] = (byte)((i / 12000) % 256);
            pixels[i + 2] = 80;
            pixels[i + 3] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3000, 2000, 96, 96, pixels);
        await encoder.FlushAsync();
        return memory.ToArray();
    }

    // Native TextBox normalizes line separators; assert text/line fidelity separately from byte fidelity.
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
