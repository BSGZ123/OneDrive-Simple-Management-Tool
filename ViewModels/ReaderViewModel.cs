using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class ReaderViewModel : ObservableObject
    {
        private ReaderSettings _displayedSettings;
        public ReaderViewModel(ReaderSession session) { Session = session; session.Changed += Refresh; Refresh(); }
        public ReaderSession Session { get; }
        [ObservableProperty] private double _fontSize = 20;
        [ObservableProperty] private double _lineHeight = 1.6;
        [ObservableProperty] private double _readingWidth = 720;
        [ObservableProperty] private int _themeIndex;
        [ObservableProperty] private int _flowIndex;
        [ObservableProperty] private int _zoomIndex;
        public string Title => string.IsNullOrWhiteSpace(Session.Title) ? "Reader_Title".GetLocalized() : Session.Title;
        public bool IsBusy => Session.State is ReaderState.Preparing or ReaderState.Loading or ReaderState.Restoring or ReaderState.Closing;
        public bool IsReady => Session.State == ReaderState.Ready && !Session.IsCommandBusy;
        public bool IsReflowableReady => IsReady && !Session.FixedLayout;
        public bool IsFixedReady => IsReady && Session.FixedLayout;
        public bool HasError => Session.State == ReaderState.Failed;
        public bool HasNotice => Session.SaveFailed || Session.NoticeKey != null;
        public bool SaveFailed => Session.SaveFailed;
        public string Notice => (Session.SaveFailed ? "Reader_StorageFailure" : Session.NoticeKey ?? "Reader_Empty").GetLocalized();
        public string Status => (Session.State switch
        {
            ReaderState.Preparing => "Reader_Preparing", ReaderState.Loading => "Reader_Loading",
            ReaderState.Restoring => "Reader_Restoring", ReaderState.Closing => "Reader_Closing",
            ReaderState.Ready => "Reader_Ready", ReaderState.Failed => "Reader_Failed", _ => "Reader_Empty"
        }).GetLocalized();
        public string Error => (Session.ErrorCode switch
        {
            "BookLimit" => "Reader_BookLimit", "InvalidBook" => "Reader_InvalidBook", "UnsupportedEncryption" => "Reader_Encryption",
            "RuntimeUnavailable" => "Reader_Runtime", "ProcessFailed" => "Reader_Crashed", "TimedOut" => "Reader_Timeout", _ => "Reader_LoadFailed"
        }).GetLocalized();
        public string Progress => Session.Location?.Fraction is double fraction ? fraction.ToString("P0") : "—";
        private void Refresh()
        {
            if (_displayedSettings != Session.Settings)
            {
                _displayedSettings = Session.Settings;
                FontSize = Session.Settings.FontSize; LineHeight = Session.Settings.LineHeight; ReadingWidth = Session.Settings.Width;
                ThemeIndex = Session.Settings.Theme == "dark" ? 1 : Session.Settings.Theme == "sepia" ? 2 : 0;
                FlowIndex = Session.Settings.Flow == "scrolled" ? 1 : 0; ZoomIndex = Session.Settings.Zoom == "fit-width" ? 1 : 0;
            }
            foreach (string property in new[] { nameof(Title), nameof(IsBusy), nameof(IsReady), nameof(IsReflowableReady), nameof(IsFixedReady),
                nameof(HasError), nameof(HasNotice), nameof(SaveFailed), nameof(Notice), nameof(Status), nameof(Error), nameof(Progress) }) OnPropertyChanged(property);
            PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged(); ApplySettingsCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        }
        [RelayCommand(CanExecute = nameof(IsReady))] private Task PreviousAsync() => Session.TurnAsync("prev");
        [RelayCommand(CanExecute = nameof(IsReady))] private Task NextAsync() => Session.TurnAsync("next");
        [RelayCommand(CanExecute = nameof(HasError))] private Task RetryAsync() => Session.RetryAsync();
        [RelayCommand] private Task RetrySaveAsync() => Session.FlushAsync();
        [RelayCommand(CanExecute = nameof(IsReady))]
        private Task ApplySettingsAsync() => Session.ApplySettingsAsync(new ReaderSettings
        {
            FontSize = FontSize, LineHeight = LineHeight, Width = ReadingWidth,
            Theme = ThemeIndex == 1 ? "dark" : ThemeIndex == 2 ? "sepia" : "light",
            Flow = FlowIndex == 1 ? "scrolled" : "paginated", Zoom = ZoomIndex == 1 ? "fit-width" : "fit-page"
        });
        public void Detach() => Session.Changed -= Refresh;
    }
}
