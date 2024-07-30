using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CloudViewModel : ObservableObject
    {



        private bool isCacheLoaded = false;
        [ObservableProperty] private ObservableCollection<DriveViewModel> drives = [];
    }
}