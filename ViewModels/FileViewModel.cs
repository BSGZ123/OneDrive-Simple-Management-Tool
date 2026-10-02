using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using OneDrive_Simple_Management_Tool.Helpers;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class FileViewModel : ObservableObject
    {
        public FileViewModel(DriveViewModel drive,DriveItem file,bool loadThumbnail=false) 
        {
            Drive = drive;
            _file = file;
            ItemType = (IsFile ? "FileItem_File" : "FileItem_Folder").GetLocalized();
        }


        [RelayCommand]
        private async Task DownloadFile(string itemId)
        {

            if (!IsFile || itemId != Id) return;
            Drive.ErrorMessage = string.Empty;
            try
            {
                StorageFile file = await SaveFilePickerHelper.PickAsync(Name, Path.GetExtension(Name));
                if (file == null) return;
                TaskManagerViewModel manager = Ioc.Default.GetService<TaskManagerViewModel>();
                await manager.AddDownloadTask(Drive, Id, file);
            }
            catch (Exception exception)
            {
                Drive.ErrorMessage = FileOperationErrors.GetMessage(exception);
            }
        }


        public async Task LoadImage()
        {
            if (IsFile && _file.Image != null)
            {
                using Stream stream = await Drive.Provider.GetItemContent(_file.Id);
                var randomAccessStream = new InMemoryRandomAccessStream();
                await RandomAccessStream.CopyAsync(stream.AsInputStream(), randomAccessStream);
                randomAccessStream.Seek(0);
                BitmapImage img = new();
                await img.SetSourceAsync(randomAccessStream);
                Image = img;
            }
        }

        public async Task LoadContent()
        {
            if (IsFile)
            {
                Content = (await Drive.Provider.GetItemContent(Id)).ToString();
            }
        }

        private readonly DriveItem _file;
        [ObservableProperty] private BitmapImage _image;
        [ObservableProperty] private string _content;

        public string Id { get => _file.Id; }
        public string Name { get => _file.Name; }
        public long? Size { get => _file.Size; }
        public bool IsFile { get => _file.Folder == null; }
        public bool IsFolder { get => !IsFile; }
        public bool IsImage => IsFile && _file.Image != null;
        public bool CanConvert => IsFile && FileConversionRules.Supports(Name);
        public bool CanOpen => IsFolder || CanPreview;
        public int? ChildrenCount { get => _file.Folder?.ChildCount; }
        public DriveViewModel Drive { get; }
        public string ItemType { get; }
        public DateTimeOffset? Updated { get => _file.LastModifiedDateTime; }
        public string DownloadUrl => _file.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var url) ? url?.ToString() : null;
        public bool CanPreview => IsFile && Utils.GetFileType(Path.GetExtension(Name).ToLowerInvariant())
            is FileType.Markdown or FileType.Image or FileType.Media or FileType.Pdf;
    }
}
