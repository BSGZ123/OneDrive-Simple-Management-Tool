using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using System.IO;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CreateFolderViewModel : FileOperationViewModel
    {
        private readonly string _parentItemId;

        public CreateFolderViewModel(DriveViewModel drive)
        {
            Drive = drive;
            _parentItemId = drive.ParentItemId;
        }

        [RelayCommand]
        public Task CreateFolder() => RunAsync(Drive, async () =>
        {
            var result = await Drive.Provider.CreateFolder(_parentItemId, FolderName);
            if (string.IsNullOrWhiteSpace(result?.Id)) throw new InvalidDataException();
        });

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSubmit))]
        private string _folderName = string.Empty;

        public override bool CanSubmit => base.CanSubmit && FileNameRules.IsValid(FolderName);
        public DriveViewModel Drive { get; }
    }
}