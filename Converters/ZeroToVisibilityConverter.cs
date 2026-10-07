using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace OneDrive_Simple_Management_Tool.Converters
{
    // Shows an empty-state element only while the bound collection count is zero.
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
