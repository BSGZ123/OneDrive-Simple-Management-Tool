using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Services;
using System.Threading.Tasks;
using System.Windows;


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
            MessageBox.Show("88888888");
            OneDrive oneDrive = new();
            await oneDrive.Login();
            if (oneDrive.IsAuthenticated)
            {
                MessageBox.Show("777777777");
            }
        }


        private readonly CloudViewModel _cloud;
        [ObservableProperty] private string _displayName;
    }
}