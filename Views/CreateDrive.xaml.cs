using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Views
{
    public sealed partial class CreateDrive : ContentDialog
    {
        public CreateDrive()
        {
            InitializeComponent();
            PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try { args.Cancel = !await ((CreateDriveViewModel)DataContext).CreateAsync(); }
                finally { deferral.Complete(); }
            };
            CloseButtonClick += (_, args) =>
            {
                var model = (CreateDriveViewModel)DataContext;
                if (model.IsBusy) { args.Cancel = true; model.Cancel(); }
            };
            Closing += (_, _) => ((CreateDriveViewModel)DataContext).Cancel();
        }
    }
}