using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class RenameFileViewModel : ObservableObject
    {
        public RenameFileViewModel(DriveViewModel drive,FileViewModel file)
        {
            Drive = drive;
            _file = file;
            _fileName = file.Name;
        }


        [RelayCommand]
        private async Task RenameFile()
        {
            await Drive.Provider.RenameFile(_file.Id, FileName);
            await Drive.Refresh();
        }


        public DriveViewModel Drive { get; }
        private readonly FileViewModel _file; 
        [ObservableProperty]private string _fileName;
    }
}