using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Views
{
    internal static class TransferDialogs
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static ContentDialog _activeConflict;
        private static TaskCompletionSource _resumeConflicts;
        private static int _pendingConflicts;
        public static bool IsOpen => _pendingConflicts > 0 || _resumeConflicts != null;

        public static async Task<UploadConflictDecision> ResolveAsync(UploadConflict conflict, CancellationToken token)
        {
            _pendingConflicts++;
            try { return await ResolveCoreAsync(conflict, token); }
            finally { _pendingConflicts--; }
        }

        private static async Task<UploadConflictDecision> ResolveCoreAsync(UploadConflict conflict, CancellationToken token)
        {
            while (true)
            {
                if (_resumeConflicts != null) await _resumeConflicts.Task.WaitAsync(token);
                await Gate.WaitAsync(token);
                try
                {
                    if (_resumeConflicts != null) continue;
                    token.ThrowIfCancellationRequested();
                    var owner = (FrameworkElement)App.StartupWindow.Content;
                    await FileActions.WaitForDialogAsync(owner.XamlRoot).WaitAsync(token);
                    if (_resumeConflicts != null) continue;
                    token.ThrowIfCancellationRequested();
                    var choices = new ComboBox { PlaceholderText = "Upload_Choose".GetLocalized(), HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (var choice in new[] { UploadConflictChoice.Replace, UploadConflictChoice.Skip, UploadConflictChoice.KeepBoth })
                        choices.Items.Add(new ComboBoxItem { Content = ($"Upload_Choice{choice}").GetLocalized(), Tag = choice });
                    var apply = new CheckBox { Content = "Upload_ApplyBatch".GetLocalized() };
                    var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
                    content.Children.Add(new TextBlock { Text = string.Format("Upload_ConflictMessage".GetLocalized(), conflict.Name), TextWrapping = TextWrapping.Wrap });
                    content.Children.Add(new TextBlock { Text = "Upload_FolderMerge".GetLocalized(), TextWrapping = TextWrapping.Wrap });
                    content.Children.Add(choices);
                    content.Children.Add(apply);
                    var dialog = new ContentDialog
                    {
                        XamlRoot = owner.XamlRoot,
                        Title = "Upload_ConflictTitle".GetLocalized(),
                        Content = content,
                        PrimaryButtonText = "Upload_ConfirmChoice".GetLocalized(),
                        CloseButtonText = "Upload_CancelChoice".GetLocalized(),
                        DefaultButton = ContentDialogButton.Close,
                        IsPrimaryButtonEnabled = false
                    };
                    choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem != null;
                    _activeConflict = dialog;
                    using var registration = token.Register(() => owner.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
                    var result = await dialog.ShowAsync();
                    token.ThrowIfCancellationRequested();
                    // Closing the window temporarily hides a conflict prompt. Cancel exit restores it.
                    if (_resumeConflicts != null) continue;
                    return result == ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem selected
                        ? new((UploadConflictChoice)selected.Tag, apply.IsChecked == true)
                        : new(UploadConflictChoice.Cancel);
                }
                finally
                {
                    _activeConflict = null;
                    Gate.Release();
                }
            }
        }

        public static async Task<bool> ConfirmExitAsync(MainWindow window, int uploads, int downloads)
        {
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeConflicts = resume;
            _activeConflict?.Hide();
            try
            {
                await Gate.WaitAsync();
                try
                {
                    var owner = (FrameworkElement)window.Content;
                    await FileActions.WaitForDialogAsync(owner.XamlRoot);
                    var dialog = new ContentDialog
                    {
                        XamlRoot = owner.XamlRoot,
                        Title = "Transfer_ExitTitle".GetLocalized(),
                        Content = new TextBlock
                        {
                            Text = string.Format("Transfer_ExitMessage".GetLocalized(), uploads, downloads),
                            TextWrapping = TextWrapping.Wrap, MaxWidth = 480
                        },
                        PrimaryButtonText = "Transfer_StopAndExit".GetLocalized(),
                        CloseButtonText = "Transfer_CancelExit".GetLocalized(),
                        DefaultButton = ContentDialogButton.Close
                    };
                    bool confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
                    if (confirmed) window.ShowStartupNotice("Transfer_Stopping".GetLocalized());
                    return confirmed;
                }
                finally { Gate.Release(); }
            }
            finally
            {
                _resumeConflicts = null;
                resume.TrySetResult();
            }
        }
    }
}
