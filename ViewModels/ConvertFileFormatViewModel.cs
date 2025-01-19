using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph.Models;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class ConvertFileFormatViewModel : ObservableObject
    {

        private const string PdfDocumentDescription = "PDF Document";
        private const string PdfExtension = ".pdf";

        public ConvertFileFormatViewModel(FileViewModel file) 
        {
            _file = file;
        }

        [RelayCommand]
        public async Task ConvertFileFormat()
        {
            Window _downloadPathSelectWindow = new Window();
            //临时的 Window 对象，用于获取句柄，以便正确显示文件选择对话框
            IntPtr windowHandle = WindowNative.GetWindowHandle(_downloadPathSelectWindow);
            FileSavePicker fileSavePicker = new() 
            {
                SuggestedStartLocation=PickerLocationId.Downloads
            };
            fileSavePicker.FileTypeChoices.Add(PdfDocumentDescription,[PdfExtension]);
            //设置默认保存文件名，使用原始文件名（不带扩展名）
            fileSavePicker.SuggestedFileName= Path.GetFileNameWithoutExtension(_file.Name);
            InitializeWithWindow.Initialize(fileSavePicker, windowHandle);

            StorageFile file=await fileSavePicker.PickSaveFileAsync();
            SavedFilePath = file?.Path;
            string fileExtension = Path.GetExtension(_file.Name).ToLower();

            if (allowedExtensions.Contains(fileExtension))
            {
                await oneDrive.ConvertFileFormat(_file.Id, file);
            }

        }

        private readonly FileViewModel _file;
        private readonly OneDrive oneDrive =Ioc.Default.GetService<OneDrive>();
        private static readonly string[] allowedExtensions = { ".csv", ".doc", ".docx", ".odp", ".ods", ".odt", ".pot", ".potm", ".potx", ".pps", ".ppsx", ".ppsxm", ".ppt", ".pptm", ".pptx", ".rtf", ".xls", ".xlsx" };
        [ObservableProperty] private string _selectedFormat = "pdf";
        [ObservableProperty] private string _savedFilePath;
        public static IEnumerable<string> TargetFormats => ["pdf"];
        public string FormattedExtensions => string.Join(", ", allowedExtensions.Select(ext => ext.TrimStart('.')));
    }
}