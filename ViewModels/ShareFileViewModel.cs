using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class ShareFileViewModel : ObservableObject
    {
        private readonly FileViewModel _file;
        private readonly Action<string> _copyLink;

        public ShareFileViewModel(FileViewModel file, Action<string> copyLink)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _copyLink = copyLink ?? throw new ArgumentNullException(nameof(copyLink));
        }

        public string FileName => _file.Name;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateLinkCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyLinkCommand))]
        private bool _isGenerating;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateLinkCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyLinkCommand))]
        private string _sharingLink = string.Empty;

        [ObservableProperty] private string _statusMessage = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = string.Empty;

        private bool CanGenerateLink() => !IsGenerating && string.IsNullOrEmpty(SharingLink);
        private bool CanCopyLink() => !IsGenerating && !string.IsNullOrEmpty(SharingLink);

        [RelayCommand(CanExecute = nameof(CanGenerateLink))]
        public async Task GenerateLink()
        {
            if (!CanGenerateLink()) return;

            IsGenerating = true;
            ErrorMessage = string.Empty;
            StatusMessage = "ShareFile_Generating".GetLocalized();
            try
            {
                SharingLink = await _file.Drive.Provider.CreateLink(_file.Id);
                StatusMessage = "ShareFile_Ready".GetLocalized();
            }
            catch (Exception exception)
            {
                StatusMessage = string.Empty;
                string resourceKey = exception switch
                {
                    ApiException api when api.ResponseStatusCode == 403 => "ShareFile_AccessDenied",
                    ApiException api when api.ResponseStatusCode == 404 => "ShareFile_NotFound",
                    ApiException api when api.ResponseStatusCode == 401 => "ShareFile_AuthenticationFailed",
                    ApiException api when api.ResponseStatusCode == 429 => "ShareFile_TooManyRequests",
                    MsalException => "ShareFile_AuthenticationFailed",
                    HttpRequestException => "ShareFile_NetworkFailed",
                    OperationCanceledException => "ShareFile_NetworkFailed",
                    InvalidDataException => "ShareFile_InvalidResponse",
                    ArgumentException => "ShareFile_InvalidItem",
                    _ => "ShareFile_Failed"
                };
                ErrorMessage = resourceKey.GetLocalized();
            }
            finally
            {
                IsGenerating = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanCopyLink))]
        private void CopyLink()
        {
            if (!CanCopyLink()) return;

            ErrorMessage = string.Empty;
            try
            {
                _copyLink(SharingLink);
                StatusMessage = "ShareFile_Copied".GetLocalized();
            }
            catch (Exception)
            {
                StatusMessage = string.Empty;
                ErrorMessage = "ShareFile_CopyFailed".GetLocalized();
            }
        }
    }
}
