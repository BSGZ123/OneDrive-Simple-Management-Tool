using Microsoft.UI.Dispatching;

// Only WinUI controls are simulated. Tests execute the production prompt coordinator.
namespace Microsoft.UI.Xaml
{
    public enum HorizontalAlignment { Stretch }
    public enum TextWrapping { Wrap }
    public class XamlRoot { }
    public class FrameworkElement
    {
        public XamlRoot XamlRoot { get; set; } = new();
        public DispatcherQueue DispatcherQueue => DispatcherQueue.GetForCurrentThread();
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public enum ContentDialogButton { Close, Primary }
    public enum ContentDialogResult { None, Primary }
    public class ComboBoxItem { public object Content { get; set; } public object Tag { get; set; } }
    public class ComboBox
    {
        public string PlaceholderText { get; set; }
        public Microsoft.UI.Xaml.HorizontalAlignment HorizontalAlignment { get; set; }
        public List<object> Items { get; } = [];
        public object SelectedItem { get; private set; }
        public event EventHandler SelectionChanged;
        public void Select(int index) { SelectedItem = Items[index]; SelectionChanged?.Invoke(this, EventArgs.Empty); }
    }
    public class CheckBox { public object Content { get; set; } public bool? IsChecked { get; set; } }
    public class StackPanel { public int Spacing { get; set; } public int MaxWidth { get; set; } public List<object> Children { get; } = []; }
    public class TextBlock { public string Text { get; set; } public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; } public int MaxWidth { get; set; } }
    public class ContentDialog
    {
        private TaskCompletionSource<ContentDialogResult> _result;
        public static ContentDialog Active { get; private set; }
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; set; }
        public string Title { get; set; }
        public object Content { get; set; }
        public string PrimaryButtonText { get; set; }
        public string CloseButtonText { get; set; }
        public ContentDialogButton DefaultButton { get; set; }
        public bool IsPrimaryButtonEnabled { get; set; } = true;
        public Task<ContentDialogResult> ShowAsync()
        {
            if (Active != null) throw new InvalidOperationException("Two dialogs opened together.");
            Active = this;
            _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _result.Task;
        }
        public void Hide() => Respond(ContentDialogResult.None);
        public void Respond(ContentDialogResult result)
        {
            if (_result?.Task.IsCompleted != false) return;
            if (result == ContentDialogResult.Primary && !IsPrimaryButtonEnabled) throw new InvalidOperationException("Disabled primary action.");
            if (Active == this) Active = null;
            _result.SetResult(result);
        }
    }
}

namespace OneDrive_Simple_Management_Tool
{
    public static class App { public static MainWindow StartupWindow { get; } = new(); }
    public class MainWindow
    {
        public object Content { get; } = new Microsoft.UI.Xaml.FrameworkElement();
        public string Notice { get; private set; }
        public void ShowStartupNotice(string message) => Notice = message;
    }
}

namespace OneDrive_Simple_Management_Tool.Views
{
    public static class FileActions
    {
        public static Task ExistingDialog { get; set; } = Task.CompletedTask;
        public static Task WaitForDialogAsync(Microsoft.UI.Xaml.XamlRoot root) => ExistingDialog;
    }
}
