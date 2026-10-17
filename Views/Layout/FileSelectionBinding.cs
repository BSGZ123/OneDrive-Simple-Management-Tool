using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.ComponentModel;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Views.Layout
{
    internal sealed class FileSelectionBinding
    {
        private readonly ListViewBase _list;
        private DriveViewModel _drive;
        private bool _syncing;

        public FileSelectionBinding(ListViewBase list)
        {
            _list = list;
            list.Loaded += (_, _) => Connect();
            list.Unloaded += (_, _) => Disconnect();
            list.DataContextChanged += (_, _) => { if (list.IsLoaded) Connect(); };
            list.SelectionChanged += (_, args) =>
            {
                if (_syncing || _drive == null || _drive.IsUpdatingSelection) return;
                _drive.SetSelection(list.SelectedItems.Cast<FileViewModel>().ToArray(),
                    args.AddedItems.OfType<FileViewModel>().LastOrDefault() ?? _drive.SelectedItem);
            };
        }

        private void Connect()
        {
            Disconnect();
            _drive = _list.DataContext as DriveViewModel;
            if (_drive != null) _drive.PropertyChanged += Changed;
            Synchronize();
            ScrollToSelection();
        }

        private void Disconnect()
        {
            if (_drive != null) _drive.PropertyChanged -= Changed;
            _drive = null;
        }

        private void Changed(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(DriveViewModel.Files) or nameof(DriveViewModel.SelectedItems)) Synchronize();
            if (args.PropertyName is nameof(DriveViewModel.Layout) or nameof(DriveViewModel.IsLoading)) ScrollToSelection();
        }

        private void Synchronize()
        {
            _syncing = true;
            try
            {
                // Assigning the source here keeps reset-generated SelectionChanged
                // events inside the guard, even when both layouts are loaded.
                if (!ReferenceEquals(_list.ItemsSource, _drive?.Files)) _list.ItemsSource = _drive?.Files;
                if (_drive == null) return;
                var visible = _drive.Files.ToHashSet();
                var selected = _drive.SelectedItems.Where(visible.Contains).ToHashSet();
                if (selected.Count == 0) _list.SelectedItems.Clear();
                else if (selected.Count == _drive.Files.Count) _list.SelectAll();
                else
                {
                    foreach (var item in _list.SelectedItems.Cast<FileViewModel>().Where(item => !selected.Contains(item)).ToArray())
                        _list.SelectedItems.Remove(item);
                    var current = _list.SelectedItems.Cast<FileViewModel>().ToHashSet();
                    foreach (var item in selected.Where(item => !current.Contains(item))) _list.SelectedItems.Add(item);
                }
            }
            finally { _syncing = false; }
        }

        private void ScrollToSelection() => _list.DispatcherQueue.TryEnqueue(() =>
        {
            if (_list.IsLoaded && _drive?.SelectedItem is FileViewModel file && _list.Items.Contains(file)) _list.ScrollIntoView(file);
        });
    }
}
