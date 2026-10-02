using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class SaveFilePickerHelper
    {
        public static async Task<StorageFile> PickAsync(string name, string extension)
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = name
            };
            picker.FileTypeChoices.Add("FilePicker_FileType".GetLocalized(),
                new List<string> { string.IsNullOrEmpty(extension) ? "." : extension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.StartupWindow));
            return await picker.PickSaveFileAsync();
        }
    }
}
