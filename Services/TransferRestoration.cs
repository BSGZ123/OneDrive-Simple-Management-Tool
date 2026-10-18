using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Services
{
    internal static class TransferRestoration
    {
        public static void Restore(TaskManagerViewModel manager, TransferRecord record)
        {
            var provider = new OneDrive(record.DriveId, record.AccountId);
            if (record.Direction == TransferDirection.Upload)
            {
                var task = new UploadTaskViewModel(new DriveViewModel(provider), record.ItemId, null,
                    new UploadConflictResolver(TransferDialogs.ResolveAsync));
                task.AttachRecord(record, manager.SaveRecord, restore: true);
                manager.UploadTasks.Add(task);
            }
            else
            {
                IDownloadSession session = record.IsFolder
                    ? new DownloadBatchSession(provider, record.Roots.Select(x => x.Id), record.Path,
                        names: record.Roots.ToDictionary(x => x.Id, x => x.Name))
                    : new DownloadSession(record.Path, token => provider.GetDownloadSourceAsync(record.ItemId, token),
                        new DownloadSessionOptions { StagingId = record.Id });
                var task = new DownloadTaskViewModel(record.Name, session, manager, record.AccountId, record.DriveId);
                task.AttachRecord(record, manager.SaveRecord, restore: true);
                manager.DownloadTasks.Add(task);
            }
        }
    }
}
