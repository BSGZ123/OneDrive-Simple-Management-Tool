using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    // All state changes run on the owning UI context. Renderers must check their token before updating UI.
    public sealed class PreviewSession
    {
        private readonly Func<CancellationToken, Task<PreviewContent>> _load;
        private readonly Func<PreviewContent, CancellationToken, Task> _render;
        private readonly Action _reset;
        private readonly PreviewOptions _options;
        private CancellationTokenSource _lifetime;
        private CancellationTokenSource _attempt;
        private Task _active = Task.CompletedTask;
        private PreviewException _renderFailure;

        public PreviewSession(Func<CancellationToken, Task<PreviewContent>> load,
            Func<PreviewContent, CancellationToken, Task> render, Action reset, PreviewOptions options = null)
        {
            _load = load;
            _render = render;
            _reset = reset;
            _options = options ?? new();
        }

        public PreviewState State { get; private set; } = PreviewState.Preparing;
        public PreviewFailure Failure { get; private set; }
        public int Attempt { get; private set; }
        public event Action Changed;

        public Task StartAsync()
        {
            if (State == PreviewState.Closed || !_active.IsCompleted) return _active;
            ReleaseAttempt();
            _lifetime?.Dispose();
            _lifetime = new();
            SetState(PreviewState.Preparing);
            _active = RunAsync(_lifetime.Token);
            return _active;
        }

        private async Task RunAsync(CancellationToken token)
        {
            await Task.Yield();
            for (int attempt = 1; attempt <= 2 && !token.IsCancellationRequested; attempt++)
            {
                Attempt = attempt;
                try
                {
                    ReleaseAttempt();
                    _attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _renderFailure = null;
                    CancellationToken attemptToken = _attempt.Token;
                    SetState(PreviewState.Preparing);
                    PreviewContent content = await _load(attemptToken).WaitAsync(attemptToken);
                    attemptToken.ThrowIfCancellationRequested();
                    if (!content.IsEmpty)
                    {
                        SetState(PreviewState.Loading);
                        _attempt.CancelAfter(_options.RenderTimeout);
                        await _render(content, attemptToken).WaitAsync(attemptToken);
                        attemptToken.ThrowIfCancellationRequested();
                        _attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                    SetState(content.IsEmpty ? PreviewState.Empty : PreviewState.Ready);
                    return;
                }
                catch (Exception exception)
                {
                    if (token.IsCancellationRequested) return;
                    PreviewException error = _renderFailure ?? PreviewErrors.Classify(exception);
                    ReleaseAttempt();
                    if (attempt == 2 || !error.Recoverable)
                    {
                        SetState(PreviewState.Failed, error.Failure);
                        return;
                    }
                    SetState(PreviewState.Retrying);
                    try { await Task.Delay(_options.RetryDelay, token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public void ReportFailure(PreviewFailure failure)
        {
            // A native failure can arrive just after its opened event, before the awaited render resumes.
            if (State == PreviewState.Loading)
            {
                _renderFailure = new PreviewException(failure);
                _attempt?.Cancel();
                return;
            }
            // Failure after opening requires an explicit retry, avoiding surprise playback restarts.
            if (State != PreviewState.Ready) return;
            ReleaseAttempt();
            SetState(PreviewState.Failed, failure);
        }

        public async Task CloseAsync()
        {
            if (State != PreviewState.Closed)
            {
                SetState(PreviewState.Closed);
                _lifetime?.Cancel();
                ReleaseAttempt();
            }
            await _active;
            _lifetime?.Dispose();
        }

        private void ReleaseAttempt()
        {
            _attempt?.Cancel();
            _attempt?.Dispose();
            _attempt = null;
            _reset();
        }

        private void SetState(PreviewState state, PreviewFailure failure = PreviewFailure.None)
        {
            if (State == PreviewState.Closed) return;
            State = state;
            Failure = failure;
            Changed?.Invoke();
        }
    }
}
