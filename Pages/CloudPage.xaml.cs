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
        private bool _managing;
        private AccountLifecycleService Lifecycle => Ioc.Default.GetRequiredService<AccountLifecycleService>();
        public CloudPage()
        {
            InitializeComponent();
            DataContext = new CloudViewModel();
            Loaded += async (_, _) =>
            {
                Lifecycle.Access.Changed -= AccessChanged;
                Lifecycle.Access.Changed += AccessChanged;
                await Model.LoadDrivesFromDisk();
                Model.HasPendingCleanup = Lifecycle.Access.Pending.Count > 0;
                if (Model.HasPendingCleanup) Model.ManagementMessage = "AccountLifecycle_Pending".GetLocalized();
                LoadCapacities();
            };
            Unloaded += (_, _) => Lifecycle.Access.Changed -= AccessChanged;
        }

        private void AccessChanged(AccountScope scope)
        {
            if (!Lifecycle.Access.IsBlocked(scope.AccountId, scope.DriveId)) Model.ManagementMessage = "";
        }

        // Silent only: a drive that needs sign-in reports that on its card instead of prompting.
        private void LoadCapacities()
        {
            foreach (var drive in Model.Drives.Where(d => !d.IsSignedOut && string.IsNullOrEmpty(d.StorageInfo) && !d.GetCapacityCommand.IsRunning))
                drive.GetCapacityCommand.Execute(null);
        }

        private async void ShowCreateDriveDialogAsync(object sender, RoutedEventArgs e)
        {
            if (_managing) return;
            await new CreateDrive { XamlRoot = XamlRoot, DataContext = new CreateDriveViewModel(Model) }.ShowAsync();
            LoadCapacities();
        }

        private void OpenDrive(DriveViewModel drive)
        {
            if (!_managing && drive != null) (App.StartupWindow as MainWindow)?.Navigate(typeof(DrivePage), drive);
        }
        // Raised for a click and for Enter or Space on the focused card.
        private void OpenClicked(object sender, ItemClickEventArgs e) => OpenDrive(e.ClickedItem as DriveViewModel);
        private void OpenFromMenu(object sender, RoutedEventArgs e) => OpenDrive((sender as FrameworkElement)?.DataContext as DriveViewModel);
        private async void Retry(object sender, RoutedEventArgs e)
        {
            if (_managing) return;
            await RunManagementAsync(() => Lifecycle.RetryPendingAsync(), "AccountLifecycle_RetryDone");
        }

        private async void SignOutAccount(object sender, RoutedEventArgs e) => await ConfirmManagementAsync(sender, false);
        private async void RemoveDrive(object sender, RoutedEventArgs e) => await ConfirmManagementAsync(sender, true);

        private async Task ConfirmManagementAsync(object sender, bool remove)
        {
            if (_managing || (sender as FrameworkElement)?.DataContext is not DriveViewModel drive) return;
            _managing = true;
            try
            {
                var scope = new AccountScope(drive.Provider.HomeAccountId, remove ? drive.Provider.DriveId : null);
                var sync = Ioc.Default.GetRequiredService<FolderSyncService>();
                await sync.InitializeAsync();
                int syncCount = sync.Jobs.Count(x => scope.Contains(x.Binding.AccountId, x.Binding.DriveId));
                string bookmarkCount;
                try
                {
                    var bookmarks = await Ioc.Default.GetRequiredService<IBookmarkStore>().LoadAsync();
                    bookmarkCount = bookmarks.Count(x => scope.Contains(x.AccountId, x.DriveId)).ToString();
                }
                catch { bookmarkCount = "AccountLifecycle_UnknownCount".GetLocalized(); }
                int driveCount = Model.Drives.Count(x => scope.Contains(x.Provider.HomeAccountId, x.Provider.DriveId));
                var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
                panel.Children.Add(new TextBlock { Text = string.Format((remove ? "AccountLifecycle_RemoveExplanation" : "AccountLifecycle_SignOutExplanation").GetLocalized(),
                    drive.DisplayName, driveCount, syncCount, bookmarkCount), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "AccountLifecycle_FileNotice".GetLocalized(), TextWrapping = TextWrapping.Wrap });
                var deleteBookmarks = new CheckBox { Content = "AccountLifecycle_DeleteBookmarks".GetLocalized(), IsChecked = false };
                if (remove) panel.Children.Add(deleteBookmarks);
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = (remove ? "AccountLifecycle_RemoveTitle" : "AccountLifecycle_SignOutTitle").GetLocalized(),
                    Content = panel,
                    PrimaryButtonText = (remove ? "AccountLifecycle_RemoveButton" : "AccountLifecycle_SignOutButton").GetLocalized(),
                    CloseButtonText = "Account_Cancel".GetLocalized(), DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                await RunManagementAsync(() => Lifecycle.ExecuteAsync(scope, remove && deleteBookmarks.IsChecked == true),
                    remove ? "AccountLifecycle_Removed" : "AccountLifecycle_SignedOutDone");
            }
            catch (Exception error) { Model.ErrorMessage = AccountConfigurationErrors.Message(error); }
            finally { _managing = false; }
        }

        private async Task RunManagementAsync(Func<Task> action, string successKey)
        {
            _managing = true;
            AccountPanel.IsHitTestVisible = false;
            DriveList.IsEnabled = false;
            string message;
            try { await action(); message = successKey.GetLocalized(); }
            catch (AccountLifecycleException error)
            {
                message = ("AccountLifecycle_Failed" + error.Stage).GetLocalized();
                if (Lifecycle.Access.Pending.Count > 0) message += " " + "AccountLifecycle_Pending".GetLocalized();
            }
            catch (Exception error) { message = AccountConfigurationErrors.Message(error); }
            finally { AccountPanel.IsHitTestVisible = true; DriveList.IsEnabled = true; _managing = false; }
            await Model.LoadDrivesFromDisk();
            Model.ManagementMessage = message;
            Model.HasPendingCleanup = Lifecycle.Access.Pending.Count > 0;
            LoadCapacities();
        }
        private async void RestoreBackup(object sender, RoutedEventArgs e) => await RecoverAsync(() => Ioc.Default.GetService<DriveConfigurationStore>().RestoreBackupAsync());

        private async Task RecoverAsync(Func<Task> action)
        {
            if (_managing) return;
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
