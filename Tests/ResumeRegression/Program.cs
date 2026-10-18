using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

if (args.Length > 0)
{
    string mode = args[0], directory = args[1], url = args[2];
    var store = new TransferStore(Path.Combine(directory, "records"));
    var record = store.Load().SingleOrDefault() ?? new TransferRecord
    {
        AccountId = "account", DriveId = "drive", ItemId = "item", Name = "data.bin",
        Direction = mode.StartsWith("download") ? TransferDirection.Download : TransferDirection.Upload,
        Path = Path.Combine(directory, "data.bin")
    };
    if (mode.StartsWith("download"))
    {
        store.Save(record);
        var session = new DownloadSession(record.Path, _ => Task.FromResult(new DownloadSource(url, long.Parse(args[3]), args[4])),
            new DownloadSessionOptions { StagingId = record.Id, RetryDelay = TimeSpan.Zero });
        session.AttachRecord(record, value =>
        {
            if (mode == "download-commit-crash" && value.DownloadState == DownloadTaskState.Completed) Environment.Exit(0);
            store.Save(value);
            if (mode == "download-crash" && value.DownloadResume?.Bytes > 0 && value.DownloadResume.Bytes < value.Size) Environment.Exit(0);
        }, true);
        if (mode == "download-cancel")
        {
            await session.CancelAsync();
            Check(session.Snapshot.State == DownloadTaskState.Cancelled, "Restored cancellation failed.");
        }
        else
        {
            await session.StartAsync();
            Check(session.Snapshot.State == DownloadTaskState.Completed, "Download did not complete: " + session.Snapshot.Failure);
        }
    }
    else
    {
        if (record.Uploads.Count == 0) record.Uploads.Add(new() { Key = ".", Name = "data.bin" });
        var entry = record.Uploads.Single();
        store.Save(record);
        using var http = new HttpClient();
        var upload = new ResumableUploadSession(entry, () =>
        {
            // Simulate the remote acknowledgement arriving just before the process dies,
            // leaving the disk checkpoint one fragment behind the server.
            if (mode == "upload-crash" && entry.Resume?.Bytes > 0) Environment.Exit(0);
            store.Save(record);
        }, async (_, token) =>
        {
            using var response = await http.PostAsync(url + "create", null, token);
            return (await response.Content.ReadFromJsonAsync<UploadSessionInfo>(cancellationToken: token))!;
        });
        await using var input = new FileStream(record.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await upload.RunAsync(input, "fail", null, default);
            Check(mode != "upload-unknown", "Unknown final response silently created another upload.");
            Check(entry.State == TransferEntryState.Completed && entry.Resume == null, "Upload completion wasn't durable.");
        }
        catch (UploadCompletionUnknownException) when (mode == "upload-unknown") { }
    }
    return;
}

