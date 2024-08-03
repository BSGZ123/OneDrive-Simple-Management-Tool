using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


//用于将 DateTimeOffset 类型的值转换为字符串类型
namespace OneDrive_Simple_Management_Tool.Converters
{
    class DateTimeOffsetToStringConverter : IValueConverter
    {
        public static readonly DateTimeOffsetToStringConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.ToString("yyyy/MM/dd HH:mm");
            }

            return "";
        }

        //反向应该没啥用
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
