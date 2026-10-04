using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed class FolderSyncBinding
    {
        [JsonIgnore]
        public long StorageRevision { get; set; }
        [JsonRequired]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonRequired]
        public string LocalPath { get; set; }
        [JsonRequired]
        public string FolderName { get; set; }
        [JsonRequired]
        public string AccountId { get; set; }
        [JsonRequired]
        public string DriveId { get; set; }
        public string DriveName { get; set; }
        [JsonRequired]
        public string RemoteFolderId { get; set; }
        [JsonRequired]
        public string RemotePath { get; set; }
        [JsonRequired]
        public List<string> RemoteAncestorIds { get; set; } = new();
        [JsonRequired]
        public bool Enabled { get; set; } = true;
        public DateTimeOffset? LastSuccess { get; set; }
        [JsonRequired]
        public Dictionary<string, FolderSyncStamp> Files { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed record FolderSyncStamp(bool IsFolder, string Hash, long Length);
    public sealed record FolderSyncItem(string Path, bool IsFolder);
    public sealed record FolderSyncIssue(string Path, string MessageKey);
    public sealed record FolderSyncProgress(string StateKey, int Completed, int Total, string CurrentPath, int Percent = 0);
    public sealed record FolderSyncRemoteFolder(string Id, string Name);

    public sealed class FolderSyncResult
    {
        public int Uploaded { get; set; }
        public List<FolderSyncIssue> Issues { get; } = new();
    }

    public sealed class FolderSyncException : Exception
    {
        public FolderSyncException(string key) : base(key) { }
    }

    [JsonSerializable(typeof(FolderSyncBinding))]
    internal partial class FolderSyncJsonContext : JsonSerializerContext { }
}
