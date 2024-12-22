using CommunityToolkit.Mvvm.ComponentModel;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public class PreviewViewModel : ObservableObject
    {
        public PreviewViewModel(FileViewModel viewModel) { _file = viewModel; }


        private readonly FileViewModel _file;
    }
}