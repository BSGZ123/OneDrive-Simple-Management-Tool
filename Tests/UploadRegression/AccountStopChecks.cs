using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Net;

namespace OneDrive_Simple_Management_Tool.Tests.UploadRegression;

internal static partial class Program
{
    private static (string, Func<Task>)[] AccountStopCases() =>
    [
        ("Account stop retries failed saves until cleared sessions are durable", RetryDiscardSave),
        ("Account stop retains rejected sessions and retries deletion anonymously", RetryRejectedDiscard),
        ("Account stop retains timed-out sessions for a later retry", RetryTimedOutDiscard),
        ("Account stop accepts already missing or gone sessions", DiscardMissingSessions)
    ];

    private static UploadTaskViewModel FailedResumableTask(UploadFixture fixture)
    {
        var record = new TransferRecord
        {
            Direction = TransferDirection.Upload, Name = "failed.bin",
            Path = Path.Combine(Path.GetTempPath(), "failed.bin"), ItemId = "root",
            AccountId = "test-account", DriveId = "test-drive", UploadState = UploadTaskState.Failed
        };
        record.Uploads.Add(new()
        {
            Key = ".", Name = "failed.bin", Size = 10, State = TransferEntryState.Failed,
            ErrorKey = "FileOperation_Failed",
            Resume = new(fixture.Server.Url, DateTimeOffset.UtcNow.AddHours(1), 10, new string('A', 64), Bytes: 5)
        });
        var task = new UploadTaskViewModel(new DriveViewModel(fixture.Provider), "root", null);
        task.AttachRecord(record, _ => { }, true);
        return task;
    }

    private static void AssertFailedCardRetained(UploadTaskViewModel task)
    {
        Assert(task.HasFailed && task.Record.UploadState == UploadTaskState.Failed && task.CanRetry &&
            task.Record.Uploads.Single().ErrorKey == "FileOperation_Failed" && !Manager.Removed.Contains(task),
            "Session cleanup changed the failed card, its error or its retry action.");
    }

    private static async Task ExpectDiscardFailure<T>(UploadTaskViewModel task) where T : Exception
    {
        try { await task.DiscardResumeAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T) { return; }
        throw new Exception("Account stop reported success before cleanup finished.");
    }

    private static async Task RetryDiscardSave()
    {
        await using var fixture = new UploadFixture();
        var task = FailedResumableTask(fixture);
        string directory = Path.Combine(Path.GetTempPath(), "cloudflow-discard-" + Guid.NewGuid().ToString("N"));
        var store = new TransferStore(directory);
        int saves = 0;
        try
        {
            store.Save(task.Record);
            Manager.OnSaveRequired = record => { saves++; store.Save(record); };
            string journal = Path.Combine(directory, task.Record.Id.ToString("N") + ".dat.journal");
            await TestUiContext.Run(async () =>
            {
                using (var held = new FileStream(journal, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    await ExpectDiscardFailure<IOException>(task);
                    Assert(task.Record.Uploads.Single().Resume == null, "Confirmed deletion was not retained in memory.");
                    await ExpectDiscardFailure<IOException>(task);
                }
                Assert(new TransferStore(directory).Load().Single().Uploads.Single().Resume != null,
                    "The failed checkpoint unexpectedly changed the saved record.");
                await task.DiscardResumeAsync();
                Assert(saves == 3 && fixture.Server.Deletes.Count == 1, "Save retry skipped persistence or repeated remote deletion.");
                var restored = new TransferStore(directory).Load().Single();
                Assert(restored.Uploads.Single().Resume == null && restored.UploadState == UploadTaskState.Failed &&
                    restored.Uploads.Single().ErrorKey == "FileOperation_Failed", "Cleared sessions or failure details did not survive reload.");
                AssertFailedCardRetained(task);
            });
        }
        finally
        {
            Manager.OnSaveRequired = null;
            task.Controls.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static async Task RetryRejectedDiscard()
    {
        foreach (int status in new[] { 403, 429, 503 })
        {
            await using var fixture = new UploadFixture();
            fixture.Server.DeleteStatusCode = status;
            var task = FailedResumableTask(fixture);
            var resume = task.Record.Uploads.Single().Resume;
            int saves = 0;
            Manager.OnSaveRequired = _ => saves++;
            try
            {
                await TestUiContext.Run(async () =>
                {
                    await ExpectDiscardFailure<HttpRequestException>(task);
                    Assert(task.Record.Uploads.Single().Resume == resume && saves == 0,
                        $"HTTP {status} discarded the only retry address or saved a false success.");
                    AssertFailedCardRetained(task);
                    fixture.Server.DeleteStatusCode = (int)HttpStatusCode.NoContent;
                    await task.DiscardResumeAsync();
                    await task.DiscardResumeAsync();
                    Assert(task.Record.Uploads.Single().Resume == null && saves > 0 && fixture.Server.Deletes.Count == 2,
                        "Remote cleanup did not retry once or was repeated after success.");
                    Assert(fixture.Server.Deletes.All(headers => !headers.ContainsKey("Authorization")),
                        "Session cleanup leaked account authorization.");
                    AssertFailedCardRetained(task);
                });
            }
            finally { Manager.OnSaveRequired = null; task.Controls.Dispose(); }
        }
    }

    private static async Task RetryTimedOutDiscard()
    {
        await using var fixture = new UploadFixture();
        fixture.Server.AllowDisconnects = true;
        fixture.Server.BeforeDeleteResponse = token => Task.Delay(Timeout.Infinite, token);
        var task = FailedResumableTask(fixture);
        var resume = task.Record.Uploads.Single().Resume;
        try
        {
            await TestUiContext.Run(async () =>
            {
                await ExpectDiscardFailure<OperationCanceledException>(task);
                Assert(task.Record.Uploads.Single().Resume == resume, "A timeout lost the session's retry address.");
                AssertFailedCardRetained(task);
                fixture.Server.BeforeDeleteResponse = null;
                await task.DiscardResumeAsync();
                Assert(task.Record.Uploads.Single().Resume == null && fixture.Server.Deletes.Count == 2,
                    "Timed-out session was not retried successfully.");
                AssertFailedCardRetained(task);
            });
        }
        finally { task.Controls.Dispose(); }
    }

    private static async Task DiscardMissingSessions()
    {
        foreach (int status in new[] { 404, 410 })
        {
            await using var fixture = new UploadFixture();
            fixture.Server.DeleteStatusCode = status;
            var task = FailedResumableTask(fixture);
            try
            {
                await TestUiContext.Run(async () =>
                {
                    await task.DiscardResumeAsync();
                    await task.DiscardResumeAsync();
                    Assert(task.Record.Uploads.Single().Resume == null && fixture.Server.Deletes.Count == 1,
                        $"HTTP {status} blocked cleanup of an already absent session.");
                    AssertFailedCardRetained(task);
                });
            }
            finally { task.Controls.Dispose(); }
        }
    }
}
