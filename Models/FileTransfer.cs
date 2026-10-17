using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum FileTransferKind { Move, Copy }
    public enum FileTransferConflict { KeepBoth, Skip }
    public enum FileTransferState { Pending, Working, Succeeded, Skipped, Failed, Waiting, Uncertain }
    public sealed record TransferItem(string Id, string Name, bool IsFolder, string ParentId,
        string ETag = null, bool IsRoot = false, bool IsUsable = true)
    {
        public override string ToString() => Name;
    }
    public sealed record CopyStatus(bool Completed, string ResourceId, double Percentage, string ErrorKey = null, bool NameConflict = false);

    public interface IFileTransferService
    {
        Task<TransferItem> GetRootAsync(CancellationToken token);
        Task<TransferItem> GetItemAsync(string id, CancellationToken token);
        Task<IReadOnlyList<TransferItem>> GetChildrenAsync(string parentId, CancellationToken token);
        Task MoveAsync(TransferItem source, string parentId, string name, CancellationToken token);
        Task<string> CopyAsync(TransferItem source, string parentId, string name, CancellationToken token);
        Task<CopyStatus> GetCopyStatusAsync(string monitorUrl, CancellationToken token);
    }

    public sealed class FileTransferException(string key, bool nameConflict = false, bool uncertain = false) : Exception(key)
    {
        public string Key { get; } = key;
        public bool NameConflict { get; } = nameConflict;
        public bool Uncertain { get; } = uncertain;
    }
}
