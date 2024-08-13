using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Windows.Storage;
using System.Threading.Tasks;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class TaskManagerViewModel : ObservableObject
    {
        public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
        public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();

        public async Task AddDownloadTask(DriveViewModel drive, string itemId, StorageFile file)
        {
            DownloadTaskViewModel downloadTask = new(drive, itemId, file);
            DownloadTasks.Add(downloadTask);
            await downloadTask.StartDownload();
        }

        //重新开始所有下载任务
        public async Task StartAllDownloadTasks()
        {
            foreach (var task in DownloadTasks)
            {
                if (!task.Completed)
                {
                    await task.ResumeDownload();
                }
            }
        }


        public void RemoveSelectedDownloadTasks(DownloadTaskViewModel task)
        {
            DownloadTasks.Remove(task);
        }

        //移除完成下载项
        public void RemoveCompletedDownloadTasks() 
        {
            foreach(var completedTask in DownloadTasks.Where(x => x.Completed).ToList())
            {
                DownloadTasks.Remove(completedTask);
            }
        }

        public async Task AddUploadTask(DriveViewModel drive, string itemId,IStorageItem item )
        {
            UploadTaskViewModel uploadTasks = new(drive, itemId, item);
            UploadTasks.Add(uploadTasks);
            await uploadTasks.StartUpload();
        }

        public void RemoveSelectedUploadTasks(UploadTaskViewModel task)
        {
            UploadTasks.Remove(task);
        }

    }
}