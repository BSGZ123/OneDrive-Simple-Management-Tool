using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using System.IO;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class RenameFileViewModel : FileOperationViewModel
    {
        private readonly FileViewModel _file;

        public RenameFileViewModel(DriveViewModel drive, FileViewModel file)
        {
            Drive = drive;
            _file = file;
            _fileName = file.Name;
        }

        [RelayCommand]
        public Task RenameFile() => RunAsync(Drive, async () =>
        {
            var result = await Drive.Provider.RenameFile(_file.Id, FileName);
            if (string.IsNullOrWhiteSpace(result?.Id)) throw new InvalidDataException();
        });

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSubmit))]
        private string _fileName;

        public override bool CanSubmit => base.CanSubmit && FileNameRules.IsValid(FileName) && FileName != _file.Name;
        public DriveViewModel Drive { get; }
    }
}