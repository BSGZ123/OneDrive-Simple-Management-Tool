using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public partial class OneDrive
    {
        public static GraphFolderSyncTarget CreateFolderSyncTarget(FolderSyncBinding binding)
        {
            var authentication = Ioc.Default.GetService<IAccountAuthenticationService>();
            return new GraphFolderSyncTarget(GraphAccountDriveResolver.CreateClient(authentication, binding.AccountId), binding);
        }
    }
    // This adapter never downloads or deletes content and never requests conflictBehavior=rename.
    public sealed class GraphFolderSyncTarget(GraphServiceClient client, FolderSyncBinding binding) : IFolderSyncTarget, IFolderSyncBrowser
    {
        private static readonly SemaphoreSlim UploadSlots = new(2, 2);
        private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DriveItem>> _children = new(StringComparer.Ordinal);

        private bool InDrive(DriveItem item) => item.RemoteItem == null && item.Deleted == null &&
            (item.ParentReference?.DriveId == null || item.ParentReference.DriveId == binding.DriveId);

        public async Task ValidateRootAsync(CancellationToken token)
        {
            var root = await client.Drives[binding.DriveId].Items[binding.RemoteFolderId].GetAsync(cancellationToken: token);
            if (root?.Id != binding.RemoteFolderId || root.Folder == null || !InDrive(root))
                throw new FolderSyncException("Sync_RemoteMissing");
            if (root.Name != binding.FolderName) throw new FolderSyncException("Sync_NameMismatch");
            // Stop if the bound folder has been moved: stored ancestry also protects overlapping bindings.
            if (binding.RemoteAncestorIds.Count > 0 && root.ParentReference?.Id != binding.RemoteAncestorIds[^1])
                throw new FolderSyncException("Sync_RemoteMoved");
            _folders.Clear();
            _children.Clear();
            _folders[""] = root.Id;
        }

        public async Task<FolderSyncRemoteFolder> GetRootAsync(CancellationToken token)
        {
            var root = await client.Drives[binding.DriveId].Root.GetAsync(cancellationToken: token);
            if (string.IsNullOrWhiteSpace(root?.Id)) throw new InvalidDataException();
            return new(root.Id, root.Name);
        }

        public async Task<IReadOnlyList<FolderSyncRemoteFolder>> BrowseAsync(string folderId, CancellationToken token)
        {
            var children = await ChildrenAsync(folderId, token);
            return children.Where(i => i.Folder != null && InDrive(i)).Select(i => new FolderSyncRemoteFolder(i.Id, i.Name))
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private async Task<List<DriveItem>> ChildrenAsync(string parent, CancellationToken token)
        {
            if (_children.TryGetValue(parent, out var cached)) return cached;
            var items = new List<DriveItem>();
            var links = new HashSet<string>(StringComparer.Ordinal);
            string link = null;
            do
            {
                var request = client.Drives[binding.DriveId].Items[parent].Children;
                DriveItemCollectionResponse response;
                if (link == null) response = await request.GetAsync(c => c.QueryParameters.Top = 200, token);
                else
                {
                    var service = new Uri(client.RequestAdapter.BaseUrl.TrimEnd('/') + "/");
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != service.Scheme ||
                        uri.Authority != service.Authority || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                        !uri.AbsolutePath.StartsWith(service.AbsolutePath + "drives/" + Uri.EscapeDataString(binding.DriveId) + "/", StringComparison.Ordinal) ||
                        !links.Add(link)) throw new InvalidDataException();
                    response = await request.WithUrl(link).GetAsync(cancellationToken: token);
                }
                if (response?.Value == null || response.Value.Any(i => string.IsNullOrWhiteSpace(i?.Id) || i.Name == null))
                    throw new InvalidDataException();
                items.AddRange(response.Value);
                link = response.OdataNextLink;
            } while (!string.IsNullOrEmpty(link));
            _children[parent] = items;
            return items;
        }

        private async Task<DriveItem> FindAsync(string parent, string name, bool folder, CancellationToken token)
        {
            var matches = (await ChildrenAsync(parent, token)).Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return null;
            if (matches.Count != 1 || matches[0].Name != name) throw new FolderSyncException("Sync_NameMismatch");
            var item = matches[0];
            if (!InDrive(item) || (folder ? item.Folder == null : item.File == null))
                throw new FolderSyncException("Sync_TypeConflict");
            return item;
        }

        public async Task EnsureFolderAsync(string relativePath, CancellationToken token)
        {
            try { await FolderAsync(relativePath, token); }
            catch (ApiException exception) { throw new FolderSyncException(FolderSyncJob.GetErrorKey(exception)); }
        }

        private async Task<string> FolderAsync(string path, CancellationToken token)
        {
            if (_folders.TryGetValue(path, out string id)) return id;
            int slash = path.LastIndexOf('/');
            string name = path[(slash + 1)..];
            FolderSyncRules.ValidateName(name);
            string parent = await FolderAsync(slash < 0 ? "" : path[..slash], token);
            var item = await FindAsync(parent, name, true, token);
            if (item == null)
            {
                try
                {
                    item = await client.Drives[binding.DriveId].Items[parent].Children.PostAsync(new DriveItem
                    {
                        Name = name, Folder = new Folder(),
                        AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" }
                    }, cancellationToken: token);
                }
                catch (ApiException exception) when (exception.ResponseStatusCode == 409)
                {
                    _children.Remove(parent);
                    item = await FindAsync(parent, name, true, token);
                    if (item == null) throw;
                }
            }
            if (string.IsNullOrWhiteSpace(item?.Id) || item.Name != name || item.Folder == null || !InDrive(item))
                throw new FolderSyncException("Sync_NameMismatch");
            RememberChild(parent, item);
            _folders[path] = item.Id;
            return item.Id;
        }

        public async Task UploadAsync(string relativePath, Stream content, IProgress<long> progress, CancellationToken token)
        {
            await UploadSlots.WaitAsync(token);
            try { await UploadCoreAsync(relativePath, content, progress, token); }
            catch (ApiException exception) { throw new FolderSyncException(FolderSyncJob.GetErrorKey(exception)); }
            finally { UploadSlots.Release(); }
        }

        private async Task UploadCoreAsync(string relativePath, Stream content, IProgress<long> progress, CancellationToken token)
        {
            long length = content.Length;
            int slash = relativePath.LastIndexOf('/');
            string name = relativePath[(slash + 1)..];
            FolderSyncRules.ValidateName(name);
            string parent = await FolderAsync(slash < 0 ? "" : relativePath[..slash], token);
            var existing = await FindAsync(parent, name, false, token);
            string behavior = existing == null ? "fail" : "replace";
            DriveItem uploaded;
            if (length == 0)
            {
                var request = client.Drives[binding.DriveId].Items[parent].ItemWithPath(name).Content.ToPutRequestInformation(content);
                request.UrlTemplate += "{?%40microsoft.graph.conflictBehavior}";
                request.QueryParameters["%40microsoft.graph.conflictBehavior"] = behavior;
                if (existing?.ETag != null) request.Headers.Add("If-Match", existing.ETag);
                uploaded = await client.RequestAdapter.SendAsync(request, DriveItem.CreateFromDiscriminatorValue,
                    new Dictionary<string, Microsoft.Kiota.Abstractions.Serialization.ParsableFactory<Microsoft.Kiota.Abstractions.Serialization.IParsable>>
                    { ["4XX"] = ODataError.CreateFromDiscriminatorValue, ["5XX"] = ODataError.CreateFromDiscriminatorValue }, token);
            }
            else
            {
                var body = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
                {
                    Item = new DriveItemUploadableProperties
                    {
                        Name = name,
                        AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = behavior }
                    }
                };
                var session = await client.Drives[binding.DriveId].Items[parent].ItemWithPath(name).CreateUploadSession
                    .PostAsync(body, c => { if (existing?.ETag != null) c.Headers.Add("If-Match", existing.ETag); }, token);
                if (string.IsNullOrWhiteSpace(session?.UploadUrl)) throw new InvalidDataException();
                var upload = new LargeFileUploadTask<DriveItem>(session, content, 320 * 1024);
                var result = await upload.UploadAsync(progress == null ? null : new InlineProgress<long>(offset => progress.Report(offset + 1)),
                    cancellationToken: token);
                if (!result.UploadSucceeded) throw new IOException();
                uploaded = result.ItemResponse;
            }
            if (string.IsNullOrWhiteSpace(uploaded?.Id) || uploaded.Name != name || uploaded.File == null ||
                uploaded.Size != length || !InDrive(uploaded)) throw new FolderSyncException("Sync_InvalidResponse");
            RememberChild(parent, uploaded);
            progress?.Report(length);
        }

        private void RememberChild(string parent, DriveItem item)
        {
            if (!_children.TryGetValue(parent, out var children)) return;
            children.RemoveAll(existing => existing.Id == item.Id || existing.Name == item.Name);
            children.Add(item);
        }

        public void Dispose() => client.Dispose();
    }
}
