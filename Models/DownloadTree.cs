using System.Collections.Generic;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed record DownloadTreeItem(string Id, string Name, bool IsFolder, long Size,
        string ParentId = null, bool IsRoot = false);

    public sealed record DownloadTreePage(IReadOnlyList<DownloadTreeItem> Items, string NextLink);
}
