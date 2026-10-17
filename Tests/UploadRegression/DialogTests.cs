using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Views;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static (string, Func<Task>)[] DialogCases() =>
    [
        ("Conflict UI requires a selection and defaults to cancellation", () => TestUiContext.Run(ExplicitDialogChoice)),
        ("Cancel exit restores an interrupted conflict prompt", () => TestUiContext.Run(RestoreConflictPrompt)),
        ("Confirmed exit cancels interrupted and queued conflict prompts", () => TestUiContext.Run(CancelExitPrompts)),
        ("Conflict prompts wait for existing file dialogs", () => TestUiContext.Run(WaitForExistingDialog))
    ];

    private static async Task<ContentDialog> WaitForDialog(Func<ContentDialog, bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ContentDialog.Active == null || !predicate(ContentDialog.Active)) await Task.Delay(1, stop.Token);
        return ContentDialog.Active;
    }

    private static async Task ExplicitDialogChoice()
    {
        var pending = TransferDialogs.ResolveAsync(new("existing.txt", false), default);
        var dialog = await WaitForDialog(d => d.Content is StackPanel);
        Assert(!dialog.IsPrimaryButtonEnabled && dialog.DefaultButton == ContentDialogButton.Close, "UI preselected an overwrite.");
        var content = (StackPanel)dialog.Content;
        var choices = content.Children.OfType<ComboBox>().Single();
        Assert(choices.Items.Count == 3, "Missing conflict choices.");
        choices.Select(2);
        content.Children.OfType<CheckBox>().Single().IsChecked = true;
        dialog.Respond(ContentDialogResult.Primary);
        var choice = await pending;
        Assert(choice.Choice == UploadConflictChoice.KeepBoth && choice.ApplyToBatch && !TransferDialogs.IsOpen, "UI did not return the explicit choice.");
    }

    private static async Task RestoreConflictPrompt()
    {
        var pending = TransferDialogs.ResolveAsync(new("existing.txt", false), default);
        var first = await WaitForDialog(d => d.Content is StackPanel);
        var exit = TransferDialogs.ConfirmExitAsync(App.StartupWindow, 1, 2);
        var confirm = await WaitForDialog(d => d.Content is TextBlock);
        Assert(confirm.DefaultButton == ContentDialogButton.Close && !pending.IsCompleted, "Exit discarded a pending decision or defaulted to stopping.");
        confirm.Hide();
        Assert(!await exit, "Cancel exit was accepted as confirmation.");
        var restored = await WaitForDialog(d => d.Content is StackPanel);
        Assert(restored != first && !restored.IsPrimaryButtonEnabled, "Conflict prompt was not restored safely.");
        ((StackPanel)restored.Content).Children.OfType<ComboBox>().Single().Select(1);
        restored.Respond(ContentDialogResult.Primary);
        Assert((await pending).Choice == UploadConflictChoice.Skip, "Restored decision was lost.");
    }

    private static async Task CancelExitPrompts()
    {
        using var stop = new CancellationTokenSource();
        var first = TransferDialogs.ResolveAsync(new("first", false), stop.Token);
        await WaitForDialog(d => d.Content is StackPanel);
        var second = TransferDialogs.ResolveAsync(new("second", false), stop.Token);
        var exit = TransferDialogs.ConfirmExitAsync(App.StartupWindow, 2, 0);
        var confirm = await WaitForDialog(d => d.Content is TextBlock);
        confirm.Respond(ContentDialogResult.Primary);
        Assert(await exit, "Explicit stop was ignored.");
        stop.Cancel();
        foreach (var pending in new[] { first, second })
        {
            try { await pending; throw new Exception("Exit returned an unchosen upload decision."); }
            catch (OperationCanceledException) { }
        }
        Assert(ContentDialog.Active == null && !TransferDialogs.IsOpen, "Exit left an active or queued conflict dialog.");
    }

    private static async Task WaitForExistingDialog()
    {
        var existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileActions.ExistingDialog = existing.Task;
        try
        {
            var pending = TransferDialogs.ResolveAsync(new("first", false), default);
            Assert(ContentDialog.Active == null && TransferDialogs.IsOpen, "Conflict UI overlapped an existing file dialog or let another dialog overtake it.");
            existing.SetResult();
            var prompt = await WaitForDialog(d => d.Content is StackPanel);
            prompt.Hide();
            Assert((await pending).Choice == UploadConflictChoice.Cancel, "Dismissed prompt enabled a mutation.");
        }
        finally { FileActions.ExistingDialog = Task.CompletedTask; }
    }
}
