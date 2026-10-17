using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Identity.Client;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public partial class OneDrive : IDownloadTreeSource
    {
        string IDownloadTreeSource.AccountId => HomeAccountId;
        string IDownloadTreeSource.DriveId => DriveId;
        public async Task<DownloadTreeItem> GetDownloadItemAsync(string itemId, CancellationToken token)
        {
            try
            {
                if (!IsAuthenticated) await Login(token);
                var item = await GetItem(itemId, token);
                if (item?.Id != itemId) throw new InvalidDataException();
                return ToDownloadItem(item);
            }
            catch (ApiException exception) { throw DownloadTreeFailure(exception); }
            catch (MsalException exception) { throw new DownloadFailureException(DownloadFailure.AccessDenied, exception); }
            catch (AccountAuthenticationException exception) { throw DownloadAuthenticationFailure(exception); }
        }

        public async Task<DownloadTreePage> GetDownloadChildrenAsync(string parentId, string nextLink, CancellationToken token)
        {
            if (nextLink != null)
            {
                ValidateNextLink(nextLink);
                string path = new Uri(graphClient.RequestAdapter.BaseUrl.TrimEnd('/') + "/drives/" +
                    Uri.EscapeDataString(DriveId) + "/items/" + Uri.EscapeDataString(parentId) + "/children").AbsolutePath;
                if (new Uri(nextLink).AbsolutePath != path) throw new InvalidDataException();
            }
            try
            {
                if (!IsAuthenticated) await Login(token);
                var page = await GetFilePageAsync(parentId, null, nextLink, token);
                // Never report a complete folder after silently omitting shared shortcuts.
                if (page.ExcludedCount > 0) throw new DownloadFailureException(DownloadFailure.UnsupportedItem);
                var items = page.Items.Select(ToDownloadItem).ToArray();
                if (items.Any(item => item.ParentId != null && item.ParentId != parentId)) throw new InvalidDataException();
                return new DownloadTreePage(items, page.NextLink);
            }
            catch (ApiException exception) { throw DownloadTreeFailure(exception); }
            catch (MsalException exception) { throw new DownloadFailureException(DownloadFailure.AccessDenied, exception); }
            catch (AccountAuthenticationException exception) { throw DownloadAuthenticationFailure(exception); }
        }

        private DownloadTreeItem ToDownloadItem(DriveItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Deleted != null || !IsInCurrentDrive(item) || (item.File == null) == (item.Folder == null))
                throw new DownloadFailureException(DownloadFailure.UnsupportedItem);
            if ((item.Root != null && item.Folder == null) || (item.Root == null && string.IsNullOrWhiteSpace(item.ParentReference?.Id)))
                throw new InvalidDataException();
            if (item.Folder == null && (item.Size == null || item.Size < 0)) throw new InvalidDataException();
            return new DownloadTreeItem(item.Id, item.Name, item.Folder != null, item.Size ?? 0,
                item.ParentReference?.Id, item.Root != null);
        }

        private static DownloadFailureException DownloadTreeFailure(ApiException exception) => new(exception.ResponseStatusCode switch
        {
            401 or 403 => DownloadFailure.AccessDenied,
            404 => DownloadFailure.NotFound,
            _ => DownloadFailure.Network
        }, exception);

        private static DownloadFailureException DownloadAuthenticationFailure(AccountAuthenticationException exception) => new(
            exception.Failure == AuthenticationFailure.Network ? DownloadFailure.Network : DownloadFailure.AccessDenied, exception);
    }
}
