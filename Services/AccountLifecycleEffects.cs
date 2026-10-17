using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class AccountLifecycleEffects(TaskManagerViewModel transfers, FolderSyncService sync,
        IBookmarkStore bookmarks, ReaderWorkspace reader) : IAccountLifecycleEffects
    {
        public Task StopAsync(AccountScope scope) => Task.WhenAll(
            transfers.StopAccountAsync(scope.AccountId, scope.DriveId), reader.CloseScopeAsync(scope.AccountId, scope.DriveId));
        public Task UpdateSyncAsync(AccountScope scope, bool remove) => sync.UpdateAccountAsync(scope.AccountId, scope.DriveId, remove);
        public async Task RemoveBookmarksAsync(AccountScope scope)
        {
            foreach (var bookmark in (await bookmarks.LoadAsync()).Where(x => scope.Contains(x.AccountId, x.DriveId)))
                await bookmarks.RemoveAsync(bookmark);
        }
        public async Task ClearCacheAsync(AccountScope scope)
        {
            if (await reader.ClearScopeCacheAsync(scope.AccountId, scope.DriveId) != 0) throw new IOException();
        }
    }
}
