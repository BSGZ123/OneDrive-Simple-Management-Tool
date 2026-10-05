using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Views.Reader
{
    public sealed class ReaderWebViewController : IReaderHost
    {
        private readonly Grid _container;
        private readonly ApplicationDataPaths _paths;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<Task> _requests = new();
        private readonly List<IDisposable> _responseStreams = new();
        private WebView2 _web;
        private CoreWebView2 _core;
        private CoreWebView2Environment _environment;
        private ReaderResourceProvider _resources;
        private string _pageUrl, _session, _closeRequest;
        private readonly TaskCompletionSource _closeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong? _navigation;
        private Task _disposing;
        private bool _closed;
        public ReaderWebViewController(Grid container, ApplicationDataPaths paths) { _container = container; _paths = paths; }
        public event Action<string, string> MessageReceived;
        public event Action<string> Failed;
        internal CoreWebView2 Core => _core;
        internal int OutstandingRequests => _requests.Count;
        internal int ResponseStreamCount => _responseStreams.Count;

        public async Task InitializeAsync(ReaderLocalBook book, string session, CancellationToken token)
        {
            _session = session; _pageUrl = ReaderProtocol.PageUrl(session);
            _resources = new(book, session);
            await _resources.PrepareAsync(_paths.ApplicationDirectory, token);
            token.ThrowIfCancellationRequested();
            if (_closed) throw new OperationCanceledException();
            _web = new WebView2();
            _container.Children.Add(_web);
            try
            {
                _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _paths.ReaderRuntime, new CoreWebView2EnvironmentOptions());
                token.ThrowIfCancellationRequested();
                if (_closed) throw new OperationCanceledException();
                var options = _environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                options.ProfileName = "Reader";
                // WaitAsync does not cancel WebView2 initialization. The finally path
                // closes a control that completes after its session was cancelled.
                var web = _web;
                var initializing = web.EnsureCoreWebView2Async(_environment, options).AsTask();
                _ = CloseLateInitializationAsync(initializing, web, token);
                try { await initializing.WaitAsync(token); }
                finally { if (_closed || token.IsCancellationRequested) web.Close(); }
                token.ThrowIfCancellationRequested();
                if (_closed) throw new OperationCanceledException();
                _core = web.CoreWebView2;
                var settings = _core.Settings;
                settings.AreDevToolsEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.AreDefaultScriptDialogsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreHostObjectsAllowed = false;
                _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                _core.WebResourceRequested += ResourceRequested;
                _core.NavigationStarting += NavigationStarting;
                _core.NavigationCompleted += NavigationCompleted;
                _core.FrameNavigationStarting += FrameNavigationStarting;
                _core.WebMessageReceived += WebMessageReceived;
                _core.NewWindowRequested += NewWindowRequested;
                _core.DownloadStarting += DownloadStarting;
                _core.PermissionRequested += PermissionRequested;
                _core.ProcessFailed += ProcessFailed;
                _core.Navigate(_pageUrl);
            }
            catch (OperationCanceledException) { throw; }
            catch { throw new Models.ReaderException("RuntimeUnavailable"); }
        }
        public void Send(string json)
        {
            if (_closed || _core == null) return;
            var command = ReaderProtocol.Parse(_pageUrl, json, _session);
            if (command?.Type == "close") _closeRequest = command.RequestId;
            _core.PostWebMessageAsJson(json);
        }
        private async Task CloseLateInitializationAsync(Task initializing, WebView2 web, CancellationToken token)
        {
            try { await initializing; } catch { }
            if (_closed || token.IsCancellationRequested) try { web.Close(); } catch { }
        }
        private void WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_closed) return;
            string json;
            try { json = args.WebMessageAsJson; } catch { return; }
            var message = ReaderProtocol.Parse(args.Source, json, _session);
            if (message?.Type == "commandCompleted" && message.RequestId == _closeRequest && _closeRequest != null
                && ReaderProtocol.String(message.Payload, "command") == "close") _closeCompleted.TrySetResult();
            MessageReceived?.Invoke(args.Source, json);
        }
        private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (_closed || args.Uri != _pageUrl || _navigation.HasValue && args.NavigationId != _navigation.Value) { args.Cancel = true; return; }
            _navigation = args.NavigationId;
        }
        private void FrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = _closed || !ReaderProtocol.FrameUri(args.Uri);
        private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!_closed && args.NavigationId == _navigation && !args.IsSuccess) Failed?.Invoke("LoadFailed");
        }
        private void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
        private void DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; args.Handled = true; }
        private void PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
        private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) { if (!_closed) Failed?.Invoke("ProcessFailed"); }
        private async void ResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            Task operation = RespondAsync(args);
            _requests.Add(operation);
            try { await operation; }
            catch { if (!_closed) Failed?.Invoke("LoadFailed"); }
            finally
            {
                _requests.Remove(operation);
                try { deferral.Complete(); } catch { /* WebView may already be closed. */ }
            }
        }
        private async Task RespondAsync(CoreWebView2WebResourceRequestedEventArgs args)
        {
            ReaderResource response;
            try { response = await _resources.ResolveAsync(args.Request.Uri, args.Request.Method, _lifetime.Token); }
            catch { response = new(null, 403, "text/plain", 0); }
            if (_closed) return;
            var stream = response.Content?.AsRandomAccessStream();
            if (stream != null) _responseStreams.Add(stream);
            args.Response = _environment.CreateWebResourceResponse(stream, response.Status, response.Status == 200 ? "OK" : "Forbidden", ReaderResourceProvider.Headers(response));
        }
        public ValueTask DisposeAsync() => new(_disposing ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            if (_closeRequest != null) try { await _closeCompleted.Task.WaitAsync(TimeSpan.FromMilliseconds(350)); } catch (TimeoutException) { }
            _closed = true;
            _lifetime.Cancel();
            if (_core != null)
            {
                _core.WebResourceRequested -= ResourceRequested;
                _core.NavigationStarting -= NavigationStarting;
                _core.NavigationCompleted -= NavigationCompleted;
                _core.FrameNavigationStarting -= FrameNavigationStarting;
                _core.WebMessageReceived -= WebMessageReceived;
                _core.NewWindowRequested -= NewWindowRequested;
                _core.DownloadStarting -= DownloadStarting;
                _core.PermissionRequested -= PermissionRequested;
                _core.ProcessFailed -= ProcessFailed;
                try { _core.Stop(); } catch { }
            }
            if (_web != null)
            {
                try { _web.Close(); } catch { }
                _container.Children.Remove(_web);
            }
            // The control releases its responses before wrappers/file streams close.
            foreach (var stream in _responseStreams) stream.Dispose();
            _responseStreams.Clear();
            _resources?.Dispose();
            _core = null; _web = null;
            if (_requests.Count > 0) try { await Task.WhenAll(_requests).WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
        }
    }
}
