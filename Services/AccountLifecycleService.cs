using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IAccountLifecycleEffects
    {
        Task StopAsync(AccountScope scope);
        Task UpdateSyncAsync(AccountScope scope, bool remove);
        Task RemoveBookmarksAsync(AccountScope scope);
        Task ClearCacheAsync(AccountScope scope);
    }

    public sealed class AccountLifecycleException(string stage, Exception inner) : Exception(stage, inner)
    {
        public string Stage { get; } = stage;
    }

    public sealed class AccountLifecycleService(AccountAccessState access, DriveConfigurationStore drives,
        IAccountAuthenticationService authentication, IAccountLifecycleEffects effects)
    {
        private readonly SemaphoreSlim _changes = new(1, 1);
        public AccountAccessState Access => access;

        public async Task ExecuteAsync(AccountScope scope, bool removeBookmarks = false)
        {
            await _changes.WaitAsync();
            try
            {
                AccountCleanup operation;
                try
                {
                    var configured = await drives.LoadAsync();
                    bool lastDrive = !configured.Data.Any(x => x.Provider.HomeAccountId == scope.AccountId && x.Provider.DriveId != scope.DriveId);
                    operation = await access.BeginAsync(scope, removeBookmarks, lastDrive);
                }
                catch (Exception error) { throw new AccountLifecycleException("Configuration", error); }
                await FinishAsync(operation);
            }
            finally { _changes.Release(); }
        }

        // Startup repair of an unreadable access journal. Every account with cached credentials, a drive or a sync
        // binding is journaled for sign-out; the next RetryPendingAsync removes credentials and pauses its sync.
        public async Task RepairAccessAsync(bool useBackup, IEnumerable<string> syncAccounts)
        {
            await _changes.WaitAsync();
            try
            {
                var accounts = new HashSet<string>(syncAccounts, StringComparer.Ordinal);
                accounts.UnionWith(await authentication.GetAccountIdsAsync(default));
                // An unreadable drive list has its own recovery; credentials for its accounts are covered above.
                try { accounts.UnionWith((await drives.LoadAsync()).Data.Select(x => x.Provider.HomeAccountId)); }
                catch (ConfigurationException) { }
                await access.RepairAsync(accounts, useBackup);
            }
            finally { _changes.Release(); }
        }

        public async Task RetryPendingAsync()
        {
            await _changes.WaitAsync();
            try
            {
                AccountLifecycleException failure = null;
                foreach (var operation in access.Pending)
                {
                    try { await FinishAsync(operation); }
                    catch (AccountLifecycleException error) { failure = error; }
                }
                if (failure != null) throw failure;
            }
            finally { _changes.Release(); }
        }

        private async Task FinishAsync(AccountCleanup operation)
        {
            string stage = "Stop";
            var scope = operation.Scope;
            try
            {
                await effects.StopAsync(scope);
                await access.DrainAsync(operation.ClearCredentials ? new AccountScope(scope.AccountId) : scope);
                stage = "Credentials";
                if (operation.ClearCredentials) await authentication.SignOutAsync(scope.AccountId, default);
                stage = "Sync";
                await effects.UpdateSyncAsync(scope, scope.DriveId != null);
                if (operation.RemoveBookmarks)
                {
                    stage = "Bookmarks";
                    await effects.RemoveBookmarksAsync(scope);
                }
                stage = "Cache";
                await effects.ClearCacheAsync(scope);
                stage = "Configuration";
                if (scope.DriveId != null) await drives.RemoveAsync(scope.AccountId, scope.DriveId);
                await access.CompleteAsync(operation);
            }
            catch (Exception error) { throw new AccountLifecycleException(stage, error); }
        }
    }
}
