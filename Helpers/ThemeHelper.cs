using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class ThemeHelper
    {
        private static Window currentApplicationWindow =App.StartupWindow;

        /// <summary>
        /// 根据根元素所请求的主题获取应用程序的当前实际主题，
        /// 或者如果该值为默认值，则获取应用程序所请求的主题。
        /// </summary>
        public static ElementTheme ActualTheme 
        {
            get
            {
                if (currentApplicationWindow.Content is FrameworkElement frameworkElement)
                {
                    if (frameworkElement.RequestedTheme != ElementTheme.Default)
                    {
                        return frameworkElement.RequestedTheme;
                    }
                }

                return (ElementTheme)Enum.Parse(typeof(ElementTheme), Application.Current.RequestedTheme.ToString());
            }

        }

        /// <summary>
        /// 获取或设置（使用 LocalSettings 持久性）根元素的请求主题。
        /// </summary>
        public static ElementTheme RootTheme
        {
            get
            {
                if (currentApplicationWindow.Content is FrameworkElement rootElement)
                {
                    return rootElement.RequestedTheme;
                }

                return ElementTheme.Default;
            }
            set
            {
                if (currentApplicationWindow.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = value;
                }
            }
        }


        public static SystemBackdrop Material
        {
            get => App.StartupWindow.SystemBackdrop;
            set
            {
                if (value is SystemBackdrop backdrop)
                {
                    App.StartupWindow.SystemBackdrop = backdrop;
                }
            }
        }

        public static bool IsDarkTheme()
        {
            if (RootTheme == ElementTheme.Default)
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            return RootTheme == ElementTheme.Dark;
        }

    }
}
