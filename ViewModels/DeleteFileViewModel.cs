using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DeleteFileViewModel : FileOperationViewModel
    {
        public DeleteFileViewModel(FileViewModel file) => File = file;

        [RelayCommand]
        public Task DeleteFile() => RunAsync(File.Drive, async () =>
        {
            if (PermanentDelete)
                await File.Drive.Provider.PermanentDeleteItem(File.Id);
            else
                await File.Drive.Provider.DeleteItem(File.Id);

            // Remove the deleted item even if the subsequent refresh fails.
            File.Drive.RemoveFile(File.Id);
        });

        [ObservableProperty] private bool _permanentDelete;
        public FileViewModel File { get; }
    }
}
