using OneDrive_Simple_Management_Tool.Models;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IDownloadTreeSource
    {
        Task<DownloadTreeItem> GetDownloadItemAsync(string itemId, CancellationToken token);
        Task<DownloadTreePage> GetDownloadChildrenAsync(string parentId, string nextLink, CancellationToken token);
        Task<DownloadSource> GetDownloadSourceAsync(string itemId, CancellationToken cancellationToken);
    }
}
