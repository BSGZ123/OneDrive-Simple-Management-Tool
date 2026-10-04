using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IBookmarkResolver
    {
        Task<BookmarkLocation> ResolveAsync(Bookmark bookmark, CancellationToken token);
    }

    public sealed class BookmarkResolver(DriveConfigurationStore drives, IAccountAuthenticationService authentication,
        Func<IAccountAuthenticationService, string, GraphServiceClient> createClient = null) : IBookmarkResolver
    {
        private readonly Func<IAccountAuthenticationService, string, GraphServiceClient> _createClient =
            createClient ?? GraphAccountDriveResolver.CreateClient;

        public async Task<BookmarkLocation> ResolveAsync(Bookmark bookmark, CancellationToken token)
        {
            try
            {
                var configured = await drives.LoadAsync(token);
                var drive = configured.Data.SingleOrDefault(d => d.Provider.HomeAccountId == bookmark.AccountId && d.Provider.DriveId == bookmark.DriveId);
                if (drive == null) throw new BookmarkException("Bookmarks_AccountUnavailable");
                // Only the stored account can supply a token. Never fall back to the current/default account.
                using var client = _createClient(authentication, bookmark.AccountId);
                var item = await client.Drives[bookmark.DriveId].Items[bookmark.ItemId].GetAsync(
                    config => config.QueryParameters.Select = new[] { "id", "name", "file", "folder", "parentReference", "remoteItem", "deleted" }, token);
                if (item?.Deleted != null) throw new BookmarkException("Bookmarks_NotFound");
                if (item?.Id != bookmark.ItemId || string.IsNullOrWhiteSpace(item.Name) || item.RemoteItem != null ||
                    (item.ParentReference?.DriveId != null && item.ParentReference.DriveId != bookmark.DriveId) ||
                    (item.Folder == null && (item.File == null || string.IsNullOrWhiteSpace(item.ParentReference?.Id))))
                    throw new BookmarkException("Bookmarks_InvalidTarget");
                var updated = bookmark with
                {
                    Name = item.Name, IsFolder = item.Folder != null,
                    DriveName = string.IsNullOrWhiteSpace(drive.DisplayName) ? "Account_DefaultName".GetLocalized() : drive.DisplayName
                };
                return new(updated, updated.IsFolder ? item.Id : item.ParentReference.Id, updated.IsFolder ? null : item.Id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (BookmarkException) { throw; }
            catch (Exception exception)
            {
                throw new BookmarkException(exception switch
                {
                    ConfigurationException => "Bookmarks_AccountConfiguration",
                    AccountAuthenticationException { Failure: AuthenticationFailure.RequiresSignIn } => "Bookmarks_SignInRequired",
                    AccountAuthenticationException { Failure: AuthenticationFailure.Network } => "Bookmarks_Network",
                    ApiException { ResponseStatusCode: 401 } => "Bookmarks_SignInRequired",
                    ApiException { ResponseStatusCode: 403 } => "Bookmarks_AccessDenied",
                    ApiException { ResponseStatusCode: 404 or 410 } => "Bookmarks_NotFound",
                    ApiException { ResponseStatusCode: 429 } => "Bookmarks_Throttled",
                    HttpRequestException or OperationCanceledException => "Bookmarks_Network",
                    _ => "Bookmarks_OpenFailed"
                });
            }
        }
    }
}
