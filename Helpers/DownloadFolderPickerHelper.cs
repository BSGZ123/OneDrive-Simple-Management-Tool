using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class DownloadFolderPickerHelper
    {
        public static async Task<StorageFolder> PickAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.StartupWindow));
            return await picker.PickSingleFolderAsync();
        }
    }
}
