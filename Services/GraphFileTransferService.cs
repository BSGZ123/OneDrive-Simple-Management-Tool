using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    // Pin the drive for the entire dialog. Monitor URLs never receive Graph credentials.
    public sealed class GraphFileTransferService : IFileTransferService
    {
        private static readonly HttpClient MonitorClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 65536 };
        private readonly GraphServiceClient _client;
        private readonly string _drive;
        private readonly HttpClient _monitor;
        private readonly Action _checkIdentity;

        public GraphFileTransferService(GraphServiceClient client, string driveId, HttpClient monitor = null, Action checkIdentity = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _drive = !string.IsNullOrWhiteSpace(driveId) ? driveId : throw new ArgumentException(nameof(driveId));
            _monitor = monitor ?? MonitorClient;
            _checkIdentity = checkIdentity ?? (() => { });
        }

        public async Task<TransferItem> GetRootAsync(CancellationToken token)
        {
            _checkIdentity();
            var root = Map(await _client.Drives[_drive].Root.GetAsync(cancellationToken: token));
            _checkIdentity();
            if (!root.IsRoot || !root.IsUsable || !root.IsFolder) throw new InvalidDataException();
            return root;
        }

        public async Task<TransferItem> GetItemAsync(string id, CancellationToken token)
        {
            _checkIdentity();
            var item = Map(await _client.Drives[_drive].Items[id].GetAsync(cancellationToken: token));
            _checkIdentity();
            if (item.Id != id || !item.IsUsable) throw new FileTransferException("Transfer_Unsupported");
            return item;
        }

        public async Task<IReadOnlyList<TransferItem>> GetChildrenAsync(string parentId, CancellationToken token)
        {
            var result = new Dictionary<string, TransferItem>(StringComparer.Ordinal);
            var links = new HashSet<string>(StringComparer.Ordinal);
            string next = null;
            var request = _client.Drives[_drive].Items[parentId].Children;
            do
            {
                _checkIdentity();
                if (next != null)
                {
                    var expected = request.ToGetRequestInformation().URI;
                    if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != expected.Scheme ||
                        uri.Authority != expected.Authority || uri.AbsolutePath != expected.AbsolutePath ||
                        uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !links.Add(next)) throw new InvalidDataException();
                }
                var page = next == null ? await request.GetAsync(c => c.QueryParameters.Top = 200, token)
                    : await request.WithUrl(next).GetAsync(cancellationToken: token);
                _checkIdentity();
                if (page?.Value == null) throw new InvalidDataException();
                foreach (var raw in page.Value)
                {
                    var item = Map(raw);
                    if (item.ParentId != parentId || item.IsRoot) throw new InvalidDataException();
                    if (result.TryGetValue(item.Id, out var previous) && previous != item) throw new InvalidDataException();
                    result[item.Id] = item;
                }
                next = page.OdataNextLink;
            } while (!string.IsNullOrEmpty(next));
            return result.Values.ToArray();
        }

        private TransferItem Map(DriveItem item)
        {
            if (string.IsNullOrWhiteSpace(item?.Id) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException();
            bool usable = item.Deleted == null && item.RemoteItem == null &&
                (item.ParentReference?.DriveId == null || item.ParentReference.DriveId == _drive) &&
                ((item.File == null) != (item.Folder == null)) &&
                (item.Root != null || !string.IsNullOrWhiteSpace(item.ParentReference?.Id));
            return new(item.Id, item.Name, item.Folder != null, item.ParentReference?.Id, item.ETag, item.Root != null, usable);
        }

        public async Task MoveAsync(TransferItem source, string parentId, string name, CancellationToken token)
        {
            ValidateMutation(source, parentId, name);
            if (string.IsNullOrWhiteSpace(source.ETag)) throw new FileTransferException("Transfer_Changed");
            var request = _client.Drives[_drive].Items[source.Id].ToPatchRequestInformation(new DriveItem
            {
                Name = name, ParentReference = new ItemReference { Id = parentId, DriveId = _drive }
            });
            request.Headers.Add("If-Match", source.ETag);
            // PATCH defaults to failing name collisions. Never delete an existing target.
            using var response = await MutateAsync(request, token);
            await EnsureSuccessAsync(response, token);
            if (response.StatusCode != HttpStatusCode.OK) throw new FileTransferException("Transfer_Uncertain", uncertain: true);
        }

        public async Task<string> CopyAsync(TransferItem source, string parentId, string name, CancellationToken token)
        {
            ValidateMutation(source, parentId, name);
            var request = _client.Drives[_drive].Items[source.Id].Copy.ToPostRequestInformation(new CopyPostRequestBody
            {
                Name = name, ParentReference = new ItemReference { Id = parentId, DriveId = _drive }
            });
            // The default is fail; conflictBehavior query options are unsupported by consumer accounts.
            using var response = await MutateAsync(request, token);
            await EnsureSuccessAsync(response, token);
            if (response.StatusCode != HttpStatusCode.Accepted || !ValidMonitor(response.Headers.Location))
                throw new FileTransferException("Transfer_Uncertain", uncertain: true);
            return response.Headers.Location.AbsoluteUri;
        }

        private void ValidateMutation(TransferItem source, string parentId, string name)
        {
            _checkIdentity();
            if (source == null || !source.IsUsable || source.IsRoot || string.IsNullOrWhiteSpace(parentId) ||
                source.Id == parentId || !FileNameRules.IsValid(name) || name.Length > 255)
                throw new FileTransferException("Transfer_InvalidTarget");
        }

        private async Task<HttpResponseMessage> MutateAsync(RequestInformation request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var handler = new NativeResponseHandler();
            request.AddRequestOptions(new IRequestOption[] { new ResponseHandlerOption { ResponseHandler = handler },
                new RetryHandlerOption { MaxRetry = 0 }, new RedirectHandlerOption { MaxRedirect = 0 } });
            try
            {
                await _client.RequestAdapter.SendNoContentAsync(request, cancellationToken: token);
                return handler.Value as HttpResponseMessage ?? throw new FileTransferException("Transfer_Uncertain", uncertain: true);
            }
            catch (HttpRequestException) { throw new FileTransferException("Transfer_Uncertain", uncertain: true); }
            catch (OperationCanceledException) { throw new FileTransferException("Transfer_Uncertain", uncertain: true); }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            bool conflict = false;
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                try { using var json = await ReadJsonAsync(response, token); conflict = HasCode(json.RootElement, "nameAlreadyExists"); }
                catch (JsonException) { }
            }
            throw new FileTransferException(conflict ? "FileOperation_NameConflict" : response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "FileOperation_AuthenticationFailed",
                HttpStatusCode.Forbidden => "FileOperation_AccessDenied",
                HttpStatusCode.NotFound => "FileOperation_NotFound",
                HttpStatusCode.PreconditionFailed => "Transfer_Changed",
                HttpStatusCode.TooManyRequests => "FileOperation_TooManyRequests",
                _ => (int)response.StatusCode >= 500 ? "Transfer_Uncertain" : "FileOperation_Failed"
            }, conflict, (int)response.StatusCode >= 500);
        }

        public async Task<CopyStatus> GetCopyStatusAsync(string monitorUrl, CancellationToken token)
        {
            _checkIdentity();
            if (!Uri.TryCreate(monitorUrl, UriKind.Absolute, out var uri) || !ValidMonitor(uri)) throw new InvalidDataException();
            using var response = await _monitor.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location;
                if (location?.IsAbsoluteUri != true || location.Scheme != "https") throw new InvalidDataException();
                string[] path = location.AbsolutePath.Split('/');
                if (path.Length < 2 || path[^2] != "items" || string.IsNullOrWhiteSpace(path[^1])) throw new InvalidDataException();
                return new(true, Uri.UnescapeDataString(path[^1]), 100);
            }
            await EnsureSuccessAsync(response, token);
            using var json = await ReadJsonAsync(response, token);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            string status = root.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.ToLowerInvariant() : null;
            if (status is "failed" or "deletefailed")
            {
                bool conflict = HasCode(root, "nameAlreadyExists");
                return new(false, null, 0, conflict ? "FileOperation_NameConflict" : HasCode(root, "accessDenied")
                    ? "FileOperation_AccessDenied" : "FileOperation_Failed", conflict);
            }
            if (status == "completed")
            {
                string id = root.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String ? resource.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException();
                return new(true, id, 100);
            }
            if (status is not ("inprogress" or "notstarted" or "updating" or "waiting")) throw new InvalidDataException();
            double percent = 0;
            if (root.TryGetProperty("percentageComplete", out var p) || root.TryGetProperty("percentComplete", out p))
                if (p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out percent) || !double.IsFinite(percent)) throw new InvalidDataException();
            return new(false, null, Math.Clamp(percent, 0, 99));
        }

        private static bool ValidMonitor(Uri uri) => uri?.IsAbsoluteUri == true && uri.Scheme == "https" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Host is "api.onedrive.com" or "graph.microsoft.com" or "graph.microsoft.us" or "microsoftgraph.chinacloudapi.cn" ||
             new[] { ".sharepoint.com", ".sharepoint.us", ".sharepoint.cn", ".sharepoint.de" }.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, token)) != 0)
            {
                if (buffer.Length + count > 65536) throw new InvalidDataException();
                buffer.Write(chunk, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }

        private static bool HasCode(JsonElement value, string code)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String &&
                    string.Equals(c.GetString(), code, StringComparison.OrdinalIgnoreCase)) return true;
                return value.EnumerateObject().Any(property => HasCode(property.Value, code));
            }
            return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => HasCode(item, code));
        }
    }
}
