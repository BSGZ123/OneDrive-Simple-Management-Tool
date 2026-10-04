using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Security.Cryptography;

namespace OneDrive_Simple_Management_Tool.Tests.FileManagementRegression;

internal static class AccountViewModelChecks
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Drive list load is idempotent and keeps same-name identities separate", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Record("A"), 0); await f.Store.AddAsync(Record("B"), 1);
            await f.Cloud.LoadDrivesFromDisk(); var first = f.Cloud.Drives[0];
            await f.Cloud.LoadDrivesFromDisk();
            Check(f.Cloud.Drives.Count == 2 && ReferenceEquals(first, f.Cloud.Drives[0]) && f.Cloud.Drives[1].Provider.DriveId == "B", "load changed identity");
        }),
        ("Failed drive save preserves input, and retry commits without reauthentication", async () =>
        {
            using var f = new Fixture(); await f.Cloud.LoadDrivesFromDisk();
            int logins = 0;
            var model = new CreateDriveViewModel(f.Cloud, _ => { logins++; return Task.FromResult(Provider("A")); }) { DisplayName = "retained input" };
            f.Protector.Fail = true;
            Check(!await model.CreateAsync() && !model.IsBusy && model.DisplayName == "retained input" && f.Cloud.Drives.Count == 0, "failed save looked successful");
            f.Protector.Fail = false;
            Check(await model.CreateAsync() && logins == 1 && f.Cloud.Drives.Count == 1 && (await f.Store.LoadAsync()).Data[0].DisplayName == "retained input", "retry failed");
        }),
        ("Busy create rejects repeated submissions and cancellation leaves no record", async () =>
        {
            using var f = new Fixture(); await f.Cloud.LoadDrivesFromDisk();
            var entered = new TaskCompletionSource();
            var model = new CreateDriveViewModel(f.Cloud, async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Provider("A"); });
            var active = model.CreateAsync(); await entered.Task;
            Check(!await model.CreateAsync(), "duplicate create accepted");
            model.Cancel();
            Check(!await active && f.Cloud.Drives.Count == 0 && (await f.Store.LoadAsync()).Revision == 0, "cancelled record persisted");
        }),
        ("Duplicate addition selects the original entry without renaming it", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Record("A"), 0); await f.Cloud.LoadDrivesFromDisk();
            var model = new CreateDriveViewModel(f.Cloud, _ => Task.FromResult(Provider("A"))) { DisplayName = "new name" };
            Check(!await model.CreateAsync() && f.Cloud.SelectedDrive == f.Cloud.Drives[0] && f.Cloud.Drives[0].DisplayName == "same name", "duplicate overwrote name");
        }),
        ("Existing OneDrive instance retains both IDs on wrong-account reauthentication", async () =>
        {
            var provider = new OneDrive("bound-drive", "A", new FakeAuthentication(), new Resolver());
            try { await provider.SignInAsync(); throw new Exception("mismatch accepted"); }
            catch (AccountAuthenticationException e) when (e.Failure == AuthenticationFailure.AccountMismatch) { }
            Check(provider.DriveId == "bound-drive" && provider.HomeAccountId == "A" && !provider.IsAuthenticated, "binding mutated");
        }),
        ("Configuration damage blocks adding without clearing an already displayed list", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Record("A"), 0); await f.Cloud.LoadDrivesFromDisk();
            await File.WriteAllTextAsync(f.Paths.Drives, "corrupt"); await f.Cloud.LoadDrivesFromDisk();
            Check(f.Cloud.NeedsRecovery && !f.Cloud.CanAdd && f.Cloud.Drives.Count == 1, "damage treated as empty data");
        })
    ];

    private static DriveDTO Record(string id) => new() { DisplayName = "same name", Provider = new() { HomeAccountId = id, DriveId = id } };
    private static OneDrive Provider(string id) => new(id, id, null) { IsAuthenticated = true };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class FakeAuthentication : IAccountAuthenticationService
    {
        public Task<AccountToken> AcquireInteractiveAsync(string id, CancellationToken token) => Task.FromResult(new AccountToken("B", "fake-token"));
        public Task<AccountToken> AcquireSilentAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Resolver : IAccountDriveResolver
    {
        public Task<string> ResolveAsync(string id, string drive, CancellationToken token) => throw new Exception("Wrong-account request must never reach Graph");
    }
    private sealed class Protector : IConfigurationProtector
    {
        public bool Fail;
        public byte[] Protect(byte[] data, string purpose) => Fail ? throw new CryptographicException() : new WindowsConfigurationProtector().Protect(data, purpose);
        public byte[] Unprotect(byte[] data, string purpose) => new WindowsConfigurationProtector().Unprotect(data, purpose);
    }
    private sealed class Fixture : IDisposable
    {
        public ApplicationDataPaths Paths { get; }
        public Protector Protector { get; } = new();
        public DriveConfigurationStore Store { get; }
        public CloudViewModel Cloud { get; }
        public Fixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "OneDriveAccountVmTests", Guid.NewGuid().ToString("N"));
            Paths = new(root, Path.Combine(root, "application"));
            Store = new(Paths, Protector);
            Cloud = new(Store, item => new DriveViewModel(Provider(item.Provider.DriveId), item.DisplayName));
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Paths.Root);
            if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "OneDriveAccountVmTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
