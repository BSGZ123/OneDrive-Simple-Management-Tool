using CommunityToolkit.Mvvm.ComponentModel;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class CreateFolderViewModel : ObservableObject
    {
        public CreateFolderViewModel(DriveViewModel drive) 
        {
            Drive = drive;
        }


        public DriveViewModel Drive;
    }
}