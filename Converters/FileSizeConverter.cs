using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

//简单的文件大小转换器
namespace OneDrive_Simple_Management_Tool.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        public static readonly FileSizeConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if(value is long size)
            {
                if (size == 0) return "";
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
            return "";
        }

        //常规情况下，文件大小转换不需要实现 ConvertBack，因为我们只需要将字节转换为方便阅读的格式，不需要反向转换
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
