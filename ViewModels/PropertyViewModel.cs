using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class PropertyViewModel : ObservableObject
    {
        public PropertyViewModel(FileViewModel file) 
        {
            _file = file;
        }

        private readonly FileViewModel _file; 
        public string Name => _file.Name;
        public long? Size => _file.Size;
        public DateTimeOffset? UpDated => _file.Updated;
        public bool IsFolder => _file.IsFolder;
        public int? ChildrenCount => _file.ChildrenCount;
    }
}