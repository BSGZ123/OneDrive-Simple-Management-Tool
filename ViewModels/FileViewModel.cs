using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Storage;
using WinRT.Interop;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class FileViewModel : ObservableObject
    {
        public FileViewModel(DriveViewModel drive, DriveItem file, bool loadThumbnail = false)
        {
            Drive = drive;
            _file = file;
            ItemType = IsFile ? "File" : "Folder";
        }

        [RelayCommand]
        private async Task DownloadFile(string itemId)
        {
            Window _downloadPathSelectWindow = new();
            IntPtr hwnd = WindowNative.GetWindowHandle(_downloadPathSelectWindow);
            FileSavePicker savePicker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            savePicker.FileTypeChoices.Add("All files", new List<string>() { Path.GetExtension(_file.Name) });
            savePicker.SuggestedFileName = _file.Name;
            InitializeWithWindow.Initialize(savePicker, hwnd);
            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                TaskManagerViewModel manager = Ioc.Default.GetService<TaskManagerViewModel>();
                await manager.AddDownloadTask(Drive, itemId, file);
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

        [RelayCommand]
        public async Task LoadContent()
        {
            if (IsFile)
            {
                Content = (await Drive.Provider.GetItemContent(Id)).ToString();
            }
        }

        [ObservableProperty] private BitmapImage _image;
        [ObservableProperty] private string _content;
        private readonly DriveItem _file;

        public string Id { get => _file.Id; }
        public string Name { get => _file.Name; }
        public long? Size { get => _file.Size; }
        public DateTimeOffset? Updated { get => _file.LastModifiedDateTime; }
        public bool IsFile { get => _file.Folder == null; }
        public bool IsFolder { get => !IsFile; }
        public int? ChildrenCount { get => _file.Folder?.ChildCount; }
        public DriveViewModel Drive { get; }
        public string ItemType { get; }
        public string DownloadUrl { get => _file.AdditionalData["@microsoft.graph.downloadUrl"].ToString(); }
        public bool CanPreview { get => IsFile && Utils.GetFileType(Path.GetExtension(Name)) != FileType.Unknown; }
    }
}
