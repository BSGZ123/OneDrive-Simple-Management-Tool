using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class PreviewViewModel : ObservableObject
    {
        private readonly FileViewModel _file;
        public PreviewViewModel(FileViewModel file) => _file = file;

        private async Task LoadAsync(Func<Task> load)
        {
            if (IsLoading) return;
            IsLoading = true;
            ErrorMessage = string.Empty;
            try { await load(); }
            catch (Exception exception) { ErrorMessage = FileOperationErrors.GetMessage(exception); }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        public Task LoadTextContent() => LoadAsync(async () =>
        {
            using Stream stream = await _file.Drive.Provider.GetItemContent(_file.Id);
            using StreamReader reader = new(stream);
            Text = await reader.ReadToEndAsync();
        });

        [RelayCommand]
        public Task LoadImageContent() => LoadAsync(async () =>
        {
            using Stream stream = await _file.Drive.Provider.GetItemContent(_file.Id);
            using InMemoryRandomAccessStream memory = new();
            await RandomAccessStream.CopyAsync(stream.AsInputStream(), memory);
            memory.Seek(0);
            BitmapImage image = new();
            await image.SetSourceAsync(memory);
            Image = image;
        });

        private Uri GetDownloadUri()
        {
            if (!Uri.TryCreate(_file.DownloadUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new InvalidDataException();
            return uri;
        }

        public void LoadMediaSource()
        {
            try { Media = MediaSource.CreateFromUri(GetDownloadUri()); }
            catch (Exception exception) { ErrorMessage = FileOperationErrors.GetMessage(exception); }
        }

        public void GetPreviewPDFSource()
        {
            try { PdfUrl = GetDownloadUri().AbsoluteUri; }
            catch (Exception exception) { ErrorMessage = FileOperationErrors.GetMessage(exception); }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private string _text;
        [ObservableProperty] private BitmapImage _image;
        [ObservableProperty] private MediaSource _media;
        [ObservableProperty] private string _pdfUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = string.Empty;
    }
}