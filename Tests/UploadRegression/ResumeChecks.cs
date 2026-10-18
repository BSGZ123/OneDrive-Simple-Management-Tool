using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static async Task DurableEmptyPriority()
    {
        await using var fixture = new UploadFixture();
        using var first = await TransferScheduler.Shared.EnterAsync(default);
        using var second = await TransferScheduler.Shared.EnterAsync(default);
        using var third = await TransferScheduler.Shared.EnterAsync(default);
        var low = fixture.Provider.UploadFileResumableAsync(new StorageFile("low.bin", 0), "root", new(), () => { }, null, default, null, new() { Priority = 0 });
        var high = fixture.Provider.UploadFileResumableAsync(new StorageFile("high.bin", 0), "root", new(), () => { }, null, default, null, new() { Priority = 2 });
        Assert(!low.IsCompleted && !high.IsCompleted, "Empty uploads bypassed the scheduler.");
        third.Dispose();
        await Task.WhenAll(low, high).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(fixture.Metadata.Requests.First(x => x.Method == "PUT").Path.Contains("high.bin"), "Empty uploads lost their task priority.");
    }

    private static async Task ControlledFolderUpload()
    {
        await using var fixture = new UploadFixture();
        fixture.Provider.IsAuthenticated = true;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudflow-rate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int size = 128 * 1024;
            foreach (string name in new[] { "one.bin", "two.bin" }) await File.WriteAllBytesAsync(System.IO.Path.Combine(directory, name), new byte[size]);
            var record = new TransferRecord { Name = "folder", Path = directory, IsFolder = true, ItemId = "root", Direction = TransferDirection.Upload };
            record.Uploads.Add(new() { Key = ".", Name = "folder", IsFolder = true, Enumerated = true, State = TransferEntryState.Completed, TargetId = "folder-id" });
            foreach (string name in new[] { "one.bin", "two.bin" }) record.Uploads.Add(new() { Key = name, Name = name, ParentKey = ".", Size = size });
            var policy = new TransferPolicy { BytesPerSecond = 128 * 1024 };
            var source = new StorageUploadRecordSource(fixture.Provider, record, null, () => { }, policy);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await new UploadRecordSession(record, source, () => { }, () => { }, _ => "error").RunAsync(null, default);
            Assert(record.Uploads.All(x => x.State == TransferEntryState.Completed), "Controlled folder upload failed.");
            Assert(policy.TransferredBytes == 2 * size && timer.Elapsed.TotalSeconds >= 1.75,
                "Durable folder children did not share their task's byte limit.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task DurableGraphUpload()
    {
        await using var fixture = new UploadFixture();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudflow-source-" + Guid.NewGuid().ToString("N"));
        int length = ResumableUploadSession.SliceSize + 1234;
        await File.WriteAllBytesAsync(path, new byte[length]);
        try
        {
            var source = new StorageFile("source.bin", length) { LocalPath = path };
            var entry = new UploadEntryRecord { Key = ".", Name = "source.bin" };
            using var stop = new CancellationTokenSource();
            try
            {
                await fixture.Provider.UploadFileResumableAsync(source, "root", entry,
                    () => { if (entry.Resume?.Bytes > 0) stop.Cancel(); }, null, stop.Token, null);
                throw new Exception("Upload ignored stop.");
            }
            catch (OperationCanceledException) { }
            Assert(entry.Resume?.Bytes == ResumableUploadSession.SliceSize && fixture.Server.Deletes.Count == 0,
                "Stopping deleted or forgot the resumable session.");
            fixture.Server.ResumeOffset = entry.Resume.Bytes;
            var outcome = await fixture.Provider.UploadFileResumableAsync(source, "root", entry, () => { }, null, default, null);
            Assert(outcome.TargetId == "uploaded-item" && entry.Resume == null && fixture.Metadata.Sessions == 1,
                "Resuming created a new session or lost confirmation.");
            Assert(fixture.Server.Headers.Count == 2 && fixture.Server.Headers.Last()["Content-Range"].StartsWith($"bytes {ResumableUploadSession.SliceSize}-"),
                "Graph adapter didn't resume the existing session range.");
            Assert(fixture.Server.Headers.All(x => !x.ContainsKey("Authorization")), "A durable upload sent a bearer token to its session.");
        }
        finally { File.Delete(path); }
    }
}
