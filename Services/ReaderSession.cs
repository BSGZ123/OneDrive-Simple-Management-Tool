using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IReaderHost : IAsyncDisposable
    {
        event Action<string, string> MessageReceived;
        event Action<string> Failed;
        Task InitializeAsync(ReaderLocalBook book, string session, CancellationToken token);
        void Send(string json);
    }

    // Called on the UI synchronization context; storage work itself never relies on it.
    public sealed class ReaderSession
    {
        private readonly IReaderStore _store;
        private readonly Func<IReaderHost> _hostFactory;
        private readonly Func<ReaderOpenRequest, IProgress<double>, CancellationToken, Task<ReaderLocalBook>> _openBook;
        private readonly SemaphoreSlim _writes = new(1, 1);
        private readonly List<ReaderTocEntry> _toc = new();
        private CancellationTokenSource _attempt;
        private IReaderHost _host;
        private ReaderLocalBook _book;
        private ReaderProgress _pendingProgress;
        private ReaderPreferences _pendingSettings;
        private ReaderLocation _resume;
        private ReaderLocation _restoreRequested;
        private string _contentVersion;
        private TaskCompletionSource _ready;
        private TaskCompletionSource _completed;
        private string _requestId, _command, _sessionId;
        private int _tocTotal = -1;
        private bool _opened;
        private Task _closing;
        public ReaderSession(IReaderStore store, Func<IReaderHost> hostFactory,
            Func<ReaderOpenRequest, IProgress<double>, CancellationToken, Task<ReaderLocalBook>> openBook = null)
        {
            _store = store; _hostFactory = hostFactory;
            _openBook = openBook ?? ((request, _, token) => ReaderLocalBook.OpenAsync(request.LocalPath, token));
        }
        public event Action Changed;
        public event Action<Uri> ExternalLinkRequested;
        public event Action BackRequested;
        public ReaderState State { get; private set; }
        public string ErrorCode { get; private set; }
        public string NoticeKey { get; private set; }
        public bool SaveFailed { get; private set; }
        public bool IsCommandBusy => _completed != null;
        public string Title { get; private set; }
        public double DownloadFraction { get; private set; }
        public bool FixedLayout { get; private set; }
        public ReaderLocation Location { get; private set; }
        public ReaderSettings Settings { get; private set; } = new();
        public IReadOnlyList<ReaderTocEntry> Toc => _toc;
        public string SessionId => _sessionId;
        public ReaderOpenRequest Request { get; private set; }
        private void Notify() => Changed?.Invoke();

        public async Task OpenAsync(ReaderOpenRequest request)
        {
            if (State is ReaderState.Preparing or ReaderState.Downloading or ReaderState.Loading or ReaderState.Restoring or ReaderState.Closing) return;
            await ReleaseAsync();
            await FlushAsync();
            if (Request != request) _resume = null;
            Request = request;
            _closing = null;
            _attempt = new();
            var attempt = _attempt;
            _sessionId = Guid.NewGuid().ToString("N");
            _toc.Clear(); _tocTotal = -1; _opened = false;
            ErrorCode = null; NoticeKey = null; Title = null; FixedLayout = false; Location = null;
            State = ReaderState.Preparing; Notify();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            timeout.CancelAfter(request.Identity == null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
            try
            {
                var progress = new Progress<double>(fraction =>
                {
                    if (attempt != _attempt || attempt.IsCancellationRequested || State is not (ReaderState.Preparing or ReaderState.Downloading)) return;
                    DownloadFraction = fraction; State = ReaderState.Downloading; Notify();
                });
                var book = await _openBook(request, progress, timeout.Token);
                if (attempt.IsCancellationRequested) { book.Dispose(); return; }
                _book = book;
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                ReaderProgress saved = null;
                try
                {
                    Settings = await _store.LoadSettingsAsync(timeout.Token);
                    saved = await _store.LoadProgressAsync(book.Identity, timeout.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch { SaveFailed = true; NoticeKey = "Reader_StorageFailure"; }
                timeout.Token.ThrowIfCancellationRequested();
                var restore = _resume != null ? book.ContentVersion == _contentVersion ? _resume : new(null, _resume.Fraction)
                    : saved?.ContentVersion == book.ContentVersion ? saved.Location : new(null, saved?.Location.Fraction);
                _restoreRequested = restore;
                _contentVersion = book.ContentVersion;
                if (_resume == null && saved != null && saved.ContentVersion != book.ContentVersion) NoticeKey = "Reader_Approximate";
                _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _host = _hostFactory();
                _host.MessageReceived += Receive;
                _host.Failed += OnHostFailed;
                State = ReaderState.Loading; Notify();
                await _host.InitializeAsync(book, _sessionId, timeout.Token);
                await _ready.Task.WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                State = ReaderState.Restoring; Notify();
                await SendAsync("openBook", new ReaderOpenPayload(Settings, restore), ReaderJsonContext.Default.ReaderOpenPayload, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (!_opened) throw new ReaderException("LoadFailed");
                State = ReaderState.Ready; _resume = null; Notify();
                _ = SaveLoopAsync(attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (attempt == _attempt && !attempt.IsCancellationRequested)
                    await FailAsync(error is ReaderException known ? known.Code : error is OperationCanceledException ? "TimedOut"
                        : error is ConfigurationException ? "CacheCorrupt" : error is System.IO.IOException or UnauthorizedAccessException ? "CacheStorage" : "LoadFailed");
            }
        }
        public async Task RetryAsync()
        {
            if (State != ReaderState.Failed || Request == null) return;
            _resume = Location;
            await OpenAsync(Request);
        }
        private async Task SendAsync<T>(string command, T payload, JsonTypeInfo<T> info, CancellationToken token)
        {
            if (_completed != null) throw new ReaderException("Busy");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completed = completion; _command = command; _requestId = Guid.NewGuid().ToString("N"); Notify();
            try
            {
                _host.Send(ReaderProtocol.Command(_sessionId, _requestId, command, payload, info));
                await completion.Task.WaitAsync(command == "openBook" ? TimeSpan.FromSeconds(22) : TimeSpan.FromSeconds(6), token);
            }
            finally
            {
                if (_completed == completion) { _completed = null; _command = null; _requestId = null; Notify(); }
            }
        }
        public Task TurnAsync(string direction) => direction is "next" or "prev" or "left" or "right"
            ? RunCommandAsync("turn", new ReaderTurnPayload(direction), ReaderJsonContext.Default.ReaderTurnPayload) : Task.CompletedTask;
        public Task NavigateAsync(string id) => _toc.Any(x => x.Id == id)
            ? RunCommandAsync("navigateToToc", new ReaderTocTargetPayload(id), ReaderJsonContext.Default.ReaderTocTargetPayload) : Task.CompletedTask;
        public async Task ApplySettingsAsync(ReaderSettings settings)
        {
            if (!ReaderProtocol.ValidSettings(settings) || State != ReaderState.Ready || IsCommandBusy) return;
            if (await RunCommandAsync("applySettings", settings, ReaderJsonContext.Default.ReaderSettings))
            {
                Settings = settings; _pendingSettings = new(settings, DateTimeOffset.UtcNow); Notify();
            }
        }
        private async Task<bool> RunCommandAsync<T>(string command, T payload, JsonTypeInfo<T> info)
        {
            if (State != ReaderState.Ready || IsCommandBusy) return false;
            try { await SendAsync(command, payload, info, _attempt.Token); return State == ReaderState.Ready; }
            catch (OperationCanceledException) { return false; }
            catch (Exception error) { await FailAsync(error is ReaderException known ? known.Code : "TimedOut"); return false; }
        }
        private void Receive(string source, string json)
        {
            if (State is ReaderState.Closing or ReaderState.Closed or ReaderState.Failed) return;
            var message = ReaderProtocol.Parse(source, json, _sessionId);
            if (message == null) return;
            try
            {
                var p = message.Payload;
                bool correlated = _requestId != null && message.RequestId == _requestId;
                switch (message.Type)
                {
                    case "ready" when State == ReaderState.Loading && message.RequestId == null && ReaderProtocol.Fields(p):
                        _ready.TrySetResult(); break;
                    case "tocChunk" when State == ReaderState.Restoring && _command == "openBook" && correlated:
                        AcceptToc(p); break;
                    case "opened" when State == ReaderState.Restoring && _command == "openBook" && correlated && !_opened:
                        if (!ReaderProtocol.Fields(p, "title", "fixedLayout", "direction", "restoredBy", "location", "elapsedMs", "entries", "declaredTotal", "compressedBytes")
                            || _tocTotal != _toc.Count || ReaderProtocol.String(p, "restoredBy") is not ("cfi" or "fraction" or "start")
                            || ReaderProtocol.String(p, "direction") is not ("ltr" or "rtl")
                            || !ReaderProtocol.Integer(p, "elapsedMs", 0, 60000, out _)
                            || !ReaderProtocol.Integer(p, "entries", 1, 10000, out _)
                            || !ReaderProtocol.Integer(p, "declaredTotal", 0, 256 * 1024 * 1024, out _)
                            || !ReaderProtocol.Integer(p, "compressedBytes", 1, (int)ReaderProtocol.MaximumBookBytes, out _)) throw new ReaderException("InvalidMessage");
                        string title = ReaderProtocol.String(p, "title");
                        if (title == null || title.Length > 512) throw new ReaderException("InvalidMessage");
                        Title = title; FixedLayout = p.GetProperty("fixedLayout").GetBoolean();
                        AcceptLocation(p.GetProperty("location")); _opened = true;
                        string restoredBy = ReaderProtocol.String(p, "restoredBy");
                        if (restoredBy == "fraction") NoticeKey ??= "Reader_Approximate";
                        if (restoredBy == "start" && _restoreRequested != null && !_restoreRequested.IsEmpty) NoticeKey ??= "Reader_Restarted";
                        break;
                    case "locationChanged" when State == ReaderState.Ready && message.RequestId == null:
                        AcceptLocation(p); break;
                    case "commandCompleted" when correlated && ReaderProtocol.Fields(p, "command") && ReaderProtocol.String(p, "command") == _command:
                        _completed.TrySetResult(); break;
                    case "readerError" when correlated && ReaderProtocol.Fields(p, "code"):
                        string code = ReaderProtocol.String(p, "code");
                        _completed.TrySetException(new ReaderException(code is "BookLimit" or "InvalidBook" or "UnsupportedEncryption" or "TimedOut" or "InvalidTarget" ? code : "LoadFailed")); break;
                    case "externalLinkRequested" when State == ReaderState.Ready && message.RequestId == null && ReaderProtocol.Fields(p, "url"):
                        if (ReaderProtocol.ExternalUri(ReaderProtocol.String(p, "url"), out var uri)) ExternalLinkRequested?.Invoke(uri); break;
                    case "hostCommand" when State == ReaderState.Ready && message.RequestId == null && ReaderProtocol.Fields(p, "command") && ReaderProtocol.String(p, "command") == "back":
                        BackRequested?.Invoke(); break;
                }
                Notify();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ReaderException)
            {
                _completed?.TrySetException(new ReaderException("InvalidMessage"));
            }
        }
        private void AcceptToc(JsonElement payload)
        {
            if (!ReaderProtocol.Fields(payload, "offset", "total", "nodes") || !ReaderProtocol.Integer(payload, "offset", 0, 1024, out int offset)
                || offset != _toc.Count || !ReaderProtocol.Integer(payload, "total", 0, 1024, out int total)
                || _tocTotal != -1 && _tocTotal != total) throw new ReaderException("InvalidMessage");
            var nodes = payload.GetProperty("nodes");
            if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 64 || offset + nodes.GetArrayLength() > total) throw new ReaderException("InvalidMessage");
            foreach (var node in nodes.EnumerateArray())
            {
                if (!ReaderProtocol.Fields(node, "id", "parentId", "label")) throw new ReaderException("InvalidMessage");
                string id = ReaderProtocol.String(node, "id"), parent = ReaderProtocol.String(node, "parentId"), label = ReaderProtocol.String(node, "label");
                if (id != "toc-" + _toc.Count || label == null || label.Length > 256
                    || !node.TryGetProperty("parentId", out var parentValue) || parentValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new ReaderException("InvalidMessage");
                var parentNode = parent == null ? null : _toc.Find(x => x.Id == parent);
                int depth = parentNode == null ? 0 : parentNode.Depth + 1;
                if (parent != null && parentNode == null || depth >= 16) throw new ReaderException("InvalidMessage");
                _toc.Add(new(id, parent, label, depth));
            }
            _tocTotal = total;
        }
        private void AcceptLocation(JsonElement payload)
        {
            var location = ReaderProtocol.Location(payload) ?? throw new ReaderException("InvalidMessage");
            Location = location;
            if (!location.IsEmpty && _book != null) _pendingProgress = new(_book.Identity, _book.ContentVersion, location, DateTimeOffset.UtcNow);
        }
        private async Task SaveLoopAsync(CancellationToken token)
        {
            try { while (!token.IsCancellationRequested) { await Task.Delay(1000, token); await FlushAsync(token); } }
            catch (OperationCanceledException) { }
        }
        public async Task FlushAsync(CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            // Bound the caller, while the underlying save keeps its serialization
            // lease until it actually settles. A timeout is never reported as saved.
            Task saving = FlushCoreAsync(timeout.Token);
            try { await saving.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { SaveFailed = true; Notify(); }
        }
        private async Task FlushCoreAsync(CancellationToken token)
        {
            bool entered = false;
            try
            {
                await _writes.WaitAsync(token); entered = true;
                var progress = _pendingProgress; var settings = _pendingSettings;
                if (progress != null) { await _store.SaveProgressAsync(progress, token); if (_pendingProgress == progress) _pendingProgress = null; }
                if (settings != null) { await _store.SaveSettingsAsync(settings, token); if (_pendingSettings == settings) _pendingSettings = null; }
                if (progress != null || settings != null) SaveFailed = false;
            }
            catch { SaveFailed = true; }
            finally { if (entered) _writes.Release(); Notify(); }
        }
        public async Task RecoverStorageAsync(bool backup)
        {
            try { await _store.RecoverAsync(backup, CancellationToken.None); SaveFailed = false; NoticeKey = null; await FlushAsync(); }
            catch { SaveFailed = true; }
            Notify();
        }
        private void OnHostFailed(string code)
        {
            _ready?.TrySetException(new ReaderException(code));
            _completed?.TrySetException(new ReaderException(code));
            if (State == ReaderState.Ready) _ = FailAsync(code);
        }
        private async Task FailAsync(string code)
        {
            if (State is ReaderState.Closing or ReaderState.Closed or ReaderState.Failed) return;
            ErrorCode = code; State = ReaderState.Failed; Notify();
            await ReleaseAsync(); await FlushAsync();
        }
        public Task CloseAsync() => _closing ??= CloseCoreAsync();
        private async Task CloseCoreAsync()
        {
            State = ReaderState.Closing; Notify();
            await ReleaseAsync(); await FlushAsync();
            State = ReaderState.Closed; Notify();
        }
        private async Task ReleaseAsync()
        {
            _attempt?.Cancel();
            var host = _host; var book = _book;
            _host = null; _book = null;
            if (host != null)
            {
                host.MessageReceived -= Receive; host.Failed -= OnHostFailed;
                try { host.Send(ReaderProtocol.Command(_sessionId, Guid.NewGuid().ToString("N"), "close", new ReaderEmptyPayload(), ReaderJsonContext.Default.ReaderEmptyPayload)); }
                catch { }
                try { await host.DisposeAsync(); } catch { }
            }
            book?.Dispose();
        }
    }
}
