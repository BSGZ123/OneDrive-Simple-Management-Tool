using Downloader;
using Downloader.Exceptions;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class DownloadSessionOptions
    {
        public int MaxAttempts { get; init; } = 3;
        public int BlockTimeoutMilliseconds { get; init; } = 15000;
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
        public bool ReplaceExisting { get; init; } = true;
        public Action<string> ValidateDestination { get; init; }
        public Guid? StagingId { get; init; }
        public TransferPolicy Policy { get; init; } = new();
    }

    // Runtime downloads retain their package; durable tasks retain verified file checkpoints.
    public sealed partial class DownloadSession : IDownloadSession
    {
        private static readonly ConcurrentDictionary<string, DownloadSession> Destinations =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private readonly Func<CancellationToken, Task<DownloadSource>> _resolveSource;
        private readonly DownloadSessionOptions _options;
        private readonly string _stagingPath;
        private CancellationTokenSource _cancellation;
        private Task _activeTask = Task.CompletedTask;
        private Task _cancelTask = Task.CompletedTask;
        private DownloadPackage _package;
        private DownloadSource _source;
        private bool _ownsDestination;
        private bool _readyToSave;
        private bool _discardPreviousPartial;
        private TransferRecord _record;
        private Action<TransferRecord> _checkpoint;
        private DownloadSnapshot _snapshot = new(0, DownloadTaskState.Pending, 0, 0, 0, 0, DownloadFailure.None, false);

        public DownloadSession(string destinationPath, Func<CancellationToken, Task<DownloadSource>> resolveSource,
            DownloadSessionOptions options = null)
        {
            DestinationPath = Path.GetFullPath(destinationPath);
            _resolveSource = resolveSource;
            _options = options ?? new DownloadSessionOptions();
            _stagingPath = Path.Combine(Path.GetDirectoryName(DestinationPath), $".cloudflow-{_options.StagingId ?? Guid.NewGuid():N}.tmp");
            _discardPreviousPartial = _options.StagingId.HasValue;
            if (_options.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        }

        public string DestinationPath { get; }
        public TransferPolicy Policy => _options.Policy;
        public DownloadSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
        public event Action<DownloadSnapshot> Changed;
        public Action RequireCheckpoint { get; set; }

        public void AttachRecord(TransferRecord record, Action<TransferRecord> checkpoint, bool restore)
        {
            _record = record;
            _checkpoint = checkpoint;
            AttachResume(record.DownloadResume, resume =>
            {
                record.DownloadResume = resume;
                checkpoint(record);
                RequireCheckpoint?.Invoke();
            });
            if (restore)
            {
                var state = record.DownloadState is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Failed or DownloadTaskState.Paused
                    ? record.DownloadState : DownloadTaskState.Interrupted;
                _snapshot = new(1, state, state == DownloadTaskState.Completed ? record.Size : record.DownloadResume?.Bytes ?? 0, record.Size, 0, 0, record.DownloadFailure, false);
            }
        }

        public async Task StopForExitAsync()
        {
            bool preserveState = Snapshot.State is DownloadTaskState.Paused or DownloadTaskState.Pausing or DownloadTaskState.Failed;
            await PauseAsync().ConfigureAwait(false);
            if (Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
            if (_saveResume == null) await ResetPartialAsync().ConfigureAwait(false);
            ReleaseDestination();
            if (!preserveState) Update(state: DownloadTaskState.Interrupted, received: _resume?.Bytes ?? 0);
        }

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (!_activeTask.IsCompleted || _snapshot.State is not
                    (DownloadTaskState.Pending or DownloadTaskState.Paused or DownloadTaskState.Failed or DownloadTaskState.Interrupted))
                    return _activeTask;
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                Publish(DownloadTaskState.Preparing, failure: DownloadFailure.None);
                _activeTask = RunAsync(_cancellation.Token);
                return _activeTask;
            }
        }

        public Task PauseAsync()
        {
            lock (_gate)
            {
                if (_snapshot.State == DownloadTaskState.Pending) Publish(DownloadTaskState.Paused);
                if (_snapshot.State is DownloadTaskState.Preparing or DownloadTaskState.Downloading or DownloadTaskState.Retrying)
                {
                    Publish(DownloadTaskState.Pausing);
                    _cancellation.Cancel();
                }
                return _activeTask;
            }
        }

        public Task CancelAsync()
        {
            lock (_gate)
            {
                if (!_cancelTask.IsCompleted) return _cancelTask;
                _cancelTask = CancelCoreAsync();
                return _cancelTask;
            }
        }

        private async Task CancelCoreAsync()
        {
            Task running;
            lock (_gate)
            {
                // Saving has committed to finishing; a concurrent remove must wait for its outcome.
                if (_snapshot.State != DownloadTaskState.Finalizing &&
                    _snapshot.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled))
                {
                    Publish(DownloadTaskState.Cancelling);
                    _cancellation?.Cancel();
                }
                running = _activeTask;
            }
            await Task.Yield();
            await running.ConfigureAwait(false);
            lock (_gate)
            {
                if (_snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
                Publish(DownloadTaskState.Cancelling);
                // Reserve this operation before yielding, so StartAsync cannot race cleanup.
                _activeTask = CleanupCancelledAsync();
                running = _activeTask;
            }
            await running.ConfigureAwait(false);
        }

        private async Task RunAsync(CancellationToken token)
        {
            // StartAsync must install _activeTask before callbacks/stop requests can run.
            await Task.Yield();
            try
            {
                using var slot = await TransferScheduler.Shared.EnterAsync(token, Policy).ConfigureAwait(false);
                lock (_gate) RequireCheckpoint?.Invoke();
                if (!_ownsDestination)
                {
                    if (!Destinations.TryAdd(DestinationPath, this))
                        throw new DownloadFailureException(DownloadFailure.DestinationBusy);
                    _ownsDestination = true;
                }
                if (_discardPreviousPartial)
                {
                    _options.ValidateDestination?.Invoke(DestinationPath);
                    await ResetPartialAsync().ConfigureAwait(false);
                    _discardPreviousPartial = false;
                }

                for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        _options.ValidateDestination?.Invoke(DestinationPath);
                        Update(state: DownloadTaskState.Preparing, attempt: attempt);
                        DownloadSource source = await _resolveSource(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (source == null || source.Size < 0 || string.IsNullOrEmpty(source.Version) ||
                            !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                            throw new InvalidDataException("The remote download metadata is incomplete.");

                        if (_source != null && !SameContent(_source, source))
                        {
                            await ResetPartialAsync().ConfigureAwait(false);
                            Update(received: 0, restarted: true);
                        }
                        _source = source;
                        Update(total: source.Size);

                        if (await RecoverCommittedDownloadAsync(source, token).ConfigureAwait(false)) return;
                        if (_saveResume != null) _readyToSave = false; // Reverify the saved prefix even after a failed final move.

                        if (!_readyToSave)
                            await TransferAsync(source, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Update(received: source.Size);

                        // Recheck the item after transfer, before publishing a possibly changed file.
                        DownloadSource current = await _resolveSource(token).ConfigureAwait(false);
                        if (!SameContent(source, current))
                        {
                            await ResetPartialAsync().ConfigureAwait(false);
                            throw new DownloadFailureException(DownloadFailure.SourceChanged);
                        }
                        await ValidateFileAsync(source, token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            token.ThrowIfCancellationRequested();
                            Publish(DownloadTaskState.Finalizing);
                        }
                        // Staging is in the destination directory. A failed replacement leaves the
                        // existing destination intact and the complete staging file available to retry.
                        _options.ValidateDestination?.Invoke(DestinationPath);
                        File.Move(_stagingPath, DestinationPath, _options.ReplaceExisting);
                        _readyToSave = false;
                        await ReleasePackageAsync().ConfigureAwait(false);
                        ReleaseDestination();
                        Update(state: DownloadTaskState.Completed, received: source.Size);
                        SaveResume(null);
                        return;
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested &&
                        attempt < _options.MaxAttempts && IsRetryable(exception))
                    {
                        if (exception is DownloadRangeException)
                        {
                            await ResetPartialAsync().ConfigureAwait(false);
                            Update(received: 0, restarted: true);
                        }
                        Update(state: DownloadTaskState.Retrying);
                        await Task.Delay(_options.RetryDelay * Math.Pow(2, attempt - 1), token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                // The destination is already committed. A checkpoint error is surfaced by
                // the task manager and must never turn a completed file into a new transfer.
                if (Snapshot.State == DownloadTaskState.Completed) return;
                if (!token.IsCancellationRequested && exception is DownloadRangeException)
                {
                    try
                    {
                        await ResetPartialAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        exception = new DownloadFailureException(DownloadFailure.LocalStorage);
                    }
                }
                lock (_gate)
                {
                    if (token.IsCancellationRequested)
                    {
                        if (_snapshot.State != DownloadTaskState.Cancelling)
                            Publish(DownloadTaskState.Paused, received: _readyToSave ? _source.Size :
                                _package?.ReceivedBytesSize ?? _snapshot.ReceivedBytes);
                    }
                    else
                    {
                        Publish(DownloadTaskState.Failed, failure: Classify(exception));
                    }
                }
            }
        }

        private async Task TransferAsync(DownloadSource source, CancellationToken token)
        {
            if (_saveResume != null)
            {
                await TransferDurableAsync(source, token).ConfigureAwait(false);
                return;
            }
            if (source.Size == 0)
            {
                await File.WriteAllBytesAsync(_stagingPath, Array.Empty<byte>(), token).ConfigureAwait(false);
                _readyToSave = true;
                return;
            }
            using var client = new HttpClient(new DownloadResponseHandler(source.Size, Policy, _options.BlockTimeoutMilliseconds))
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
            var configuration = new DownloadConfiguration
            {
                ChunkCount = 1,
                ParallelDownload = false,
                MaxTryAgainOnFailure = 0, // One finite retry budget, owned by this session.
                BlockTimeout = int.MaxValue, // The response stream times network reads separately from throttling.
                ClearPackageOnCompletionWithFailure = false,
                EnableAutoResumeDownload = false,
                MaximumMemoryBufferBytes = 1024 * 1024,
                CustomHttpClientFactory = () => client
            };
            await using var downloader = new DownloadService(configuration);
            bool wasResuming = _package?.ReceivedBytesSize > 0;
            AsyncCompletedEventArgs completion = null;
            void OnCompleted(object sender, AsyncCompletedEventArgs args) => completion = args;
            void OnStarted(object sender, DownloadStartedEventArgs args)
            {
                if (wasResuming && !downloader.Package.IsSupportDownloadInRange)
                    Update(received: 0, restarted: true);
            }
            void OnProgress(object sender, DownloadProgressChangedEventArgs args)
            {
                lock (_gate)
                {
                    if (_snapshot.State == DownloadTaskState.Downloading && !token.IsCancellationRequested)
                        Publish(received: args.ReceivedBytesSize, speed: (long)args.BytesPerSecondSpeed);
                }
            }
            downloader.DownloadFileCompleted += OnCompleted;
            downloader.DownloadStarted += OnStarted;
            downloader.DownloadProgressChanged += OnProgress;
            try
            {
                token.ThrowIfCancellationRequested();
                Update(state: DownloadTaskState.Downloading);
                if (_package == null)
                    await downloader.DownloadFileTaskAsync(source.Url, _stagingPath, token).ConfigureAwait(false);
                else
                    await downloader.DownloadFileTaskAsync(_package, source.Url, token).ConfigureAwait(false);

                // Awaiting DownloadFileTaskAsync alone does not propagate every library failure.
                if (completion?.Error != null) throw completion.Error;
                if (completion == null || completion.Cancelled || downloader.Status != DownloadStatus.Completed)
                {
                    token.ThrowIfCancellationRequested();
                    throw new IOException("The download did not complete.");
                }
                _readyToSave = true;
            }
            finally
            {
                _package = downloader.Package;
                await _package.CloseAsync().ConfigureAwait(false);
                downloader.DownloadFileCompleted -= OnCompleted;
                downloader.DownloadStarted -= OnStarted;
                downloader.DownloadProgressChanged -= OnProgress;
            }
        }

        private async Task ValidateFileAsync(DownloadSource source, CancellationToken token)
        {
            if (new FileInfo(_stagingPath).Length != source.Size)
            {
                await ResetPartialAsync().ConfigureAwait(false);
                throw new InvalidDataException("The downloaded file length is incorrect.");
            }
            bool useSha256 = !string.IsNullOrEmpty(source.Sha256);
            string expected = useSha256 ? source.Sha256 : source.Sha1;
            if (string.IsNullOrEmpty(expected)) return;
            await using var stream = File.OpenRead(_stagingPath);
            byte[] hash = useSha256
                ? await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)
                : await SHA1.HashDataAsync(stream, token).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                // Close the read handle before deleting the invalid partial file on Windows.
                await stream.DisposeAsync().ConfigureAwait(false);
                await ResetPartialAsync().ConfigureAwait(false);
                throw new InvalidDataException("The downloaded file checksum is incorrect.");
            }
        }

        private static bool SameContent(DownloadSource first, DownloadSource second) => second != null &&
            first.Size == second.Size && !string.IsNullOrEmpty(first.Version) && first.Version == second.Version;

        private async Task ReleasePackageAsync()
        {
            if (_package != null) await _package.DisposeAsync().ConfigureAwait(false);
            _package = null;
        }

        private async Task ResetPartialAsync()
        {
            _options.ValidateDestination?.Invoke(DestinationPath);
            await ReleasePackageAsync().ConfigureAwait(false);
            File.Delete(_stagingPath + ".download");
            File.Delete(_stagingPath);
            _readyToSave = false;
            SaveResume(null);
        }

        private async Task CleanupCancelledAsync()
        {
            await Task.Yield();
            try
            {
                await ResetPartialAsync().ConfigureAwait(false);
                ReleaseDestination();
                Update(state: DownloadTaskState.Cancelled);
            }
            catch (Exception)
            {
                Update(state: DownloadTaskState.Failed, failure: DownloadFailure.LocalStorage);
            }
        }

        private void ReleaseDestination()
        {
            if (_ownsDestination) Destinations.TryRemove(DestinationPath, out _);
            _ownsDestination = false;
        }

        private static bool IsRetryable(Exception exception) => exception switch
        {
            DownloadFailureException failure => failure.Failure == DownloadFailure.Network,
            DownloadRangeException => true,
            HttpRequestException http => http.StatusCode == null || (int)http.StatusCode is 401 or 403 or 408 or 429 or >= 500,
            HttpIOException => true,
            OperationCanceledException => true,
            IncompleteDownloadException => true,
            _ => false
        };

        private static DownloadFailure Classify(Exception exception) => exception switch
        {
            DownloadFailureException failure => failure.Failure,
            HttpRequestException http when (int?)http.StatusCode is 401 or 403 => DownloadFailure.AccessDenied,
            HttpRequestException http when (int?)http.StatusCode == 404 => DownloadFailure.NotFound,
            DownloadRangeException or InvalidDataException => DownloadFailure.InvalidResponse,
            HttpRequestException or HttpIOException or OperationCanceledException or IncompleteDownloadException => DownloadFailure.Network,
            IOException or UnauthorizedAccessException => DownloadFailure.LocalStorage,
            _ => DownloadFailure.Unknown
        };

        private void Update(DownloadTaskState? state = null, long? received = null, long? total = null,
            int? attempt = null, DownloadFailure? failure = null, bool? restarted = null)
        {
            lock (_gate)
            {
                // Stop requests win over in-flight progress/state updates.
                if (_snapshot.State is DownloadTaskState.Pausing or DownloadTaskState.Cancelling &&
                    state is not (DownloadTaskState.Cancelled or DownloadTaskState.Failed)) return;
                Publish(state, received, total, attempt: attempt, failure: failure, restarted: restarted);
            }
        }

        private void Publish(DownloadTaskState? state = null, long? received = null, long? total = null,
            long? speed = null, int? attempt = null, DownloadFailure? failure = null, bool? restarted = null)
        {
            var previousState = _snapshot.State;
            var nextState = state ?? _snapshot.State;
            _snapshot = new(_snapshot.Revision + 1, nextState, received ?? _snapshot.ReceivedBytes,
                total ?? _snapshot.TotalBytes, nextState == DownloadTaskState.Downloading ? speed ?? 0 : 0,
                attempt ?? _snapshot.Attempt, failure ?? _snapshot.Failure, restarted ?? _snapshot.Restarted);
            if (_record != null && (previousState != nextState || total.HasValue))
            {
                _record.DownloadState = nextState;
                _record.DownloadFailure = _snapshot.Failure;
                _record.Size = _snapshot.TotalBytes;
                _checkpoint(_record);
            }
            Changed?.Invoke(_snapshot);
        }
    }
}
