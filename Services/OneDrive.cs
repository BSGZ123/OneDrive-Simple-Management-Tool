using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Restore;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Services
{
    public partial class OneDrive
    {
        public OneDrive() : this(null, null) { }

        public OneDrive(string driveId, string homeAccountId)
            : this(driveId, homeAccountId, Ioc.Default.GetService<IAccountAuthenticationService>()) { }

        public OneDrive(string driveId, string homeAccountId, IAccountAuthenticationService authentication, IAccountDriveResolver resolver = null)
        {
            DriveId = driveId;
            HomeAccountId = homeAccountId;
            _authentication = authentication;
            if (authentication != null)
            {
                _session = new DriveAuthenticationSession(authentication, resolver ?? new GraphAccountDriveResolver(authentication));
                if (homeAccountId != null) graphClient = GraphAccountDriveResolver.CreateClient(authentication, homeAccountId);
            }
        }

        public async Task<DriveItemPage> GetFilePageAsync(string parentId, string keyword,
            string nextLink, CancellationToken cancellationToken)
        {
            if (!IsAuthenticated) await Login(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (nextLink != null) ValidateNextLink(nextLink);

            List<DriveItem> items;
            string continuation;
            if (keyword == null)
            {
                var request = graphClient.Drives[DriveId].Items[parentId].Children;
                var response = nextLink == null
                    ? await request.GetAsync(config => config.QueryParameters.Top = 200, cancellationToken)
                    : await request.WithUrl(nextLink).GetAsync(cancellationToken: cancellationToken);
                items = response?.Value;
                continuation = response?.OdataNextLink;
            }
            else
            {
                // Resolve the root ID so the item-scoped search cannot broaden to shared drives.
                string rootId = "root";
                if (nextLink == null)
                {
                    var root = await graphClient.Drives[DriveId].Root.GetAsync(cancellationToken: cancellationToken);
                    rootId = root?.Id;
                    if (string.IsNullOrWhiteSpace(rootId)) throw new InvalidDataException();
                }
                var request = graphClient.Drives[DriveId].Items[rootId].SearchWithQ(SearchQueryRules.ForGraph(keyword));
                var response = nextLink == null
                    ? await request.GetAsSearchWithQGetResponseAsync(config => config.QueryParameters.Top = 200, cancellationToken)
                    : await request.WithUrl(nextLink).GetAsSearchWithQGetResponseAsync(cancellationToken: cancellationToken);
                items = response?.Value;
                continuation = response?.OdataNextLink;
            }
            if (items == null || items.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || item.Name == null))
                throw new InvalidDataException();
            // Remote shortcuts/shared items cannot be operated on with this drive's ID.
            var localItems = items.Where(IsInCurrentDrive).ToList();
            return new DriveItemPage(localItems, string.IsNullOrEmpty(continuation) ? null : continuation, items.Count - localItems.Count);
        }

        private bool IsInCurrentDrive(DriveItem item) => item.RemoteItem == null &&
            (item.ParentReference?.DriveId == null || item.ParentReference.DriveId == DriveId);

        private void ValidateNextLink(string nextLink)
        {
            var service = new Uri(graphClient.RequestAdapter.BaseUrl.TrimEnd('/') + "/");
            if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri) || uri.Scheme != service.Scheme ||
                uri.Authority != service.Authority || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !uri.AbsolutePath.StartsWith(service.AbsolutePath + "drives/" + Uri.EscapeDataString(DriveId) + "/", StringComparison.Ordinal))
                throw new InvalidDataException();
        }

        public async Task<IReadOnlyList<DriveItem>> GetFolderPathAsync(string itemId, CancellationToken cancellationToken)
        {
            if (!IsAuthenticated) await Login(cancellationToken);
            var root = await graphClient.Drives[DriveId].Root.GetAsync(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(root?.Id)) throw new InvalidDataException();
            var path = new List<DriveItem>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (itemId != root.Id)
            {
                if (string.IsNullOrWhiteSpace(itemId) || !visited.Add(itemId)) throw new InvalidDataException();
                var item = await graphClient.Drives[DriveId].Items[itemId].GetAsync(cancellationToken: cancellationToken);
                if (item?.Id != itemId || item.Name == null || item.Folder == null || !IsInCurrentDrive(item))
                    throw new InvalidDataException();
                path.Add(item);
                itemId = item.ParentReference?.Id;
            }
            path.Reverse();
            return path;
        }

        public async Task<ThumbnailSetCollectionResponse> GetThumbNails(string itemId)
        {
            return await graphClient.Drives[DriveId].Items[itemId].Thumbnails.GetAsync();
        }

        public async Task<DriveItem> GetItem(string itemId, CancellationToken cancellationToken = default)
        {
            return await graphClient.Drives[DriveId].Items[itemId].GetAsync(cancellationToken: cancellationToken);
        }

        public async Task<DownloadSource> GetDownloadSourceAsync(string itemId, CancellationToken cancellationToken)
        {
            try
            {
                DriveItem item = await graphClient.Drives[DriveId].Items[itemId]
                    .GetAsync(cancellationToken: cancellationToken);
                if (item?.File == null || item.Size == null ||
                    !item.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out object url))
                    throw new DownloadFailureException(DownloadFailure.InvalidResponse);

                return new DownloadSource(url?.ToString(), item.Size.Value, item.CTag ?? item.ETag,
                    item.File.Hashes?.Sha256Hash, item.File.Hashes?.Sha1Hash);
            }
            catch (ApiException exception)
            {
                throw new DownloadFailureException(exception.ResponseStatusCode switch
                {
                    401 or 403 => DownloadFailure.AccessDenied,
                    404 => DownloadFailure.NotFound,
                    _ => DownloadFailure.Network
                }, exception);
            }
            catch (MsalException exception)
            {
                throw new DownloadFailureException(DownloadFailure.AccessDenied, exception);
            }
            catch (AccountAuthenticationException exception)
            {
                throw new DownloadFailureException(exception.Failure == AuthenticationFailure.Network
                    ? DownloadFailure.Network : DownloadFailure.AccessDenied, exception);
            }
        }

        public async Task<Stream> GetItemContent(string itemId, CancellationToken cancellationToken = default)
        {
            return await graphClient.Drives[DriveId].Items[itemId].Content.GetAsync(cancellationToken: cancellationToken);
        }

        public async Task<DriveItem> CreateFolder(string parentItemId, string folderName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestBody = new DriveItem
            {
                Name = folderName,
                Folder = new Folder { },
                AdditionalData = new Dictionary<string, object>
                {
                    {
                        "@microsoft.graph.conflictBehavior" , "rename"
                    },
                },
            };
            return await graphClient.Drives[DriveId].Items[parentItemId].Children.PostAsync(requestBody, cancellationToken: cancellationToken);
        }

        public async Task<DriveItem> RenameFile(string itemId, string newName)
        {
            DriveItem requestBody = new()
            {
                Name = newName,
            };
            return await graphClient.Drives[DriveId].Items[itemId].PatchAsync(requestBody);
        }

        // Progress reports uploaded bytes for both files and folders.
        public async Task UploadFileAsync(StorageFile file, string itemId, IProgress<long> progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Opening a stream has no cancellation overload. Await it and dispose it even if cancelled meanwhile.
            using Stream stream = await file.OpenStreamForReadAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length == 0)
            {
                // Empty files cannot use an upload session.
                DriveItem emptyItem = await graphClient.Drives[DriveId].Items[itemId].ItemWithPath(file.Name).Content.PutAsync(stream,
                    cancellationToken: cancellationToken);
                EnsureUploadedItem(emptyItem);
                progress?.Report(0);
                return;
            }

            var uploadSessionRequestBody = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
            {
                Item = new DriveItemUploadableProperties
                {
                    AdditionalData = new Dictionary<string, object>
                    {
                        { "@microsoft.graph.conflictBehavior", "replace" },
                    },
                },
            };
            UploadSession uploadSession = await graphClient
                .Drives[DriveId]
                .Items[itemId]
                .ItemWithPath(file.Name)
                .CreateUploadSession
                .PostAsync(uploadSessionRequestBody, cancellationToken: cancellationToken);

            if (string.IsNullOrWhiteSpace(uploadSession?.UploadUrl))
            {
                throw new InvalidOperationException("The server did not return an upload session.");
            }

            int maxChunkSize = 320 * 1024;
            // The session URL is preauthenticated. Let the SDK use its anonymous adapter.
            LargeFileUploadTask<DriveItem> fileUploadTask = new(uploadSession, stream, maxChunkSize);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uploadResult = await fileUploadTask.UploadAsync(progress == null ? null : new UploadSliceProgress(progress),
                    cancellationToken: cancellationToken);
                if (!uploadResult.UploadSucceeded)
                {
                    throw new InvalidOperationException("The server did not confirm the upload.");
                }

                // A confirmed success wins a concurrent cancellation; never delete a completed item.
                EnsureUploadedItem(uploadResult.ItemResponse);
                progress?.Report(stream.Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The transfer has exited. Cleanup needs its own token because the upload token is cancelled.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await fileUploadTask.DeleteSessionAsync(cleanup.Token);
                }
                catch (Exception)
                {
                    // Best effort: expired/unreachable sessions are cleaned up by OneDrive.
                    // Do not log the preauthenticated session URL or replace the cancellation result.
                }
                throw;
            }
        }

        private sealed class UploadSliceProgress(IProgress<long> progress) : IProgress<long>
        {
            public void Report(long lastByteOffset)
            {
                // Graph.Core 3.2.4 reports a zero-based last-byte offset, not a byte count.
                progress.Report(checked(lastByteOffset + 1));
            }
        }

        private static void EnsureUploadedItem(DriveItem item)
        {
            if (string.IsNullOrWhiteSpace(item?.Id))
            {
                throw new InvalidOperationException("The server did not return the uploaded item.");
            }
        }

        public async Task UploadFolderAsync(StorageFolder folder, string itemId, IProgress<long> progress = null,
            CancellationToken cancellationToken = default)
        {
            var tracker = new UploadProgressTracker(progress);
            await UploadFolderCoreAsync(folder, itemId, tracker, cancellationToken);
        }

        private async Task UploadFolderCoreAsync(StorageFolder folder, string itemId, UploadProgressTracker tracker,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await folder.GetFilesAsync().AsTask(cancellationToken);
            DriveItem cloudFolder = await CreateFolder(itemId, folder.Name, cancellationToken);
            if (string.IsNullOrWhiteSpace(cloudFolder?.Id))
            {
                throw new InvalidOperationException("The server did not return the created folder.");
            }

            // Stop scheduling on cancellation, but always join every operation already started.
            var uploadTasks = new List<Task>();
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested) break;
                uploadTasks.Add(UploadFileAsync(file, cloudFolder.Id, tracker.CreateFileProgress(), cancellationToken));
            }
            await Task.WhenAll(uploadTasks);

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StorageFolder> subfolders = await folder.GetFoldersAsync().AsTask(cancellationToken);
            var subfolderTasks = new List<Task>();
            foreach (var subfolder in subfolders)
            {
                if (cancellationToken.IsCancellationRequested) break;
                subfolderTasks.Add(UploadFolderCoreAsync(subfolder, cloudFolder.Id, tracker, cancellationToken));
            }
            await Task.WhenAll(subfolderTasks);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task<string> CreateLink(string itemId, DateTimeOffset? expirationDateTime = null, string password = null, string type = "view")
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("A file or folder ID is required.", nameof(itemId));
            }

            if (graphClient == null) await Login();
            if (string.IsNullOrWhiteSpace(DriveId))
            {
                throw new InvalidOperationException("No drive is available for sharing.");
            }

            Microsoft.Graph.Drives.Item.Items.Item.CreateLink.CreateLinkPostRequestBody requestBody = new()
            {
                Type = type,
                Password = password,
                Scope = "anonymous",
                RetainInheritedPermissions = true,
                ExpirationDateTime = expirationDateTime,
            };
            Permission result = await graphClient.Drives[DriveId].Items[itemId].CreateLink.PostAsync(requestBody);
            string link = result?.Link?.WebUrl;
            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The server did not return a valid sharing link.");
            }

            return link;
        }

        public async Task<string> GetDisplayName()
        {
            User user = await graphClient.Me.GetAsync();
            return user.DisplayName;
        }

        public async Task<Quota> GetStorageInfo()
        {
            if (!IsAuthenticated) await Login();
            Drive drive = await graphClient.Drives[DriveId].GetAsync();
            return drive.Quota;
        }

        public async Task ConvertFileFormat(string itemId, StorageFile file, string format = "pdf")
        {
            ArgumentNullException.ThrowIfNull(file);
            var content = graphClient.Drives[DriveId].Items[itemId].Content;
            // Graph SDK 5.80 does not expose the format query parameter on this builder.
            string url = content.ToGetRequestInformation().URI.GetLeftPart(UriPartial.Path);
            using Stream result = await content.WithUrl(url + "?format=" + Uri.EscapeDataString(format)).GetAsync();
            if (result == null) throw new InvalidDataException("The server returned no converted content.");
            using Stream fileStream = await file.OpenStreamForWriteAsync();
            fileStream.SetLength(0);
            if (result.CanSeek)
            {
                result.Seek(0, SeekOrigin.Begin);
            }
            await result.CopyToAsync(fileStream);
        }

        public async Task DeleteItem(string itemId)
        {
            await graphClient.Drives[DriveId].Items[itemId].DeleteAsync();
        }

        public async Task PermanentDeleteItem(string itemId)
        {
            await graphClient.Drives[DriveId].Items[itemId].PermanentDelete.PostAsync();
        }

        public async Task<DriveItem> RestoreItem(string itemId)
        {
            // 恢复默认原名
            RestorePostRequestBody requestBody = new()
            {
                ParentReference = new ItemReference
                {
                    Id = itemId,
                },
            };
            return await graphClient.Drives[DriveId].Items[itemId].Restore.PostAsync(requestBody);
        }

        public Task Login(CancellationToken cancellationToken = default) => AuthenticateAsync(false, cancellationToken);

        public Task SignInAsync(CancellationToken cancellationToken = default) => AuthenticateAsync(true, cancellationToken);

        private async Task AuthenticateAsync(bool interactive, CancellationToken token)
        {
            if (_authentication == null) throw new AccountAuthenticationException(AuthenticationFailure.RequiresSignIn);
            long attempt;
            string accountId, driveId;
            lock (_authenticationGate) { attempt = ++_authenticationAttempt; accountId = HomeAccountId; driveId = DriveId; }
            var identity = await _session.AuthenticateAsync(accountId, driveId, interactive, token);
            lock (_authenticationGate)
            {
                token.ThrowIfCancellationRequested();
                if (attempt != _authenticationAttempt) throw new OperationCanceledException();
                graphClient = GraphAccountDriveResolver.CreateClient(_authentication, identity.AccountId);
                HomeAccountId = identity.AccountId;
                DriveId = identity.DriveId;
                IsAuthenticated = true;
            }
        }

        private readonly IAccountAuthenticationService _authentication;
        private readonly DriveAuthenticationSession _session;
        private readonly object _authenticationGate = new();
        private long _authenticationAttempt;
        private GraphServiceClient graphClient;
        public string DriveId;
        public bool IsAuthenticated = false;
        public string HomeAccountId;
    }
}
