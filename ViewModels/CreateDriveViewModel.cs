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

        //更简洁的数据绑定和操作
        [RelayCommand]
        public async Task CreateDrive()
        {
            OneDrive oneDrive = new();
            await oneDrive.Login();
            MessageBox.Show("777777777");
            if (oneDrive.IsAuthenticated)
            {
                DriveViewModel driveViewModel = new(oneDrive, DisplayName);
                _cloud.AddDrive(driveViewModel);
            }
        }


        private readonly CloudViewModel _cloud;
        //别忘了在页面前端绑定displayName，以获取云盘别名
        [ObservableProperty] private string _displayName;
    }
}