string root = Path.Combine(Path.GetTempPath(), "cloudflow-resume-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await using var downloads = new LoopbackServer { Bytes = Data(8 * 1024 * 1024), DelayMilliseconds = 2 };
    foreach (string scenario in new[] { "resume", "corrupt", "changed", "no-range", "cancel", "commit" })
    {
        string folder = Path.Combine(root, "download-" + scenario);
        Directory.CreateDirectory(folder);
        await Child(scenario == "commit" ? "download-commit-crash" : "download-crash", folder, downloads.Url, downloads.Bytes.Length.ToString(), "v1");
        var record = new TransferStore(Path.Combine(folder, "records")).Load().Single();
        Check(record.DownloadResume?.Bytes > 0, "No byte checkpoint survived process exit.");
        string partial = Path.Combine(folder, $".cloudflow-{record.Id:N}.tmp");
        if (scenario == "resume")
        {
            // A buffered write can reach disk after the last committed checkpoint.
            using var tail = new FileStream(partial, FileMode.Append);
            tail.Write(new byte[123]);
        }
        if (scenario == "corrupt")
        {
            using var corrupt = new FileStream(partial, FileMode.Open, FileAccess.Write);
            corrupt.WriteByte(77);
        }
        downloads.IgnoreRange = scenario == "no-range";
        int requests = downloads.Offsets.Count;
        await Child(scenario == "cancel" ? "download-cancel" : "download-resume", folder,
            downloads.Url + "?refreshed=1", downloads.Bytes.Length.ToString(), scenario == "changed" ? "v2" : "v1");
        var offsets = downloads.Offsets.Skip(requests).ToArray();
        if (scenario == "cancel") Check(!File.Exists(partial), "Removing a restored task leaked its partial file.");
        else
        {
            Check(File.ReadAllBytes(record.Path).SequenceEqual(downloads.Bytes), "Resumed download content differs.");
            Check(!File.Exists(partial), "Completed download leaked staging data.");
            if (scenario == "resume") Check(offsets.First() == record.DownloadResume.Bytes, "Download did not resume from the committed offset.");
            if (scenario is "corrupt" or "changed" or "no-range") Check(offsets.Last() == 0, "Invalid resume didn't restart safely.");
            if (scenario == "commit") Check(offsets.Length == 0, "Crash after rename redownloaded the completed file.");
        }
        downloads.IgnoreRange = false;
        Console.WriteLine("PASS download across OS processes: " + scenario);
    }

    foreach (string scenario in new[] { "resume", "expired", "changed", "range-recovery" })
    {
        await using var server = new UploadServer();
        string folder = Path.Combine(root, "upload-" + scenario);
        Directory.CreateDirectory(folder);
        byte[] bytes = Data(8 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(folder, "data.bin"), bytes);
        await Child("upload-crash", folder, server.Url);
        var record = new TransferStore(Path.Combine(folder, "records")).Load().Single();
        Check(record.Uploads.Single().Resume.Bytes == 0 && server.Received == ResumableUploadSession.SliceSize,
            "Test didn't leave the server ahead of the saved checkpoint.");
        foreach (string path in Directory.GetFiles(Path.Combine(folder, "records")))
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(server.Url), "Session capability leaked as plaintext.");
        if (scenario == "expired") server.ExpireCurrent = true;
        if (scenario == "range-recovery") server.RejectNextRange = true;
        if (scenario == "changed")
        {
            bytes[123] ^= 255; // Same size; timestamps alone would not be sufficient.
            File.WriteAllBytes(record.Path, bytes);
        }
        int count = server.Offsets.Count;
        await Child("upload-resume", folder, server.Url);
        Check(server.CompletedBytes.SequenceEqual(bytes), "Uploaded content differs after resumption.");
        Check(server.Offsets.Skip(count).First() == (scenario is "expired" or "changed" ? 0 : ResumableUploadSession.SliceSize),
            "Upload ignored the server's offset or failed to restart an invalid session.");
        Check(server.Creates == (scenario is "expired" or "changed" ? 2 : 1), "Wrong session creation count.");
        Check(!server.SawAuthorization, "A bearer token was sent to a session URL.");
        Console.WriteLine("PASS upload across OS processes: " + scenario);
    }

    await using (var server = new UploadServer())
    {
        string folder = Path.Combine(root, "unknown-final"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "data.bin"), Data(8 * 1024 * 1024));
        await Child("upload-crash", folder, server.Url);
        var store = new TransferStore(Path.Combine(folder, "records"));
        var record = store.Load().Single();
        record.Uploads.Single().Resume = record.Uploads.Single().Resume with { CommitPending = true };
        store.Save(record);
        server.ExpireCurrent = true;
        await Child("upload-unknown", folder, server.Url);
        Check(server.Creates == 1, "Uncertain final response created a duplicate upload.");
        Console.WriteLine("PASS unknown final upload result is preserved for inspection without duplication");
    }
}
finally { Directory.Delete(root, true); }

static byte[] Data(int size) => Enumerable.Range(0, size).Select(i => (byte)(i * 31 + i / 251)).ToArray();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Child(params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
    Check(process.ExitCode == 0, "Child failed: " + arguments[0]);
}
