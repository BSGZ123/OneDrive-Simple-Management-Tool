using Microsoft.Graph;
using Microsoft.Graph.Models;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Services
{
    public partial class OneDrive
    {
        public async Task<UploadOutcome> UploadFileResumableAsync(StorageFile file, string parentId, UploadEntryRecord entry,
            Action checkpoint, IProgress<long> progress, CancellationToken token, UploadConflictResolver conflicts, TransferPolicy policy = null)
        {
            // Empty files have no byte ranges and retain the existing single-request path.
            if ((await file.GetBasicPropertiesAsync().AsTask(token)).Size == 0)
            {
                await ResumableUploadSession.DeleteAsync(entry.Resume);
                entry.Resume = null;
                checkpoint();
                return await UploadFileAsync(file, parentId, progress, token, conflicts, policy);
            }
            using var slot = await TransferScheduler.Shared.EnterAsync(token, policy);
            // Refuse concurrent local writers throughout hashing and transfer.
            await using var content = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            async Task<UploadSessionInfo> Create(string behavior, CancellationToken cancellation)
            {
                var body = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
                {
                    Item = new DriveItemUploadableProperties
                    {
                        AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = behavior }
                    }
                };
                var session = await graphClient.Drives[DriveId].Items[parentId].ItemWithPath(file.Name)
                    .CreateUploadSession.PostAsync(body, cancellationToken: cancellation);
                if (string.IsNullOrWhiteSpace(session?.UploadUrl) || session.ExpirationDateTime == null) throw new InvalidDataException();
                return new(session.UploadUrl, session.ExpirationDateTime.Value);
            }
            var transfer = new ResumableUploadSession(entry, checkpoint, Create, policy);
            try { return await transfer.RunAsync(content, entry.Resume?.ConflictBehavior ?? "fail", progress, token); }
            catch (Exception exception) when (conflicts != null &&
                (IsUploadNameConflict(exception) || exception is HttpRequestException { StatusCode: HttpStatusCode.Conflict }))
            {
                var choice = await conflicts.ResolveAsync(new(file.Name, false), token);
                await ResumableUploadSession.DeleteAsync(entry.Resume);
                entry.Resume = null;
                checkpoint();
                if (choice == UploadConflictChoice.Cancel) throw new UploadAbortedException();
                if (choice == UploadConflictChoice.Skip) return new(0, 1);
                return await transfer.RunAsync(content, choice == UploadConflictChoice.Replace ? "replace" : "rename", progress, token);
            }
        }
    }
}
