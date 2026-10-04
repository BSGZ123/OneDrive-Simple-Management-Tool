using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    public sealed partial class GridCloudView : UserControl
    {
        public GridCloudView()
        {
            this.InitializeComponent();
            Loaded += (_, _) => ScrollToSelection(Content, null);
        }

        private void ScrollToSelection(object sender, SelectionChangedEventArgs args)
        {
            var list = (GridView)sender;
            DispatcherQueue.TryEnqueue(() => { if (list.IsLoaded && list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem); });
        }
    }
}
