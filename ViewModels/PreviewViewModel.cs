using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class PreviewViewModel : ObservableObject
    {
        private readonly PreviewContentLoader _loader;
        private PreviewSession _session;
        private bool _decodeAgain;
        public PreviewViewModel(string fileName, PreviewContentLoader loader, PreviewOptions options = null)
        {
            FileName = fileName;
            _loader = loader;
            Options = options ?? new();
        }

        public string FileName { get; }
        public PreviewOptions Options { get; }
        public PreviewKind Kind => _loader.Kind;
        public bool IsText => Kind is PreviewKind.Text or PreviewKind.Markdown;
        public bool CanChangeEncoding => !IsLoading && State != PreviewState.Closed && _loader.HasTextBytes;
        public string SelectedEncoding => _loader.TextEncoding;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private string _actualEncoding = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private double _readingFontSize = 16;
        [ObservableProperty] private bool _wrapText = true;
        [ObservableProperty] private bool _showSource;

        public async Task ChangeEncodingAsync(string encoding)
        {
            if (!CanChangeEncoding || Array.IndexOf(PreviewTextDecoder.Encodings, encoding) < 0) return;
            _loader.TextEncoding = encoding;
            OnPropertyChanged(nameof(SelectedEncoding));
            _decodeAgain = true;
            await StartAsync();
        }

        partial void OnReadingFontSizeChanged(double value)
        {
            if (!double.IsFinite(value)) ReadingFontSize = 16;
            else if (value < 12 || value > 32) ReadingFontSize = Math.Clamp(value, 12, 32);
        }
        public bool DownloadRequested { get; set; }
        public PreviewState State => _session?.State ?? PreviewState.Preparing;
        public bool IsLoading => State is PreviewState.Preparing or PreviewState.Loading or PreviewState.Retrying;
        public bool HasError => State == PreviewState.Failed;
        public bool CanRetry => HasError;
        public string RetryText => "Preview_Retry".GetLocalized();
        public string DownloadText => "Preview_Download".GetLocalized();
        public string CloseText => "Preview_Close".GetLocalized();
        public string ErrorMessage => HasError ? ("Preview_Error_" + _session.Failure).GetLocalized() : string.Empty;
        public string StatusText => ("Preview_Status_" + (IsBuffering ? "Buffering" : State.ToString())).GetLocalized()
            + (IsText && !string.IsNullOrEmpty(ActualEncoding) ? $" · {ActualEncoding} · {ReadingFontSize:0}" : string.Empty);
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private bool _isBuffering;
        [ObservableProperty] private string _notice = string.Empty;

        public void Configure(Func<PreviewContent, CancellationToken, Task> render, Action reset)
        {
            if (_session != null) throw new InvalidOperationException("The preview is already configured.");
            _session = new(LoadAsync, render, reset, Options);
            _session.Changed += Refresh;
        }

        public Task StartAsync() => _session.StartAsync();
        [RelayCommand(CanExecute = nameof(CanRetry))]
        private Task Retry() => StartAsync();
        public void ReportFailure(PreviewFailure failure) => _session.ReportFailure(failure);
        public async Task CloseAsync()
        {
            if (_session != null) await _session.CloseAsync();
            _loader.Clear();
            ActualEncoding = string.Empty;
        }

        private async Task<PreviewContent> LoadAsync(CancellationToken token)
        {
            bool decodeAgain = _decodeAgain;
            _decodeAgain = false;
            ActualEncoding = string.Empty;
            PreviewContent content = await (decodeAgain ? _loader.DecodeTextAsync(token) : _loader.LoadAsync(token));
            token.ThrowIfCancellationRequested();
            ActualEncoding = content.EncodingName ?? string.Empty;
            return content;
        }

        private void Refresh()
        {
            IsBuffering = false;
            if (State is PreviewState.Preparing or PreviewState.Closed) Notice = string.Empty;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanChangeEncoding));
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(StatusText));
            RetryCommand.NotifyCanExecuteChanged();
        }
    }
}
