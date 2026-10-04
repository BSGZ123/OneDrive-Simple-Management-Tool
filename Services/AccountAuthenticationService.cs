using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public enum AuthenticationFailure { RequiresSignIn, AccountMismatch, Network, Failed }

    public sealed class AccountAuthenticationException(AuthenticationFailure failure) : Exception(failure.ToString())
    {
        public AuthenticationFailure Failure { get; } = failure;
    }

    // Deliberately not a record: generated ToString must never print credentials.
    public sealed class AccountToken(string accountId, string accessToken)
    {
        public string AccountId { get; } = accountId;
        public string AccessToken { get; } = accessToken;
        public override string ToString() => nameof(AccountToken);
    }

    public interface IAccountAuthenticationService
    {
        Task<AccountToken> AcquireSilentAsync(string accountId, CancellationToken token);
        Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token);
    }

    public sealed class MsalAccountAuthenticationService(IPublicClientApplication app) : IAccountAuthenticationService
    {
        private static readonly string[] Scopes = { "User.Read", "Files.ReadWrite.All" };
        private readonly SemaphoreSlim _interactive = new(1, 1);

        public async Task<AccountToken> AcquireSilentAsync(string accountId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(accountId)) throw new AccountAuthenticationException(AuthenticationFailure.RequiresSignIn);
            var account = (await app.GetAccountsAsync().ConfigureAwait(false))
                .SingleOrDefault(a => string.Equals(a.HomeAccountId.Identifier, accountId, StringComparison.Ordinal));
            token.ThrowIfCancellationRequested();
            if (account == null) throw new AccountAuthenticationException(AuthenticationFailure.RequiresSignIn);
            try
            {
                return Validate(await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(token).ConfigureAwait(false), accountId, token);
            }
            catch (MsalException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (MsalUiRequiredException) { throw new AccountAuthenticationException(AuthenticationFailure.RequiresSignIn); }
            catch (MsalServiceException exception) when (exception.StatusCode == 0 || exception.StatusCode == 429 || exception.StatusCode >= 500)
            { throw new AccountAuthenticationException(AuthenticationFailure.Network); }
            catch (MsalException) { throw new AccountAuthenticationException(AuthenticationFailure.Failed); }
        }

        public async Task<AccountToken> AcquireInteractiveAsync(string expectedAccountId, CancellationToken token)
        {
            await _interactive.WaitAsync(token);
            try
            {
                // Only a foreground user action calls this method. Token callbacks are always silent.
                var result = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync(token);
                return Validate(result, expectedAccountId, token);
            }
            catch (MsalClientException exception) when (exception.ErrorCode == "authentication_canceled")
            {
                throw new OperationCanceledException();
            }
            catch (MsalException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (MsalServiceException exception) when (exception.StatusCode == 0 || exception.StatusCode == 429 || exception.StatusCode >= 500)
            { throw new AccountAuthenticationException(AuthenticationFailure.Network); }
            catch (MsalException) { throw new AccountAuthenticationException(AuthenticationFailure.Failed); }
            finally { _interactive.Release(); }
        }

        private static AccountToken Validate(AuthenticationResult result, string expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string account = result?.Account?.HomeAccountId?.Identifier;
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(result.AccessToken))
                throw new AccountAuthenticationException(AuthenticationFailure.Failed);
            if (expected != null && !string.Equals(account, expected, StringComparison.Ordinal))
                throw new AccountAuthenticationException(AuthenticationFailure.AccountMismatch);
            return new AccountToken(account, result.AccessToken);
        }
    }

    public sealed class AccountTokenProvider(IAccountAuthenticationService authentication, string accountId) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new(new[] { "graph.microsoft.com" });

        public async Task<string> GetAuthorizationTokenAsync(Uri uri,
            Dictionary<string, object> additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || !AllowedHostsValidator.IsUrlHostValid(uri))
                throw new AccountAuthenticationException(AuthenticationFailure.Failed);
            var result = await authentication.AcquireSilentAsync(accountId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result?.AccountId != accountId || string.IsNullOrWhiteSpace(result.AccessToken))
                throw new AccountAuthenticationException(AuthenticationFailure.AccountMismatch);
            return result.AccessToken;
        }
    }

    public interface IAccountDriveResolver
    {
        Task<string> ResolveAsync(string accountId, string existingDriveId, CancellationToken token);
    }

    public sealed class GraphAccountDriveResolver(IAccountAuthenticationService authentication) : IAccountDriveResolver
    {
        public async Task<string> ResolveAsync(string accountId, string existingDriveId, CancellationToken token)
        {
            using var client = CreateClient(authentication, accountId);
            var drive = existingDriveId == null
                ? await client.Me.Drive.GetAsync(cancellationToken: token)
                : await client.Drives[existingDriveId].GetAsync(cancellationToken: token);
            if (string.IsNullOrWhiteSpace(drive?.Id) || (existingDriveId != null && drive.Id != existingDriveId))
                throw new AccountAuthenticationException(AuthenticationFailure.Failed);
            return drive.Id;
        }

        public static GraphServiceClient CreateClient(IAccountAuthenticationService authentication, string accountId) =>
            new(new BaseBearerTokenAuthenticationProvider(new AccountTokenProvider(authentication, accountId)));
    }

    // One instance per drive, so a late authentication attempt cannot replace a newer result.
    public sealed class DriveAuthenticationSession(IAccountAuthenticationService authentication, IAccountDriveResolver resolver)
    {
        private long _attempt;
        public async Task<(string AccountId, string DriveId)> AuthenticateAsync(string accountId, string driveId,
            bool interactive, CancellationToken token)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SafeDiagnostics.Current.Record(DiagnosticEvent.Authentication, DiagnosticLevel.Debug, status: 0);
            long attempt = Interlocked.Increment(ref _attempt);
            var result = interactive
                ? await authentication.AcquireInteractiveAsync(accountId, token)
                : await authentication.AcquireSilentAsync(accountId, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result?.AccountId) || string.IsNullOrWhiteSpace(result.AccessToken))
                throw new AccountAuthenticationException(AuthenticationFailure.Failed);
            if (accountId != null && result.AccountId != accountId)
                throw new AccountAuthenticationException(AuthenticationFailure.AccountMismatch);
            string resolved;
            try { resolved = await resolver.ResolveAsync(result.AccountId, driveId, token); }
            catch (System.Net.Http.HttpRequestException) { throw new AccountAuthenticationException(AuthenticationFailure.Network); }
            token.ThrowIfCancellationRequested();
            if (attempt != Volatile.Read(ref _attempt)) throw new OperationCanceledException();
            if (string.IsNullOrWhiteSpace(resolved) || (driveId != null && resolved != driveId))
                throw new AccountAuthenticationException(AuthenticationFailure.Failed);
            SafeDiagnostics.Current.Record(DiagnosticEvent.Authentication, DiagnosticLevel.Info, status: 1,
                elapsedMilliseconds: (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return (result.AccountId, resolved);
        }
    }
}
