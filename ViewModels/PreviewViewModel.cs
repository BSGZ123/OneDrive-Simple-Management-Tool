using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class PreviewViewModel : ObservableObject
    {
        public PreviewViewModel(FileViewModel viewModel) { _file = viewModel; }

        [RelayCommand]
        public async Task LoadTextContent()
        {
            IsLoading = true;
            Stream stream = await _file.Drive.Provider.GetItemContent(_file.Id);
            using StreamReader reader = new(stream);
            Text = reader.ReadToEnd();
            IsLoading = false;
        }

        private readonly FileViewModel _file;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private string _text;

    }
}