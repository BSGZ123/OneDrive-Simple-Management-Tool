using Microsoft.Identity.Client;
using Microsoft.Graph;
using Microsoft.Identity.Client.Extensions.Msal;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using System.Diagnostics;
using System.Text.Json;

internal static class LiveSetup
{
    public static async Task SeedAsync(string command, string directory, string applicationData, string settingsPath, string folderName)
    {
        if (Process.GetProcessesByName("OneDrive Simple Management Tool").Length != 0)
            throw new InvalidOperationException("Close the application before preparing its test record.");
        if (!folderName.StartsWith("CF-Acceptance-", StringComparison.Ordinal)) throw new ArgumentException();
        var config = new ProtectedConfigurationFile<List<DriveDTO>>(Path.Combine(applicationData, "Configuration", "drives.dat"), "drives",
            DriveDTOSourceGenerationContext.Default.ListDriveDTO, _ => { }, 4 * 1024 * 1024);
        var drives = await config.ReadAsync(default);
        if (drives?.Data.Count != 1) throw new InvalidOperationException("Exactly one configured drive is required.");
        var identity = drives.Data.Single().Provider;
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        string clientId = settings.RootElement.GetProperty("AzureAD").GetProperty("ClientId").GetString();
        var app = PublicClientApplicationBuilder.Create(clientId).WithRedirectUri("http://localhost").Build();
        string cacheDirectory = Path.Combine(directory, "authentication");
        Directory.CreateDirectory(cacheDirectory);
        string copy = Path.Combine(cacheDirectory, "cache.bin");
        if (!File.Exists(copy)) File.Copy(Path.Combine(applicationData, "Authentication", "CloudFlowTokenCache.bin"), copy);
        var logger = new TraceSource("LiveAcceptanceCache", SourceLevels.Off);
        logger.Listeners.Clear();
        var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("cache.bin", cacheDirectory).Build(), logger);
        cache.RegisterCache(app.UserTokenCache);
        try
        {
            var authentication = new MsalAccountAuthenticationService(app);
            using var client = GraphAccountDriveResolver.CreateClient(authentication, identity.HomeAccountId, identity.DriveId);
            var drive = await client.Drives[identity.DriveId].GetAsync();
            if (drive.Quota?.Remaining < 512L * 1024 * 1024) throw new IOException("Insufficient test quota.");
            var folder = await client.Drives[identity.DriveId].Items["root"].ItemWithPath(folderName).GetAsync();
            if (folder?.Folder == null || folder.Name != folderName) throw new InvalidDataException();
            string name = "CF-Acceptance-256MiB.bin";
            var store = new TransferStore(Path.Combine(applicationData, "Transfers"));
            var records = store.Load();
            var direction = command == "seed-upload" ? TransferDirection.Upload : TransferDirection.Download;
            if (records.Any(x => x.Direction == direction && x.Name == name)) throw new InvalidOperationException("Test record already exists.");
            var record = new TransferRecord { Direction = direction, Name = name, AccountId = identity.HomeAccountId, DriveId = identity.DriveId,
                ItemId = folder.Id, Path = Path.Combine(directory, name), SpeedLimitKiB = 2048 };
            if (direction == TransferDirection.Upload)
            {
                if (!File.Exists(record.Path)) throw new FileNotFoundException();
                record.Size = new FileInfo(record.Path).Length;
            }
            else
            {
                var item = await client.Drives[identity.DriveId].Items[folder.Id].ItemWithPath(name).GetAsync();
                if (item?.File == null || item.Size != new FileInfo(record.Path).Length) throw new InvalidDataException();
                Directory.CreateDirectory(Path.Combine(directory, "download"));
                record.ItemId = item.Id; record.Size = item.Size.Value;
                record.Path = Path.Combine(directory, "download", name);
            }
            store.Save(record);
            Console.WriteLine(JsonSerializer.Serialize(new { record.Id, Direction = direction.ToString(), record.Size, FreeBytes = drive.Quota?.Remaining }));
        }
        finally { cache.UnregisterCache(app.UserTokenCache); }
    }
}
