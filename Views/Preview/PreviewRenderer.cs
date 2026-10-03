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
            ImageSource source = await PreviewImageDecoder.DecodeAsync(content.Bytes,
                Path.GetExtension(_viewModel.FileName).Equals(".svg", StringComparison.OrdinalIgnoreCase), token);
            token.ThrowIfCancellationRequested();
            var image = new Image { Source = source, Stretch = Stretch.Uniform };
            ExceptionRoutedEventHandler failed = (_, _) =>
            {
                if (!token.IsCancellationRequested) _viewModel.ReportFailure(PreviewFailure.InvalidContent);
            };
            image.ImageFailed += failed;
            _cleanup.Add(() => { image.ImageFailed -= failed; image.Source = null; });
            _host.Children.Add(image);
        }

        private async Task MarkdownAsync(PreviewContent content, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var markdown = new MarkdownTextBlock { Padding = new Thickness(12) };
            // Serial bounded image loads keep Markdown subresources inside the preview lifecycle.
            var imageGate = new SemaphoreSlim(1, 1);
            int imageCount = 0;
            int remainingImageBytes = _viewModel.Options.MaxImageBytes;
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
                    if (++imageCount > 16 || !Uri.TryCreate(args.Url, UriKind.Absolute, out Uri uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        throw new PreviewException(PreviewFailure.Unsupported);
                    await imageGate.WaitAsync(token);
                    entered = true;
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
            _host.Children.Add(new ScrollViewer { Content = markdown, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
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
                Fail(completion, token, new PreviewException(PreviewFailure.Unsupported));
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
