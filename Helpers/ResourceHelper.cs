using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Windows.ApplicationModel.Resources;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class ResourceHelper
    {
        private static readonly ResourceLoader _resourceLoader = new();

        public static string GetLocalized(this string resourceKey) => _resourceLoader.GetString(resourceKey);
    }
}
