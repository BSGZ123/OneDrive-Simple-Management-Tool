using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

//根据文件名后缀判断是否显示"转换"命令
namespace OneDrive_Simple_Management_Tool.Converters
{
    class FileNameToCanConvertCommandVisible : IValueConverter
    {
        //方便在其他地方使用该转换器实例，而不用时常创建新实例
        public static readonly FileNameToCanConvertCommandVisible Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            //允许的后缀名
            string[] allowedExtensions = { ".csv", ".doc", ".docx", ".odp", ".ods", ".odt", ".pot", ".potm", ".potx", ".pps", ".ppsx", ".ppsxm", ".ppt", ".pptm", ".pptx", ".rtf", ".xls", ".xlsx" };
            if(value is string fileName)
            {
                //要注意将获取的后缀名转换为小写。。。
                string fileExtension=Path.GetExtension(fileName).ToLower();
                if (allowedExtensions.Contains(fileExtension)) { return Visibility.Visible; }

            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
