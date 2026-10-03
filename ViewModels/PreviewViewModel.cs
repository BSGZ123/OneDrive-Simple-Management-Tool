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
        public PreviewViewModel(string fileName, PreviewContentLoader loader, PreviewOptions options = null)
        {
            FileName = fileName;
            _loader = loader;
            Options = options ?? new();
        }

        public string FileName { get; }
        public PreviewOptions Options { get; }
        public bool DownloadRequested { get; set; }
        public PreviewState State => _session?.State ?? PreviewState.Preparing;
        public bool IsLoading => State is PreviewState.Preparing or PreviewState.Loading or PreviewState.Retrying;
        public bool HasError => State == PreviewState.Failed;
        public bool CanRetry => HasError;
        public string RetryText => "Preview_Retry".GetLocalized();
        public string DownloadText => "Preview_Download".GetLocalized();
        public string CloseText => "Preview_Close".GetLocalized();
        public string ErrorMessage => HasError ? ("Preview_Error_" + _session.Failure).GetLocalized() : string.Empty;
        public string StatusText => ("Preview_Status_" + (IsBuffering ? "Buffering" : State.ToString())).GetLocalized();
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private bool _isBuffering;
        [ObservableProperty] private string _notice = string.Empty;

        public void Configure(Func<PreviewContent, CancellationToken, Task> render, Action reset)
        {
            if (_session != null) throw new InvalidOperationException("The preview is already configured.");
            _session = new(_loader.LoadAsync, render, reset, Options);
            _session.Changed += Refresh;
        }

        public Task StartAsync() => _session.StartAsync();
        [RelayCommand(CanExecute = nameof(CanRetry))]
        private Task Retry() => StartAsync();
        public void ReportFailure(PreviewFailure failure) => _session.ReportFailure(failure);
        public Task CloseAsync() => _session?.CloseAsync() ?? Task.CompletedTask;

        private void Refresh()
        {
            IsBuffering = false;
            if (State is PreviewState.Preparing or PreviewState.Closed) Notice = string.Empty;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(StatusText));
            RetryCommand.NotifyCanExecuteChanged();
        }
    }
}
