﻿using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Restore;
using Microsoft.Graph.Drives.Item.Items.Item.SearchWithQ;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions.Authentication;
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
    public class OneDrive
    {
        public OneDrive()
        {
            PublicClientApp = Ioc.Default.GetService<IPublicClientApplication>();
        }

        public OneDrive(string driveId, string homeAccountId)
        {
            DriveId = driveId;
            HomeAccountId = homeAccountId;
            PublicClientApp = Ioc.Default.GetService<IPublicClientApplication>();
        }

        public async Task<DriveItemCollectionResponse> GetFiles(string parentId = "Root")
        {
            if (!IsAuthenticated) await Login();
            return await graphClient.Drives[DriveId].Items[parentId].Children.GetAsync();
        }

        public async Task<ThumbnailSetCollectionResponse> GetThumbNails(string itemId)
        {
            return await graphClient.Drives[DriveId].Items[itemId].Thumbnails.GetAsync();
        }

        public async Task<DriveItem> GetItem(string itemId)
        {
            return await graphClient.Drives[DriveId].Items[itemId].GetAsync();
        }

        public async Task<Stream> GetItemContent(string itemId)
        {
            return await graphClient.Drives[DriveId].Items[itemId].Content.GetAsync();
        }

        public async Task<DriveItem> CreateFolder(string parentItemId, string folderName)
        {
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
            return await graphClient.Drives[DriveId].Items[parentItemId].Children.PostAsync(requestBody);
        }

        public async Task<DriveItem> RenameFile(string itemId, string newName)
        {
            DriveItem requestBody = new()
            {
                Name = newName,
            };
            return await graphClient.Drives[DriveId].Items[itemId].PatchAsync(requestBody);
        }

        //这里通过SDK上传是有问题的，估摸着还得用Api上传
        public async Task UploadFileAsync(StorageFile file, string itemId, IProgress<long> progress = null)
        {
            using Stream stream = await file.OpenStreamForReadAsync();
            if ((await file.GetBasicPropertiesAsync()).Size == 0)
            {
                // 上传一个空文件
                await graphClient.Drives[DriveId].Items[itemId].ItemWithPath(file.Name).Content.PutAsync(new MemoryStream());
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
                .PostAsync(uploadSessionRequestBody);

            int maxChunckSize = 320 * 1024;
            LargeFileUploadTask<DriveItem> fileUploadTask = new(uploadSession, stream, maxChunckSize, graphClient.RequestAdapter);

            try
            {
               var uploadResult= await fileUploadTask.UploadAsync(progress);
                Console.WriteLine(uploadResult.ItemResponse.ToString());
                Console.WriteLine(uploadResult.UploadSucceeded ?
                $"Upload complete, item ID: {uploadResult.ItemResponse.Id}" :
                "Upload failed");
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Error uploading: {ex.Message}");
            }

            
        }

        public async Task UploadFolderAsync(StorageFolder folder, string itemId, IProgress<long> progress = null)
        {
            ulong totalSize = await Utils.GetFolderSize(folder);
            ulong uploadedSize = 0;
            var files = await folder.GetFilesAsync();
            DriveItem cloudFolder = await CreateFolder(itemId, folder.Name);

            IEnumerable<Task> uploadTasks = files.Select(async file =>
            {
                await UploadFileAsync(file, cloudFolder.Id, progress);
                ulong fileSize = (await file.GetBasicPropertiesAsync()).Size;
                Interlocked.Add(ref uploadedSize, fileSize);
                progress?.Report((long)(uploadedSize / totalSize));
            });
            await Task.WhenAll(uploadTasks);

            IReadOnlyList<StorageFolder> subfolders = await folder.GetFoldersAsync();
            IEnumerable<Task> subfolderTasks = subfolders.Select(subfolder => UploadFolderAsync(subfolder, cloudFolder.Id, progress));
            await Task.WhenAll(subfolderTasks);
        }

        public async Task<string> CreateLink(string itemId, DateTimeOffset? expirationDateTime = null, string password = null, string type = "view")
        {
            Microsoft.Graph.Drives.Item.Items.Item.CreateLink.CreateLinkPostRequestBody requestBody = new()
            {
                Type = type,
                Password = password,
                Scope = "anonymous",
                RetainInheritedPermissions = false,
                ExpirationDateTime = expirationDateTime,
            };
            Permission result = await graphClient.Drives[DriveId].Items[itemId].CreateLink.PostAsync(requestBody);
            return result.Link.WebUrl;
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
            Stream result = await graphClient.Drives[DriveId].Items[itemId].Content.GetAsync(requestConfiguration =>
            {
                // 文档上是这么写的，但是有个错误，查看源码发现，QueryParameters没有定义
                // requestConfiguration.QueryParameters.Format = format;
                requestConfiguration.Headers.Add("Format", format);
            });
            using Stream fileStream = await file.OpenStreamForWriteAsync();
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

        public async Task<SearchWithQGetResponse> SearchLocalItems(string query, string itemId)
        {
            // 根据代码，Microsoft.Graph.Drives.Item.Items.Item.SearchWithQ.SearchWithQResponse
            // 与 Microsoft.Graph.Drives.Item.SearchWithQ.SearchWithQResponse 相同，那么微软为什么这样做？
            return await graphClient.Drives[DriveId].Items[itemId].SearchWithQ(query).GetAsSearchWithQGetResponseAsync();
        }

        public async Task<Microsoft.Graph.Drives.Item.SearchWithQ.SearchWithQGetResponse> SearchGlobalItems(string query)
        {
            return await graphClient.Drives[DriveId].SearchWithQ(query).GetAsSearchWithQGetResponseAsync();
        }

        //获取受限资源的访问令牌，委托getTokenDelegate，scopes限定权限范围
        private class TokenProvider(Func<string[], Task<string>> getTokenDelegate, string[] scopes) : IAccessTokenProvider
        {
            private readonly Func<string[], Task<string>> getTokenDelegate = getTokenDelegate;
            private readonly string[] scopes = scopes;

            public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object> additionalAuthenticationContext = default,
                CancellationToken cancellationToken = default)
            {
                return getTokenDelegate(scopes);
            }

            public AllowedHostsValidator AllowedHostsValidator { get; }
        }

        public async Task Login()
        {
            TokenProvider tokenProvider = new(async Task<string> (string[] scopes) =>
            {
                IEnumerable<IAccount> accounts = await PublicClientApp.GetAccountsAsync().ConfigureAwait(false);

                try
                {
                    //尝试使用静默方式获取令牌 (AcquireTokenSilent)，如果失败则尝试交互式方式获取令牌 (AcquireTokenInteractive)
                    authResult = await PublicClientApp
                                    .AcquireTokenSilent(scopes, accounts.First(account => account.HomeAccountId.Identifier == HomeAccountId))
                                    .ExecuteAsync();
                }
                catch (Exception exception) when (exception is MsalUiRequiredException || exception is InvalidOperationException)
                {
                    try
                    {
                        authResult = await PublicClientApp.AcquireTokenInteractive(scopes).ExecuteAsync();
                    }
                    catch (MsalException msalex)
                    {
                        Console.WriteLine(msalex);
                    }
                    catch (Exception odataEx)
                    {
                        Debug.WriteLine($"OData Error: {odataEx}");
                    }
                }
                if (authResult != null)
                {
                    //身份验证通过
                    IsAuthenticated = true;
                }
                HomeAccountId = authResult?.Account.HomeAccountId.Identifier;
                return authResult?.AccessToken;
            }, scopes);

            BaseBearerTokenAuthenticationProvider authProvider = new(tokenProvider);
            graphClient = new(authProvider);
            await Task.FromResult(graphClient);
            SaveTokenCache();
            try
            {
                Drive driveItem = await graphClient.Me.Drive.GetAsync();
                DriveId = driveItem.Id;
            }
            catch
            {
            }
        }

        public static void SaveTokenCache()
        {
            MsalCacheHelper cacheHelper = Ioc.Default.GetService<MsalCacheHelper>();
            cacheHelper.RegisterCache(PublicClientApp.UserTokenCache);
        }

        private static IPublicClientApplication PublicClientApp;
        private readonly string[] scopes = ["User.Read", "Files.ReadWrite.All"];
        private static AuthenticationResult authResult;
        private GraphServiceClient graphClient;

        public string DriveId;
        public bool IsAuthenticated = false;
        public string ClientId;
        // 暂时用来识别账户
        public string HomeAccountId;
    }
}
