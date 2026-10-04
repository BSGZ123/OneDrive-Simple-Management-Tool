using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IHomeDriveService
    {
        Task<IReadOnlyList<HomeDrive>> LoadDrivesAsync(CancellationToken token);
        Task<HomeQuota> GetQuotaAsync(HomeDrive drive, CancellationToken token);
    }

    public sealed class HomeDriveService(DriveConfigurationStore store, IAccountAuthenticationService authentication,
        Func<IAccountAuthenticationService, string, Microsoft.Graph.GraphServiceClient> createClient = null) : IHomeDriveService
    {
        private readonly Func<IAccountAuthenticationService, string, Microsoft.Graph.GraphServiceClient> _createClient =
            createClient ?? GraphAccountDriveResolver.CreateClient;
        public async Task<IReadOnlyList<HomeDrive>> LoadDrivesAsync(CancellationToken token)
        {
            try
            {
                var snapshot = await store.LoadAsync(token);
                return snapshot.Data.Select(d => new HomeDrive(d.Provider.HomeAccountId, d.Provider.DriveId,
                    string.IsNullOrWhiteSpace(d.DisplayName) ? "Account_DefaultName".GetLocalized() : d.DisplayName)).ToList();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { throw new HomeOverviewException(AccountConfigurationErrors.Key(exception)); }
        }

        public async Task<HomeQuota> GetQuotaAsync(HomeDrive drive, CancellationToken token)
        {
            try
            {
                // AccountTokenProvider only acquires silently for this exact account. No login UI on the home page.
                using var client = _createClient(authentication, drive.AccountId);
                var result = await client.Drives[drive.DriveId].GetAsync(
                    configuration => configuration.QueryParameters.Select = new[] { "id", "quota" }, token);
                if (result?.Id != drive.DriveId) throw new HomeOverviewException("Home_QuotaUnavailable");
                return new HomeQuota(result.Quota?.Total, result.Quota?.Used, result.Quota?.Remaining);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HomeOverviewException) { throw; }
            catch (Exception exception)
            {
                throw new HomeOverviewException(exception switch
                {
                    AccountAuthenticationException { Failure: AuthenticationFailure.RequiresSignIn } => "Home_SignInRequired",
                    AccountAuthenticationException { Failure: AuthenticationFailure.Network } => "Home_QuotaNetwork",
                    ApiException { ResponseStatusCode: 401 } => "Home_SignInRequired",
                    ApiException { ResponseStatusCode: 403 } => "Home_QuotaAccessDenied",
                    ApiException { ResponseStatusCode: 404 } => "Home_DriveNotFound",
                    ApiException { ResponseStatusCode: 429 } => "Home_QuotaThrottled",
                    HttpRequestException or OperationCanceledException => "Home_QuotaNetwork",
                    _ => "Home_QuotaFailed"
                });
            }
        }
    }
}
