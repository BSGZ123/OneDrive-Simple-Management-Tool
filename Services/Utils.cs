using Microsoft.Graph.Models;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Services
{
    public class Utils
    {
        public static string ReadableFileSize(long? bytes)
        {
            if (bytes is long size)
            {
                if (size == 0) return "0";
                if (size < 1024)
                {
                    return size.ToString("F0") + " bytes";
                }

                if (size >> 10 < 1024)
                {
                    return (size / 1024F).ToString("F1") + " KB";
                }

                if (size >> 20 < 1024)
                {
                    return ((size >> 10) / 1024F).ToString("F1") + " MB";
                }

                if (size >> 30 < 1024)
                {
                    return ((size >> 20) / 1024F).ToString("F1") + " GB";
                }

                if (size >> 40 < 1024)
                {
                    return ((size >> 30) / 1024F).ToString("F1") + " TB";
                }

                if (size >> 50 < 1024)
                {
                    return ((size >> 40) / 1024F).ToString("F1") + " PB";
                }
                return ((size >> 50) / 1024F).ToString("F1") + " EB";
            }
            return "0";
        }
        //实现简单的文件类型识别功能
        public static FileType GetFileType(string ext)
        {
            return ext switch
            {
                ".txt" => FileType.Text,
                ".md" => FileType.Markdown,
                //https://learn.microsoft.com/zh-cn/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.image?view=windows-app-sdk-1.5#image-file-formats
                string _ when ImageType.Contains(ext) => FileType.Image,
                string _ when MediaType.Contains(ext) => FileType.Media,
                _ => FileType.Unknown,
            };
        }

        //获取文件夹内文件累计大小
        public static async Task<ulong> GetFolderSize(StorageFolder folder)
        {
            ulong res = 0;
            foreach (StorageFile file in await folder.GetFilesAsync())
            {
                Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
                res += properties.Size;
            }

            //获取子文件并累计大小
            foreach (StorageFolder subFolder in await folder.GetFoldersAsync())
            {
                res += await GetFolderSize(subFolder);
            }
            return res;
        }



        public static string GetVersion()
        {
            Version version = Assembly.GetEntryAssembly().GetName().Version;
            return string.Format("{0}.{1},{2}",version.Major,version.Minor,version.Build);
        }

        public static readonly string[] ImageType = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".ico", ".svg" };
        public static readonly string[] MediaType = { ".mp3", ".mp4", ".wma", ".3gp", ".aac", ".flac", ".wax", ".wav", ".wmx", ".wpl", ".avi" };
        public static readonly string[] EBookType = { ".epub", ".pdf" };
    }
}
