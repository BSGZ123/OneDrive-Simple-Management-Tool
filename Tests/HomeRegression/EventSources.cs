using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Models;
using System.Collections.ObjectModel;

namespace OneDrive_Simple_Management_Tool.ViewModels;

// The existing transfer/sync models are event sources at this boundary. The UI probe
// also exercises Home with the real WinUI models; no transfer worker runs in this suite.
public sealed class TaskManagerViewModel
{
    public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();
    public ObservableCollection<UploadTaskViewModel> UploadTasks { get; } = new();
}
public partial class DownloadTaskViewModel : ObservableObject
{
    [ObservableProperty] private DownloadTaskState _state;
}
public partial class UploadTaskViewModel : ObservableObject
{
    [ObservableProperty] private UploadTaskState _state;
}
public partial class FolderSyncViewModel : ObservableObject
{
    public ObservableCollection<FolderSyncItemViewModel> Bindings { get; } = new();
    [ObservableProperty] private string _errorMessage = "";
    public Func<Task> Initialize { get; set; } = () => Task.CompletedTask;
    public Task InitializeAsync() => Initialize();
}
public partial class FolderSyncItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _hasIssues;
    [ObservableProperty] private string _commandError;
}
