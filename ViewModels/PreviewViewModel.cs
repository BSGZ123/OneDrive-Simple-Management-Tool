using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class PreviewViewModel : ObservableObject
    {
        public PreviewViewModel(FileViewModel viewModel) { _file = viewModel; }

        [RelayCommand]
        public async Task LoadTextContent()
        {
            IsLoading = true;
            Stream stream = await _file.Drive.Provider.GetItemContent(_file.Id);
            using StreamReader reader = new(stream);
            Text = reader.ReadToEnd();
            IsLoading = false;
        }

        [RelayCommand]
        public async Task LoadImageContent()
        {
            IsLoading= true;
            Stream stream = await _file.Drive.Provider.GetItemContent(_file.Id);
            //提供对存储在内存而不是磁盘上的输入和输出流中的数据的随机访问
            InMemoryRandomAccessStream inMemoryRandomAccess = new();
            //将之前从文件获取的 stream 对象转换为 IInputStream 接口  将输入流的内容复制到另一个输出流
            await RandomAccessStream.CopyAsync(stream.AsInputStream(),inMemoryRandomAccess);
            //将inMemoryRandomAccess流的当前位置设为开头，因为将流复制到另外一个流中后，流位置会移动到末尾
            inMemoryRandomAccess.Seek(0);
            BitmapImage image = new();
            await image.SetSourceAsync(inMemoryRandomAccess);
            Image = image;
            IsLoading = false;

        }

        //MediaSource 用于访问媒体数据的通用模型，而不需考虑基础媒体格式
        public void LoadMediaSource()
        {
            IsLoading= true;
            string downloadurl=_file.DownloadUrl;
            MediaSource mediaSource = MediaSource.CreateFromUri(new Uri(downloadurl));
            Media= mediaSource;
            IsLoading= false;

        }

        private readonly FileViewModel _file;
        [ObservableProperty]private bool _isLoading;
        [ObservableProperty] private string _text;
        [ObservableProperty]private BitmapImage _image;
        [ObservableProperty]private MediaSource _media;

    }
}