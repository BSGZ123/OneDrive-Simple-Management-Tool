using CommunityToolkit.Mvvm.ComponentModel;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        public SearchViewModel(DriveViewModel drive)
        {
            _drive=drive;
        }




        public enum SearchMode
        {
            Local,
            Global,
        }


        public DriveViewModel _drive;
        [ObservableProperty] private string _fileName;
        [ObservableProperty] private SearchMode _mode;
    }
}