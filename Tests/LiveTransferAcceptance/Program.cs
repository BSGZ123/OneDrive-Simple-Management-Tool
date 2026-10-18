using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("prepare/proxy/inspect/verify <test-directory> [source]");
string command = args[0];
string directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
switch (command)
{
    case "seed-upload":
    case "seed-download":
        try { await LiveSetup.SeedAsync(command, directory, args[2], args[3], args[4]); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Live setup failed: {exception.GetType().Name}");
            if (exception is AccountAuthenticationException auth) Console.Error.WriteLine(auth.Failure);
            Environment.ExitCode = 1;
        }
        break;
    case "prepare":
        string source = Path.Combine(directory, "CF-Acceptance-256MiB.bin");
        if (File.Exists(source)) throw new IOException("Test source already exists.");
        await using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] block = new byte[1024 * 1024];
            for (int index = 0; index < 256; index++)
            {
                RandomNumberGenerator.Fill(block);
                await output.WriteAsync(block);
            }
            await output.WriteAsync(block.AsMemory(0, 123));
        }
        await using (var input = File.OpenRead(source))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
            await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), JsonSerializer.Serialize(new { Bytes = input.Length, Sha256 = hash }));
            Console.WriteLine(JsonSerializer.Serialize(new { Bytes = input.Length, Sha256 = hash }));
        }
        break;
    case "proxy":
        await new ConnectionProxy(directory).RunAsync();
        break;
    case "inspect":
        // Load an encrypted snapshot, never mutate the application's live store.
        string snapshot = Path.Combine(directory, "snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        foreach (string file in Directory.EnumerateFiles(args[2], "*.dat*"))
        {
            if (file.EndsWith(".dat") || file.EndsWith(".dat.journal")) File.Copy(file, Path.Combine(snapshot, Path.GetFileName(file)));
        }
        var records = new TransferStore(snapshot).Load().Select(record => new
        {
            record.Id,
            Direction = record.Direction.ToString(),
            State = record.Direction == TransferDirection.Upload ? record.UploadState.ToString() : record.DownloadState.ToString(),
            Bytes = record.Direction == TransferDirection.Upload
                ? record.Uploads.Where(x => !x.IsFolder).Sum(x => x.State == TransferEntryState.Completed ? x.Size : x.Resume?.Bytes ?? 0)
                : record.IsFolder ? record.Downloads.Sum(x => x.Completed ? x.Item.Size : x.Resume?.Bytes ?? 0)
                : record.DownloadState == DownloadTaskState.Completed ? record.Size : record.DownloadResume?.Bytes ?? 0,
            Size = record.Direction == TransferDirection.Upload ? record.Uploads.Sum(x => x.Size) : record.Size,
            UploadFiles = record.Uploads.Where(x => !x.IsFolder).Select(x => new { State = x.State.ToString(), HasSession = x.Resume != null,
                Bytes = x.Resume?.Bytes, CommitPending = x.Resume?.CommitPending, Error = x.ErrorKey }).ToArray(),
            Failure = record.DownloadFailure.ToString()
        }).ToArray();
        string report = JsonSerializer.Serialize(records);
        await File.WriteAllTextAsync(Path.Combine(directory, "latest-snapshot.json"), report);
        Console.WriteLine(report);
        break;
    case "verify":
        var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "source.json")));
        await using (var input = File.OpenRead(args[2]))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
            bool matches = hash == expected.RootElement.GetProperty("Sha256").GetString() && input.Length == expected.RootElement.GetProperty("Bytes").GetInt64();
            Console.WriteLine(JsonSerializer.Serialize(new { Bytes = input.Length, Sha256 = hash, Matches = matches }));
            if (!matches) Environment.ExitCode = 1;
        }
        break;
    default: throw new ArgumentException("Unknown test command.");
}
