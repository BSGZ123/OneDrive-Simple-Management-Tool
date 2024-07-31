using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Graph.Models;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public class FileViewModel : ObservableObject
    {
        public FileViewModel(DriveViewModel drive,DriveItem file,bool loadThumbnail=false) 
        {
            Drive = drive;
            _file = file;
            ItemType = IsFile ? "File" : "Folder";
        }

        private readonly DriveItem _file;


        public string Id { get => _file.Id; }
        public string Name { get => _file.Name; }
        public long? Size { get => _file.Size; }
        public bool IsFile { get => _file.Folder == null; }
        public bool IsFolder { get => !IsFile; }
        public DriveViewModel Drive { get; }
        public string ItemType { get; }
    }
}