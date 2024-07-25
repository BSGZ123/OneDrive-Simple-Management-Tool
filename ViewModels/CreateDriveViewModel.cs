using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CreateDriveViewModel : ObservableObject
    {
        public CreateDriveViewModel(CloudViewModel cloud)
        {
            _cloud = cloud;
        }

        [RelayCommand]
        public async Task CreateDrive()
        {
            OneDrive drive = new();
            await drive.Login();
            if (drive.IsAuthenticated)
            {
                DriveViewModel driveViewModel = new(drive, DisplayName);
                _cloud.AddDrive(driveViewModel);
            }
        }

        private readonly CloudViewModel _cloud;
        [ObservableProperty] private string _displayName;
    }
}
