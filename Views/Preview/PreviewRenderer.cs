using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal sealed class PreviewRenderer
    {
        private readonly Grid _host;
        private readonly PreviewViewModel _viewModel;
        private readonly List<Action> _cleanup = new();
        private static readonly HttpClient Images = new(new SocketsHttpHandler { UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public PreviewRenderer(Grid host, PreviewViewModel viewModel)
        {
            _host = host;
            _viewModel = viewModel;
        }

        public Task RenderAsync(PreviewContent content, CancellationToken token) => content.Kind switch
        {
            PreviewKind.Markdown => MarkdownAsync(content, token),
            PreviewKind.Text => TextAsync(content, token),
            PreviewKind.Image => ImageAsync(content, token),
            PreviewKind.Pdf => PdfAsync(content, token),
            PreviewKind.Media => MediaAsync(content, token),
            _ => throw new PreviewException(PreviewFailure.Unsupported)
        };

        public void Reset()
        {
            // Best effort per resource so one native teardown failure cannot skip the other resources.
            foreach (Action release in _cleanup)
            {
                try { release(); }
                catch (Exception) { System.Diagnostics.Debug.WriteLine("Preview resource teardown failed."); }
            }
            _cleanup.Clear();
            _host.Children.Clear();
        }

        private void Dispatch(CancellationToken token, Action action)
        {
            _host.DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested) action();
            });
        }

        private void Fail(TaskCompletionSource completion, CancellationToken token, PreviewException error)
        {
            if (token.IsCancellationRequested) return;
            if (!completion.TrySetException(error)) _viewModel.ReportFailure(error.Failure);
        }

        private async Task ImageAsync(PreviewContent content, CancellationToken token)
        {
            var image = new PreviewImageSurface(content.Bytes,
                Path.GetExtension(_viewModel.FileName).Equals(".svg", StringComparison.OrdinalIgnoreCase), _viewModel, token);
            _cleanup.Add(image.Dispose);
            _host.Children.Add(image);
            await image.LoadAsync();
        }

        private TextBox CreateTextBox(string text)
        {
            var box = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false, FontSize = _viewModel.ReadingFontSize,
                TextWrapping = _viewModel.WrapText ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"), Padding = new Thickness(12), Text = text
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, _viewModel.FileName);
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, _viewModel.WrapText ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            PropertyChangedEventHandler changed = (_, args) =>
            {
                if (args.PropertyName == nameof(PreviewViewModel.ReadingFontSize)) box.FontSize = _viewModel.ReadingFontSize;
                if (args.PropertyName == nameof(PreviewViewModel.WrapText))
                {
                    box.TextWrapping = _viewModel.WrapText ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    ScrollViewer.SetHorizontalScrollBarVisibility(box, _viewModel.WrapText ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
                }
            };
            _viewModel.PropertyChanged += changed;
            _cleanup.Add(() => { _viewModel.PropertyChanged -= changed; box.Text = string.Empty; });
            return box;
        }

        private Task TextAsync(PreviewContent content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _host.Children.Add(CreateTextBox(content.Text));
            return Task.CompletedTask;
        }

        private async Task MarkdownAsync(PreviewContent content, CancellationToken token)
        {
            if (PreviewTextLayout.UsePlainText(content.Text))
            {
                _viewModel.Notice = "Preview_PlainTextFallback".GetLocalized();
                await TextAsync(content, token);
                return;
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var markdown = new MarkdownTextBlock
            {
                Padding = new Thickness(12), IsTextSelectionEnabled = true, UseSyntaxHighlighting = true,
                WrapCodeBlock = false, TextWrapping = TextWrapping.Wrap,
                CodeFontFamily = new FontFamily("Consolas"), InlineCodeFontFamily = new FontFamily("Consolas"),
                FontSize = _viewModel.ReadingFontSize, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch,
                Header1FontSize = _viewModel.ReadingFontSize * 2, Header2FontSize = _viewModel.ReadingFontSize * 1.6,
                Header3FontSize = _viewModel.ReadingFontSize * 1.3, Header4FontSize = _viewModel.ReadingFontSize * 1.2,
                Header5FontSize = _viewModel.ReadingFontSize * 1.1, Header6FontSize = _viewModel.ReadingFontSize
            };
            markdown.SetRenderer<ReadingMarkdownRenderer>();
            var scroll = new ScrollViewer { Content = markdown, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            TextBox sourceView = null;
            PropertyChangedEventHandler readingChanged = (_, args) =>
            {
                if (token.IsCancellationRequested) return;
                if (args.PropertyName == nameof(PreviewViewModel.ReadingFontSize))
                {
                    double fraction = scroll.ScrollableHeight > 0 ? scroll.VerticalOffset / scroll.ScrollableHeight : 0;
                    markdown.FontSize = _viewModel.ReadingFontSize;
                    markdown.Header1FontSize = _viewModel.ReadingFontSize * 2;
                    markdown.Header2FontSize = _viewModel.ReadingFontSize * 1.6;
                    markdown.Header3FontSize = _viewModel.ReadingFontSize * 1.3;
                    markdown.Header4FontSize = _viewModel.ReadingFontSize * 1.2;
                    markdown.Header5FontSize = _viewModel.ReadingFontSize * 1.1;
                    markdown.Header6FontSize = _viewModel.ReadingFontSize;
                    Dispatch(token, () => scroll.ChangeView(null, fraction * scroll.ScrollableHeight, null, true));
                }
                if (args.PropertyName == nameof(PreviewViewModel.ShowSource))
                {
                    if (_viewModel.ShowSource && sourceView == null)
                    {
                        sourceView = CreateTextBox(content.Text);
                        _host.Children.Add(sourceView);
                    }
                    scroll.Visibility = _viewModel.ShowSource ? Visibility.Collapsed : Visibility.Visible;
                    if (sourceView != null) sourceView.Visibility = _viewModel.ShowSource ? Visibility.Visible : Visibility.Collapsed;
                }
            };
            _viewModel.PropertyChanged += readingChanged;
            _cleanup.Add(() => _viewModel.PropertyChanged -= readingChanged);
            // Serial bounded image loads keep Markdown subresources inside the preview lifecycle.
            var imageGate = new SemaphoreSlim(1, 1);
            int imageCount = 0;
            int remainingImageBytes = _viewModel.Options.MaxImageBytes;
            var imageCache = new Dictionary<string, ImageSource>();
            _cleanup.Add(imageCache.Clear);
            EventHandler<MarkdownRenderedEventArgs> rendered = (_, args) =>
            {
                if (token.IsCancellationRequested) return;
                if (args.Exception == null) completion.TrySetResult();
                else Fail(completion, token, new PreviewException(PreviewFailure.InvalidContent));
            };
            EventHandler<ImageResolvingEventArgs> resolving = async (_, args) =>
            {
                args.Handled = true;
                var deferral = args.GetDeferral();
                bool entered = false;
                try
                {
                    if (!Uri.TryCreate(args.Url, UriKind.Absolute, out Uri uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        throw new PreviewException(PreviewFailure.Unsupported);
                    await imageGate.WaitAsync(token);
                    entered = true;
                    if (imageCache.TryGetValue(uri.AbsoluteUri, out ImageSource cached))
                    {
                        args.Image = cached;
                        return;
                    }
                    if (++imageCount > 16) throw new PreviewException(PreviewFailure.TooLarge);
                    if (remainingImageBytes <= 0) throw new PreviewException(PreviewFailure.TooLarge);
                    using var response = await PreviewContentLoader.WithTimeoutAsync(
                        ct => Images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct),
                        _viewModel.Options.ReadTimeout, token);
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync(token);
                    byte[] bytes = await PreviewContentLoader.ReadLimitedAsync(stream,
                        Math.Min(4 * 1024 * 1024, remainingImageBytes), _viewModel.Options.ReadTimeout, token);
                    remainingImageBytes -= bytes.Length;
                    var image = await PreviewImageDecoder.DecodeAsync(bytes,
                        response.Content.Headers.ContentType?.MediaType == "image/svg+xml", token);
                    token.ThrowIfCancellationRequested();
                    args.Image = image;
                    imageCache[uri.AbsoluteUri] = image;
                }
                catch (Exception)
                {
                    if (!token.IsCancellationRequested) _viewModel.Notice = "Preview_ImagesIncomplete".GetLocalized();
                }
                finally
                {
                    if (entered) imageGate.Release();
                    deferral.Complete();
                }
            };
            markdown.MarkdownRendered += rendered;
            markdown.ImageResolving += resolving;
            _cleanup.Add(() =>
            {
                markdown.MarkdownRendered -= rendered;
                markdown.ImageResolving -= resolving;
                markdown.Text = string.Empty;
            });
            _host.Children.Add(scroll);
            readingChanged(this, new PropertyChangedEventArgs(nameof(PreviewViewModel.ShowSource)));
            token.ThrowIfCancellationRequested();
            markdown.Text = content.Text;
            await completion.Task.WaitAsync(token);
        }

        private async Task MediaAsync(PreviewContent content, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var element = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false };
            var player = new MediaPlayer { AutoPlay = false };
            var source = MediaSource.CreateFromUri(content.Uri);
            CancellationTokenSource buffering = null;
            TypedEventHandler<MediaPlayer, object> opened = (_, _) => Dispatch(token, () => completion.TrySetResult());
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, args) => Dispatch(token, () =>
            {
                var error = args.Error switch
                {
                    MediaPlayerError.NetworkError => new PreviewException(PreviewFailure.Network, true),
                    MediaPlayerError.SourceNotSupported => new PreviewException(PreviewFailure.Unsupported),
                    MediaPlayerError.DecodingError => new PreviewException(PreviewFailure.InvalidContent),
                    _ => new PreviewException(PreviewFailure.Unknown)
                };
                Fail(completion, token, error);
            });
            TypedEventHandler<MediaPlayer, object> bufferingStarted = (_, _) => Dispatch(token, () =>
            {
                _viewModel.IsBuffering = true;
                buffering?.Cancel();
                buffering?.Dispose();
                buffering = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = WatchBufferingAsync(buffering.Token, token);
            });
            TypedEventHandler<MediaPlayer, object> bufferingEnded = (_, _) => Dispatch(token, () =>
            {
                buffering?.Cancel();
                _viewModel.IsBuffering = false;
            });
            player.MediaOpened += opened;
            player.MediaFailed += failed;
            player.BufferingStarted += bufferingStarted;
            player.BufferingEnded += bufferingEnded;
            _cleanup.Add(() =>
            {
                buffering?.Cancel();
                buffering?.Dispose();
                player.MediaOpened -= opened;
                player.MediaFailed -= failed;
                player.BufferingStarted -= bufferingStarted;
                player.BufferingEnded -= bufferingEnded;
                try
                {
                    player.Pause();
                    player.Source = null;
                    element.SetMediaPlayer(null);
                }
                finally
                {
                    source.Dispose();
                    player.Dispose();
                }
            });
            token.ThrowIfCancellationRequested();
            element.SetMediaPlayer(player);
            _host.Children.Add(element);
            player.Source = source;
            await completion.Task.WaitAsync(token);

            async Task WatchBufferingAsync(CancellationToken bufferToken, CancellationToken attemptToken)
            {
                try
                {
                    await Task.Delay(_viewModel.Options.RenderTimeout, bufferToken);
                    if (!attemptToken.IsCancellationRequested) _viewModel.ReportFailure(PreviewFailure.Timeout);
                }
                catch (OperationCanceledException) { }
            }
        }

        private async Task PdfAsync(PreviewContent content, CancellationToken token)
        {
            await PreviewPdfProbe.ValidateAsync(content.Uri, _viewModel.Options, token);
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var web = new WebView2();
            _host.Children.Add(web);
            // A new control per attempt isolates initialization/navigation callbacks from previous retries.
            _cleanup.Add(web.Close);
            try { await web.EnsureCoreWebView2Async().AsTask().WaitAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { throw new PreviewException(PreviewFailure.RuntimeUnavailable, inner: exception); }
            token.ThrowIfCancellationRequested();
            var core = web.CoreWebView2;
            core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs;
            ulong? navigationId = null;
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs> starting = (_, args) =>
            {
                if (token.IsCancellationRequested) { args.Cancel = true; return; }
                if (navigationId == null) navigationId = args.NavigationId;
                else if (args.NavigationId != navigationId) args.Cancel = true;
            };
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> completed = (_, args) =>
            {
                if (token.IsCancellationRequested || args.NavigationId != navigationId) return;
                if (args.IsSuccess && args.HttpStatusCode < 400) completion.TrySetResult();
                else if (args.HttpStatusCode is 401 or 403)
                    Fail(completion, token, new PreviewException(PreviewFailure.AccessDenied, true));
                else if (args.HttpStatusCode == 404)
                    Fail(completion, token, new PreviewException(PreviewFailure.NotFound));
                else
                    Fail(completion, token, new PreviewException(PreviewFailure.Network, true));
            };
            TypedEventHandler<CoreWebView2, CoreWebView2ProcessFailedEventArgs> crashed = (_, _) =>
                Fail(completion, token, new PreviewException(PreviewFailure.RuntimeUnavailable));
            TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs> download = (_, args) =>
            {
                args.Cancel = true;
                args.Handled = true;
                if (!completion.Task.IsCompleted)
                    Fail(completion, token, new PreviewException(PreviewFailure.Unsupported));
                else if (!token.IsCancellationRequested)
                    _viewModel.Notice = "Preview_PdfDownload".GetLocalized();
            };
            TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> newWindow = (_, args) => args.Handled = true;
            core.NavigationStarting += starting;
            core.NavigationCompleted += completed;
            core.ProcessFailed += crashed;
            core.DownloadStarting += download;
            core.NewWindowRequested += newWindow;
            // Unsubscribe before Close, which may itself raise navigation events.
            _cleanup.Insert(0, () =>
            {
                core.NavigationStarting -= starting;
                core.NavigationCompleted -= completed;
                core.ProcessFailed -= crashed;
                core.DownloadStarting -= download;
                core.NewWindowRequested -= newWindow;
                core.Stop();
            });
            core.Navigate(content.Uri.AbsoluteUri);
            await completion.Task.WaitAsync(token);
        }
    }
}
