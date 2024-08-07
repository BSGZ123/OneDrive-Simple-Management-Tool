using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class TaskManagerViewModel : ObservableObject
    {
        public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
        public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();


    }
}