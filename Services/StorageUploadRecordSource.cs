using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class StorageUploadRecordSource(OneDrive drive, TransferRecord record, UploadConflictResolver conflicts,
        Action checkpoint = null, TransferPolicy policy = null) : IUploadRecordSource
    {
        private string LocalPath(string key)
        {
            if (key == ".") return record.Path;
            if (!record.IsFolder || key.Split('/').Any(x => x is "" or "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException();
            return Path.Combine(record.Path, key.Replace('/', Path.DirectorySeparatorChar));
        }

        public async Task<IReadOnlyList<UploadSourceItem>> GetChildrenAsync(string key, CancellationToken token)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(LocalPath(key)).AsTask(token);
            var files = await folder.GetFilesAsync().AsTask(token);
            var folders = await folder.GetFoldersAsync().AsTask(token);
            var result = new List<UploadSourceItem>();
            string ChildKey(string name) => key == "." ? name : key + "/" + name;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                result.Add(new(ChildKey(file.Name), file.Name, false, checked((long)(await file.GetBasicPropertiesAsync().AsTask(token)).Size)));
            }
            result.AddRange(folders.Select(x => new UploadSourceItem(ChildKey(x.Name), x.Name, true, 0)));
            return result;
        }

        public async Task<UploadOutcome> CreateFolderAsync(UploadEntryRecord item, string parentId, CancellationToken token)
        {
            drive.CheckAccess();
            if (!drive.IsAuthenticated) await drive.Login(token);
            return await drive.EnsureUploadFolderAsync(item.Name, parentId, conflicts, token);
        }

        public async Task<UploadOutcome> UploadAsync(UploadEntryRecord item, string parentId, IProgress<long> progress, CancellationToken token)
        {
            drive.CheckAccess();
            if (!drive.IsAuthenticated) await drive.Login(token);
            var file = await StorageFile.GetFileFromPathAsync(LocalPath(item.Key)).AsTask(token);
            item.Size = checked((long)(await file.GetBasicPropertiesAsync().AsTask(token)).Size);
            return checkpoint == null ? await drive.UploadFileAsync(file, parentId, progress, token, conflicts, policy) :
                await drive.UploadFileResumableAsync(file, parentId, item, checkpoint, progress, token, conflicts, policy);
        }
    }
}
