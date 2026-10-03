using System;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum DownloadTaskState
    {
        Pending, Preparing, Downloading, Retrying, Pausing, Paused,
        Finalizing, Failed, Completed, Cancelling, Cancelled
    }

    public enum DownloadFailure
    {
        None, Network, AccessDenied, NotFound, InvalidResponse,
        SourceChanged, LocalStorage, DestinationBusy, Unknown
    }

    // Version is the Graph cTag (or eTag fallback), never the expiring download URL.
    public sealed record DownloadSource(string Url, long Size, string Version,
        string Sha256 = null, string Sha1 = null);

    public sealed record DownloadSnapshot(long Revision, DownloadTaskState State,
        long ReceivedBytes, long TotalBytes, long BytesPerSecond, int Attempt,
        DownloadFailure Failure, bool Restarted);

    public sealed class DownloadFailureException : Exception
    {
        public DownloadFailureException(DownloadFailure failure, Exception inner = null)
            : base(failure.ToString(), inner) => Failure = failure;

        public DownloadFailure Failure { get; }
    }
}
