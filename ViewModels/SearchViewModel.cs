using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        public SearchViewModel(DriveViewModel drive)
        {
            _drive = drive;
            _fileName = drive.Keyword;
            _mode = drive.IsDriveSearch ? SearchMode.Global : SearchMode.Local;
        }

        [RelayCommand(CanExecute = nameof(CanSearch))]
        private async Task Search()
        {
            if (!SearchQueryRules.TryNormalize(FileName, out string keyword)) return;
            if (Mode == SearchMode.Local)
            {
                await _drive.ApplyLocalFilter(keyword);
            }
            else
            {
                await _drive.SearchFile(keyword);
            }
        }

        public enum SearchMode
        {
            Local,
            Global,
        }


        private readonly DriveViewModel _drive;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSearch))]
        [NotifyPropertyChangedFor(nameof(ValidationMessage))]
        [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
        private string _fileName;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ModeIndex))]
        [NotifyPropertyChangedFor(nameof(ModeHint))]
        private SearchMode _mode;

        public int ModeIndex
        {
            get => (int)Mode;
            set { if (value is 0 or 1) Mode = (SearchMode)value; }
        }
        public bool CanSearch => SearchQueryRules.TryNormalize(FileName, out _);
        public string ValidationMessage => string.IsNullOrEmpty(FileName) || CanSearch ? string.Empty : "Search_InvalidKeyword".GetLocalized();
        public string ModeHint => (Mode == SearchMode.Local ? "Search_LocalHint" : "Search_DriveHint").GetLocalized();
    }
}
