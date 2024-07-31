using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Models.DTO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CloudViewModel : ObservableObject
    {
        public CloudViewModel() 
        {
            Drives.CollectionChanged += async (sender, args) =>
            {
                await SaveDrivesToDisk();
            };
        }

        public DriveViewModel GetDrive(string name)
        {
            return Drives.FirstOrDefault(d => d.DisplayName == name);
        }


        private async Task SaveDrivesToDisk()
        {
            List<DriveDTO> drives = [];
            foreach (DriveViewModel drive in Drives)
            {
                DriveDTO driveDTO = new()
                {
                    DisplayName = drive.DisplayName,
                    Provider = new()
                    {
                        HomeAccountId = drive.Provider.HomeAccountId,
                        DriveId = drive.Provider.DriveId
                    }
                };
                drives.Add(driveDTO);
            }
            string jsonData = JsonSerializer.Serialize(drives, DriveDTOSourceGenerationContext.Default.ListDriveDTO);
            string cachePath = Path.Combine(Directory.GetCurrentDirectory(), "cache");
            Directory.CreateDirectory(cachePath);
            await File.WriteAllTextAsync(cacheFilePath, jsonData);
        }

        private readonly string cacheFilePath = Path.Combine(Directory.GetCurrentDirectory(), "cache", "drives.json");
        private bool isCacheLoaded = false;
        [ObservableProperty] private ObservableCollection<DriveViewModel> _drives = [];
    }
}