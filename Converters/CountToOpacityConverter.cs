using Microsoft.UI.Xaml.Data;
using System;

namespace OneDrive_Simple_Management_Tool.Converters
{
    // De-emphasizes zero counts so non-zero statistics stand out.
    public class CountToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => value is int count && count > 0 ? 1.0 : 0.5;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
