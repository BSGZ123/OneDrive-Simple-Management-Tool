using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
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

        public void AddDrive(DriveViewModel drive) 
        {
            Drives.Add(drive);
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

        //从磁盘上的缓存文件中加载云盘信息
        public async Task LoadDrivesFromDisk()
        {
            if (File.Exists(cacheFilePath) && !isCacheLoaded)
            {
                string jsonData = await File.ReadAllTextAsync(cacheFilePath);
                List<DriveDTO> drives = JsonSerializer.Deserialize(jsonData, DriveDTOSourceGenerationContext.Default.ListDriveDTO);
                foreach (DriveDTO drive in drives)
                {
                    OneDrive provider = new(drive.Provider.DriveId, drive.Provider.HomeAccountId);
                    Drives.Add(new DriveViewModel(provider, drive.DisplayName));
                }
            }
            isCacheLoaded = true;
        }

        private readonly string cacheFilePath = Path.Combine(Directory.GetCurrentDirectory(), "cache", "drives.json");
        private bool isCacheLoaded = false;
        [ObservableProperty] private ObservableCollection<DriveViewModel> _drives = [];
    }
}