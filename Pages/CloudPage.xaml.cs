using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.Views;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class CloudPage : Page
    {
        private CloudViewModel Model => (CloudViewModel)DataContext;
        public CloudPage()
        {
            InitializeComponent();
            DataContext = new CloudViewModel();
            Loaded += async (_, _) => { await Model.LoadDrivesFromDisk(); LoadCapacities(); };
        }

        // Silent only: a drive that needs sign-in reports that on its card instead of prompting.
        private void LoadCapacities()
        {
            foreach (var drive in Model.Drives.Where(d => string.IsNullOrEmpty(d.StorageInfo) && !d.GetCapacityCommand.IsRunning))
                drive.GetCapacityCommand.Execute(null);
        }

        private async void ShowCreateDriveDialogAsync(object sender, RoutedEventArgs e)
        {
            await new CreateDrive { XamlRoot = XamlRoot, DataContext = new CreateDriveViewModel(Model) }.ShowAsync();
            LoadCapacities();
        }

        private void OpenDrive(DriveViewModel drive)
        {
            if (drive != null) (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage), drive);
        }
        // Raised for a click and for Enter or Space on the focused card.
        private void OpenClicked(object sender, ItemClickEventArgs e) => OpenDrive(e.ClickedItem as DriveViewModel);
        private void OpenFromMenu(object sender, RoutedEventArgs e) => OpenDrive((sender as FrameworkElement)?.DataContext as DriveViewModel);
        private async void Retry(object sender, RoutedEventArgs e) { await Model.LoadDrivesFromDisk(); LoadCapacities(); }
        private async void RestoreBackup(object sender, RoutedEventArgs e) => await RecoverAsync(() => Ioc.Default.GetService<DriveConfigurationStore>().RestoreBackupAsync());

        private async Task RecoverAsync(Func<Task> action)
        {
            try { await action(); await Model.LoadDrivesFromDisk(); LoadCapacities(); }
            catch (Exception exception) { Model.ErrorMessage = AccountConfigurationErrors.Message(exception); }
        }

        private async void Rebuild(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Configuration_RebuildTitle".GetLocalized(),
                Content = "Configuration_RebuildExplanation".GetLocalized(),
                PrimaryButtonText = "Configuration_Rebuild".GetLocalized(), CloseButtonText = "Account_Cancel".GetLocalized(),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await RecoverAsync(() => Ioc.Default.GetService<DriveConfigurationStore>().RebuildAsync());
        }

        private async void RepairDuplicates(object sender, RoutedEventArgs e)
        {
            try
            {
                var store = Ioc.Default.GetService<DriveConfigurationStore>();
                var original = await store.ReadDuplicateCandidatesAsync();
                var retained = new List<int>();
                var choices = new List<(ComboBox Control, List<int> Indices)>();
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = "Configuration_DuplicateExplanation".GetLocalized(), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
                foreach (var group in original.Data.Select((drive, index) => (drive, index)).GroupBy(item => DriveConfigurationStore.Identity(item.drive)))
                {
                    if (group.Count() == 1) { retained.Add(group.Single().index); continue; }
                    var items = group.ToList();
                    var combo = new ComboBox { PlaceholderText = "Configuration_ChooseRecord".GetLocalized(), MinWidth = 280 };
                    foreach (var item in items) combo.Items.Add($"{item.index + 1}. {item.drive.DisplayName ?? "Account_DefaultName".GetLocalized()}");
                    choices.Add((combo, items.Select(item => item.index).ToList()));
                    panel.Children.Add(combo);
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Configuration_Repair".GetLocalized(),
                    Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                    PrimaryButtonText = "Configuration_Repair".GetLocalized(), CloseButtonText = "Account_Cancel".GetLocalized(),
                    IsPrimaryButtonEnabled = false
                };
                foreach (var choice in choices) choice.Control.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.All(c => c.Control.SelectedIndex >= 0);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                retained.AddRange(choices.Select(c => c.Indices[c.Control.SelectedIndex]));
                await RecoverAsync(() => store.RepairDuplicatesAsync(original, retained));
            }
            catch (Exception exception) { Model.ErrorMessage = AccountConfigurationErrors.Message(exception); }
        }
    }
}
