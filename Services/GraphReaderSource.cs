using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class GraphReaderSource
    {
        public static ReaderOpenRequest Create(OneDrive provider, string itemId)
        {
            var identity = new ReaderIdentity("onedrive", provider.HomeAccountId, provider.DriveId, itemId);
            if (!ReaderProtocol.ValidIdentity(identity)) throw new ReaderException("AccessDenied");
            return new(Identity: identity, ResolveSource: ResolveAsync);

            async Task<DownloadSource> ResolveAsync(CancellationToken token)
            {
                CheckIdentity();
                try
                {
                    var item = await provider.GetItem(itemId, token).ConfigureAwait(false);
                    CheckIdentity();
                    if (item?.Id != itemId || item.File == null || item.Folder != null || item.RemoteItem != null || item.Deleted != null
                        || item.ParentReference?.DriveId != identity.DriveId || !Supports(item.Name)) throw new ReaderException("InvalidBook");
                    if (item.Size is not > 0 or > ReaderProtocol.MaximumBookBytes) throw new ReaderException("BookLimit");
                    string version = !string.IsNullOrWhiteSpace(item.CTag) ? "ctag:" + item.CTag
                        : !string.IsNullOrWhiteSpace(item.ETag) ? "etag:" + item.ETag : null;
                    item.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var raw);
                    if (!ReaderProtocol.ValidText(version, 2048) || !Uri.TryCreate(raw?.ToString(), UriKind.Absolute, out var uri)
                        || uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new ReaderException("InvalidBook");
                    return new(uri.AbsoluteUri, item.Size.Value, version, item.File.Hashes?.Sha256Hash, item.File.Hashes?.Sha1Hash);
                }
                catch (ApiException error) { throw new ReaderException(error.ResponseStatusCode switch { 401 or 403 => "AccessDenied", 404 => "NotFound", _ => "Network" }); }
                catch (AccountAuthenticationException error) { throw new ReaderException(error.Failure == AuthenticationFailure.Network ? "Network" : "AccessDenied"); }
                catch (MsalException) { throw new ReaderException("AccessDenied"); }
                catch (HttpRequestException) { throw new ReaderException("Network"); }
            }
            void CheckIdentity()
            {
                if (provider.HomeAccountId != identity.AccountId || provider.DriveId != identity.DriveId) throw new ReaderException("AccessDenied");
            }
        }
        public static bool Supports(string name) => string.Equals(Path.GetExtension(name), ".epub", StringComparison.OrdinalIgnoreCase);
    }
}
