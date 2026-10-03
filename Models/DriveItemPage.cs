using Microsoft.Graph.Models;
using System.Collections.Generic;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed record DriveItemPage(IReadOnlyList<DriveItem> Items, string NextLink, int ExcludedCount);
}
