using System;
using System.Collections.Generic;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum TransferDirection { Download, Upload }
    public enum TransferEntryState { Pending, Working, Completed, Skipped, Failed }

    // Session URLs are capabilities: this entire record must stay in the DPAPI-protected store.
    public sealed record TransferRecord
    {
        public int Version { get; set; } = 1;
        public Guid Id { get; set; } = Guid.NewGuid();
        public TransferDirection Direction { get; set; }
        public string AccountId { get; set; }
        public string DriveId { get; set; }
        public string Name { get; set; }
        public string ItemId { get; set; }
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public DownloadTaskState DownloadState { get; set; }
        public DownloadFailure DownloadFailure { get; set; }
        public UploadTaskState UploadState { get; set; }
        public long Size { get; set; }
        public int Priority { get; set; } = 1;
        public int SpeedLimitKiB { get; set; }
        public DownloadResumeRecord DownloadResume { get; set; }
        public List<DownloadRootRecord> Roots { get; set; } = new();
        public List<DownloadNodeRecord> Downloads { get; set; } = new();
        public List<UploadEntryRecord> Uploads { get; set; } = new();
    }

    public sealed record DownloadRootRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Resolved { get; set; }
        public DownloadFailure Failure { get; set; }
    }

    public sealed record DownloadNodeRecord
    {
        public DownloadTreeItem Item { get; set; }
        public string Path { get; set; }
        public string ParentId { get; set; }
        public bool Enumerated { get; set; }
        public bool Completed { get; set; }
        public DownloadFailure Failure { get; set; }
        public Guid StagingId { get; set; } = Guid.NewGuid();
        public DownloadResumeRecord Resume { get; set; }
    }

    public sealed record UploadEntryRecord
    {
        public string Key { get; set; }
        public string ParentKey { get; set; }
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public bool Enumerated { get; set; }
        public string TargetId { get; set; }
        public string TargetName { get; set; }
        public long Size { get; set; }
        public TransferEntryState State { get; set; }
        public string ErrorKey { get; set; }
        public UploadResumeRecord Resume { get; set; }
    }

    // Immutable checkpoints keep the store's delta comparison independent of live progress.
    public sealed record DownloadResumeRecord(string Version, long Size, long Bytes, string PrefixSha256);
    public sealed record UploadResumeRecord(string Url, DateTimeOffset Expires, long Size, string SourceSha256,
        long Bytes = 0, string ConflictBehavior = "fail", bool CommitPending = false);
}
