using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Windows.Storage;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class TaskManagerViewModel : ObservableObject
    {
        public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
        public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();

        public async Task AddDownloadTask(DriveViewModel drive,string itemId,StorageFile file)
        {
            DownloadTaskViewModel downloadTask = new(drive, itemId, file);
            DownloadTasks.Add(downloadTask);
            await downloadTask.StartDownload();
        }
    }
}