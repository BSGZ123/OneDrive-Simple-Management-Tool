using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;

internal static class Program
{
    private static int _passed;
    private static DriveDTO Drive(string account = "fictional-account-A", string id = "fictional-drive-A", string name = "same name") => new()
    { DisplayName = name, Provider = new() { HomeAccountId = account, DriveId = id } };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child-add")
        {
            var store = new DriveConfigurationStore(new ApplicationDataPaths(args[1], Path.Combine(args[1], "application")));
            await File.WriteAllTextAsync(Path.Combine(args[1], args[2] + ".ready"), "ready");
            while (!File.Exists(Path.Combine(args[1], "go"))) await Task.Delay(20);
            try { await store.AddAsync(Drive(args[2], args[2]), 1); return 0; }
            catch (ConfigurationException e) when (e.Failure == ConfigurationFailure.Conflict) { return 10; }
        }

        await Case("Authentication cancellation cannot reuse account A", async () =>
        {
            var auth = new FakeAuthentication();
            var session = new DriveAuthenticationSession(auth, new Resolver());
            var first = await session.AuthenticateAsync(null, null, true, default);
            auth.Interactive = (_, _) => throw new OperationCanceledException();
            await Expect<OperationCanceledException>(() => session.AuthenticateAsync(null, null, true, default));
            Check(first.AccountId == "A", "first account changed");
        });
        await Case("Existing account rejects another interactive result", async () =>
        {
            var auth = new FakeAuthentication { Interactive = (_, _) => Task.FromResult(new AccountToken("B", "token")) };
            await Failure(AuthenticationFailure.AccountMismatch, () => new DriveAuthenticationSession(auth, new Resolver()).AuthenticateAsync("A", "drive-A", true, default));
        });
        await Case("Existing drive is validated without discovering a replacement", async () =>
        {
            var resolver = new Resolver();
            var result = await new DriveAuthenticationSession(new FakeAuthentication(), resolver).AuthenticateAsync("A", "bound", false, default);
            Check(result.DriveId == "bound" && resolver.Existing == "bound", "binding changed");
        });
        await Case("Late completion cannot override a newer attempt", async () =>
        {
            var old = new TaskCompletionSource<AccountToken>();
            int call = 0;
            var auth = new FakeAuthentication { Interactive = (_, _) => ++call == 1 ? old.Task : Task.FromResult(new AccountToken("B", "token")) };
            var session = new DriveAuthenticationSession(auth, new Resolver());
            var first = session.AuthenticateAsync(null, null, true, default);
            Check((await session.AuthenticateAsync(null, null, true, default)).AccountId == "B", "second attempt failed");
            old.SetResult(new AccountToken("A", "token"));
            await Expect<OperationCanceledException>(() => first);
        });
        await Case("Independent sessions complete in reverse order with correct identities", async () =>
        {
            var first = new TaskCompletionSource<AccountToken>();
            var auth = new FakeAuthentication { Silent = (account, _) => account == "A" ? first.Task : Task.FromResult(new AccountToken(account, "token")) };
            var a = new DriveAuthenticationSession(auth, new Resolver()).AuthenticateAsync("A", "drive-A", false, default);
            var b = await new DriveAuthenticationSession(auth, new Resolver()).AuthenticateAsync("B", "drive-B", false, default);
            first.SetResult(new AccountToken("A", "token"));
            Check((await a).AccountId == "A" && b.AccountId == "B", "sessions mixed");
        });
        await Case("Drive resolution failure and cancellation do not yield an identity", async () =>
        {
            var resolver = new Resolver { Resolve = (_, _, _) => throw new IOException() };
            await Expect<IOException>(() => new DriveAuthenticationSession(new FakeAuthentication(), resolver).AuthenticateAsync(null, null, true, default));
            using var cancel = new CancellationTokenSource();
            resolver.Resolve = (_, _, _) => { cancel.Cancel(); return Task.FromResult("drive-A"); };
            await Expect<OperationCanceledException>(() => new DriveAuthenticationSession(new FakeAuthentication(), resolver).AuthenticateAsync(null, null, true, cancel.Token));
        });
        await Case("Token provider enforces HTTPS host and propagates cancellation", async () =>
        {
            var auth = new FakeAuthentication();
            var provider = new AccountTokenProvider(auth, "A");
            foreach (string url in new[] { "https://evil.example/", "http://graph.microsoft.com/", "https://graph.microsoft.com:444/", "https://user@graph.microsoft.com/" })
                await Expect<AccountAuthenticationException>(() => provider.GetAuthorizationTokenAsync(new Uri(url)));
            Check(auth.SilentCalls == 0, "credential requested for invalid target");
            using var cancel = new CancellationTokenSource();
            auth.Silent = (account, token) => { Check(token == cancel.Token, "token not forwarded"); return Task.FromResult(new AccountToken(account, "fictional-token")); };
            Check(await provider.GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com/v1.0/me"), cancellationToken: cancel.Token) == "fictional-token", "token missing");
            cancel.Cancel();
            await Expect<OperationCanceledException>(() => provider.GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com"), cancellationToken: cancel.Token));
        });
        await Case("Token provider rejects silent results for a different account", async () =>
        {
            var auth = new FakeAuthentication { Silent = (_, _) => Task.FromResult(new AccountToken("B", "token")) };
            await Failure(AuthenticationFailure.AccountMismatch, () => new AccountTokenProvider(auth, "A").GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com")));
        });

        await Case("First load is empty and creates no data file", async () =>
        {
            using var f = new Fixture();
            Check((await f.Store.LoadAsync()).Revision == 0 && !File.Exists(f.Paths.Drives), "load wrote data");
        });
        await Case("Same display name preserves different account identities", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            var saved = await f.Store.AddAsync(Drive("B", "drive-B"), 1);
            Check(saved.Data.Count == 2 && saved.Data[1].Provider.HomeAccountId == "B", "same name deduplicated");
            await ConfigFailure(ConfigurationFailure.Duplicate, () => f.Store.AddAsync(Drive(), 2));
            Check((await f.Store.LoadAsync()).Data.Count == 2, "duplicate mutated data");
        });
        await Case("Reading an existing list does not rewrite its data or backup", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            byte[] before = await File.ReadAllBytesAsync(f.Paths.Drives);
            DateTime time = File.GetLastWriteTimeUtc(f.Paths.Drives);
            await f.Store.LoadAsync(); await f.Store.LoadAsync();
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Drives)) && time == File.GetLastWriteTimeUtc(f.Paths.Drives), "read rewrote data");
        });
        await Case("Real DPAPI encrypts main and backup and rejects the wrong purpose", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0); await f.Store.AddAsync(Drive("B", "B"), 1);
            foreach (string file in new[] { f.Paths.Drives, f.Paths.Drives + ".bak" })
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                Check(!Encoding.UTF8.GetString(bytes).Contains("fictional-account"), "plaintext found");
                var envelope = JsonSerializer.Deserialize(bytes, ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
                var protector = new WindowsConfigurationProtector();
                byte[] plain = protector.Unprotect(envelope.Payload, "OneDriveSimpleManagementTool/drives/v1");
                Check(Encoding.UTF8.GetString(plain).Contains("fictional-account"), "roundtrip failed");
                await Expect<CryptographicException>(() => Task.FromResult(protector.Unprotect(envelope.Payload, "wrong-purpose")));
            }
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(f.Paths.Drives), "*.tmp").Any(), "temp leaked");
        });
        await Case("Optimistic revision detects stale and concurrent writers", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            var outcomes = await Task.WhenAll(Enumerable.Range(1, 8).Select(async i =>
            {
                try { await new DriveConfigurationStore(f.Paths).AddAsync(Drive("A" + i, "D" + i), 1); return true; }
                catch (ConfigurationException e) when (e.Failure == ConfigurationFailure.Conflict) { return false; }
            }));
            Check(outcomes.Count(x => x) == 1 && (await f.Store.LoadAsync()).Data.Count == 2, "silent lost update");
        });
        await Case("Two processes cannot both commit the same revision", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            using var first = Child(f.Paths.Root, "child-A"); using var second = Child(f.Paths.Root, "child-B");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(f.Paths.Root, "child-A.ready")) || !File.Exists(Path.Combine(f.Paths.Root, "child-B.ready"))) await Task.Delay(20, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(f.Paths.Root, "go"), "go");
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            Check(new[] { first.ExitCode, second.ExitCode }.Order().SequenceEqual(new[] { 0, 10 }), "process conflict not detected");
            Check((await f.Store.LoadAsync()).Data.Count == 2, "process update missing");
        });
        await Case("Damaged main preserves bytes until explicit backup recovery", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0); await f.Store.AddAsync(Drive("B", "B"), 1);
            await File.WriteAllTextAsync(f.Paths.Drives, "broken-fictional-private-data");
            await ConfigFailure(ConfigurationFailure.Invalid, () => f.Store.LoadAsync());
            Check(await File.ReadAllTextAsync(f.Paths.Drives) == "broken-fictional-private-data", "damage overwritten");
            await f.Store.RestoreBackupAsync();
            Check((await f.Store.LoadAsync()).Data.Count == 1, "backup not restored");
            Check(Directory.EnumerateFiles(Path.GetDirectoryName(f.Paths.Drives), "*.recovery-*").All(p => !File.ReadAllText(p).Contains("fictional-private-data")), "archive plaintext");
        });
        await Case("Future versions are preserved and block ordinary writes", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            var envelope = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(f.Paths.Drives), ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
            envelope.Version = 99;
            await File.WriteAllBytesAsync(f.Paths.Drives, JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectedConfigurationJsonContext.Default.ProtectedEnvelope));
            await ConfigFailure(ConfigurationFailure.Version, () => f.Store.LoadAsync());
            await ConfigFailure(ConfigurationFailure.Version, () => f.Store.AddAsync(Drive("B", "B"), 1));
        });
        await Case("Bad main never falls back to an obsolete legacy file", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0); await f.Legacy(new() { Drive("old", "old") });
            await File.WriteAllTextAsync(f.Paths.Drives, "bad");
            await ConfigFailure(ConfigurationFailure.Invalid, () => f.Store.LoadAsync());
            Check(File.Exists(f.Paths.LegacyDrives), "legacy discarded during failure");
        });
        await Case("Valid legacy list migrates once and cleans plaintext", async () =>
        {
            using var f = new Fixture();
            await f.Legacy(new() { Drive(), Drive("B", "B") });
            var loaded = await f.Store.LoadAsync();
            Check(loaded.Data.Count == 2 && !File.Exists(f.Paths.LegacyDrives) && File.Exists(f.Paths.Drives + ".migrated"), "migration incomplete");
            Check((await f.Store.LoadAsync()).Revision == 1, "migration repeated");
        });
        await Case("Failed legacy cleanup is visible and retries without importing again", async () =>
        {
            using var f = new Fixture();
            await f.Legacy(new() { Drive() });
            using (var locked = new FileStream(f.Paths.LegacyDrives, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check((await f.Store.LoadAsync()).Data.Count == 1 && f.Store.LegacyCleanupPending, "cleanup failure hidden");
            }
            Check((await f.Store.LoadAsync()).Data.Count == 1 && !f.Store.LegacyCleanupPending && !File.Exists(f.Paths.LegacyDrives), "cleanup retry failed");
        });
        await Case("Legacy duplicates require an explicit choice and preserve different identities", async () =>
        {
            using var f = new Fixture();
            await f.Legacy(new() { Drive(), Drive(name: "second alias"), Drive("B", "B") });
            await ConfigFailure(ConfigurationFailure.Duplicate, () => f.Store.LoadAsync());
            Check(!File.Exists(f.Paths.Drives), "invalid migration committed");
            var candidates = await f.Store.ReadDuplicateCandidatesAsync();
            await ConfigFailure(ConfigurationFailure.Invalid, () => f.Store.RepairDuplicatesAsync(candidates, new[] { 1 }));
            await f.Store.RepairDuplicatesAsync(candidates, new[] { 1, 2 });
            var result = await f.Store.LoadAsync();
            Check(result.Data.Count == 2 && result.Data[0].DisplayName == "second alias", "wrong duplicate kept");
        });
        await Case("Encryption failure never overwrites main or creates plaintext temp", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            byte[] before = await File.ReadAllBytesAsync(f.Paths.Drives);
            var broken = new DriveConfigurationStore(f.Paths, new FailingProtector());
            await ConfigFailure(ConfigurationFailure.Protection, () => broken.AddAsync(Drive("B", "B"), 1));
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Drives)), "main lost on encryption failure");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(f.Paths.Drives), "*.tmp").Any(), "temp left");
        });
        await Case("Cancellation before commit and occupied destination preserve data", async () =>
        {
            using var f = new Fixture();
            await f.Store.AddAsync(Drive(), 0);
            byte[] before = await File.ReadAllBytesAsync(f.Paths.Drives);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Expect<OperationCanceledException>(() => f.Store.AddAsync(Drive("B", "B"), 1, cancelled.Token));
            using (var locked = new FileStream(f.Paths.Drives, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Expect<IOException>(() => f.Store.AddAsync(Drive("B", "B"), 1));
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Drives)), "write failure lost data");
        });
        await Case("Missing migrated file is damage, not an empty first run", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0); File.Delete(f.Paths.Drives);
            await ConfigFailure(ConfigurationFailure.Missing, () => f.Store.LoadAsync());
        });
        await Case("Explicit rebuild encrypts invalid legacy recovery bytes", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.LegacyDrives));
            await File.WriteAllTextAsync(f.Paths.LegacyDrives, "bad-fictional-sensitive");
            await f.Store.RebuildAsync();
            Check((await f.Store.LoadAsync()).Data.Count == 0 && !File.Exists(f.Paths.LegacyDrives), "rebuild failed");
            Check(Directory.EnumerateFiles(Path.GetDirectoryName(f.Paths.Drives), "*.recovery-*").Any(), "recovery missing");
        });
        await Case("Oversize list is rejected before commit", async () =>
        {
            using var f = new Fixture();
            await ConfigFailure(ConfigurationFailure.Invalid, () => f.Store.AddAsync(Drive(name: new string('x', DriveConfigurationStore.MaximumBytes)), 0));
            Check(!File.Exists(f.Paths.Drives), "oversize committed");
        });
        await Case("Sync migration preserves manifest and encrypts checkpoint backups", async () =>
        {
            using var f = new Fixture();
            var store = new FolderSyncStore(f.Paths.FolderSync);
            var binding = new FolderSyncBinding { AccountId = "fictional-account", DriveId = "drive", LocalPath = Path.Combine(f.Paths.Root, "folder"),
                FolderName = "folder", RemoteFolderId = "remote", RemotePath = "/folder", Enabled = false,
                Files = new() { ["hello.txt"] = new(false, "fake-hash", 42) } };
            Directory.CreateDirectory(f.Paths.FolderSync);
            string legacy = Path.Combine(f.Paths.FolderSync, binding.Id + ".json");
            await File.WriteAllBytesAsync(legacy, JsonSerializer.SerializeToUtf8Bytes(binding, FolderSyncJsonContext.Default.FolderSyncBinding));
            var loaded = await store.LoadAsync(default);
            Check(loaded.Errors == 0 && loaded.Bindings[0].Files.Count == 1 && !loaded.Bindings[0].Enabled && !File.Exists(legacy), "sync migration changed semantics");
            binding = loaded.Bindings[0];
            await store.SaveAsync(binding, default);
            Check(!File.ReadAllText(Path.Combine(f.Paths.FolderSync, binding.Id + ".dat.bak")).Contains("fictional-account"), "sync backup plaintext");
            var stale = (await store.LoadAsync(default)).Bindings[0];
            await store.SaveAsync(binding, default);
            await Expect<FolderSyncException>(() => store.SaveAsync(stale, default));
        });
        await Case("Shipped application settings pass startup validation without rewriting their encoding", async () =>
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            string path = Path.Combine(directory, "appsettings.json");
            byte[] before = await File.ReadAllBytesAsync(path);
            var configuration = await StartupInitialization.ReadSettingsAsync(directory);
            string clientId = configuration["AzureAD:ClientId"];
            Check(Guid.TryParse(clientId, out var id) && id != Guid.Empty, "shipped settings rejected");
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "settings were rewritten");
        });
        await Case("Settings are read from the supplied application directory", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.ApplicationDirectory);
            await File.WriteAllTextAsync(Path.Combine(f.Paths.ApplicationDirectory, "appsettings.json"), "{\"AzureAD\":{\"ClientId\":\"12345678-1234-1234-1234-123456789abc\"}}");
            var configuration = await StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory);
            Check(configuration["AzureAD:ClientId"] == "12345678-1234-1234-1234-123456789abc", "wrong settings path");
            await File.WriteAllTextAsync(Path.Combine(f.Paths.ApplicationDirectory, "appsettings.json"), "{\"AzureAD\":{\"ClientId\":\"bad\"}}");
            Check(configuration["AzureAD:ClientId"] == "12345678-1234-1234-1234-123456789abc", "validated snapshot changed after file replacement");
            await Expect<InvalidDataException>(() => StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory));
        });
        await Case("Settings accept UTF-8 with or without BOM and JSON configuration syntax", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.ApplicationDirectory);
            foreach (bool bom in new[] { true, false })
            {
                await File.WriteAllTextAsync(Path.Combine(f.Paths.ApplicationDirectory, "appsettings.json"),
                    "{ // application settings\n\"azuread\": {\"clientid\": \"12345678-1234-1234-1234-123456789abc\",},}", new UTF8Encoding(bom));
                var configuration = await StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory);
                Check(configuration["AzureAD:ClientId"] == "12345678-1234-1234-1234-123456789abc", "configuration syntax rejected");
            }
        });
        await Case("Missing or invalid settings still stop startup before other phases and preserve the file", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.ApplicationDirectory);
            string path = Path.Combine(f.Paths.ApplicationDirectory, "appsettings.json");
            await Expect<FileNotFoundException>(() => StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory));
            foreach (string content in new[] { "", "{", "{}", "{\"AzureAD\":{\"ClientId\":null}}",
                "{\"AzureAD\":{\"ClientId\":\"00000000-0000-0000-0000-000000000000\"}}",
                "{\"AzureAD\":{\"ClientId\":\"bad\"}}" })
            {
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));
                var visited = new List<StartupPhase>();
                try
                {
                    await StartupInitialization.RunAsync(async phase =>
                    {
                        visited.Add(phase);
                        if (phase == StartupPhase.Settings) await StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory);
                    });
                    throw new Exception("invalid settings accepted");
                }
                catch (StartupException exception) { Check(exception.Phase == StartupPhase.Settings, "wrong failure phase"); }
                Check(visited.SequenceEqual(new[] { StartupPhase.Settings }), "startup continued with invalid settings");
                Check(await File.ReadAllTextAsync(path) == content, "invalid settings overwritten");
            }
        });
        await Case("Settings retain bounded reads and cancellation", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.ApplicationDirectory);
            string path = Path.Combine(f.Paths.ApplicationDirectory, "appsettings.json");
            await File.WriteAllTextAsync(path, "{\"AzureAD\":{\"ClientId\":\"12345678-1234-1234-1234-123456789abc\"}}");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Expect<OperationCanceledException>(() => StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory, cancelled.Token));
            using (var stream = File.OpenWrite(path)) stream.SetLength(1024 * 1024 + 1);
            await ConfigFailure(ConfigurationFailure.Invalid, () => StartupInitialization.ReadSettingsAsync(f.Paths.ApplicationDirectory));
        });
        await Case("Startup stops at a failed stage before services or background work", async () =>
        {
            var visited = new List<StartupPhase>();
            await Expect<StartupException>(() => StartupInitialization.RunAsync(phase =>
            {
                visited.Add(phase); if (phase == StartupPhase.Cache) throw new IOException("fictional-path"); return Task.CompletedTask;
            }));
            Check(visited.SequenceEqual(new[] { StartupPhase.Settings, StartupPhase.Directory, StartupPhase.Cache }), "initialization advanced after failure");
        });
        await Case("Production diagnostics are bounded and stop detailed collection at 30 minutes", () =>
        {
            using var f = new Fixture(); var time = new FakeTime(); var diagnostics = new SafeDiagnostics(f.Paths.Diagnostics, false, time);
            diagnostics.Record(DiagnosticEvent.Authentication, DiagnosticLevel.Info);
            Check(diagnostics.GetSummary() == "" && !Directory.Exists(f.Paths.Diagnostics), "default info/files enabled");
            diagnostics.BeginSession(); diagnostics.Record(DiagnosticEvent.Authentication, DiagnosticLevel.Info);
            Check(diagnostics.GetSummary().Contains("Authentication"), "session info missing");
            time.Now += TimeSpan.FromMinutes(31);
            diagnostics.Record(DiagnosticEvent.Migration, DiagnosticLevel.Info);
            Check(!diagnostics.IsSessionActive && !diagnostics.GetSummary().Contains("Migration"), "session did not expire");
            for (int i = 0; i < 250; i++) diagnostics.Record(DiagnosticEvent.ConfigurationFailure);
            Check(diagnostics.GetSummary().Split(Environment.NewLine).Length == 200, "buffer not bounded");
            Check(!new SafeDiagnostics(f.Paths.Diagnostics, false, time).IsSessionActive, "session persisted across restart");
            return Task.CompletedTask;
        });
        await Case("Debug events and file failures cannot expose arbitrary strings", () =>
        {
            using var f = new Fixture();
            var diagnostics = new SafeDiagnostics(f.Paths.Diagnostics, true);
            diagnostics.Record(DiagnosticEvent.Authentication, DiagnosticLevel.Debug);
            Check(diagnostics.GetSummary().Contains("Debug"), "debug event missing");
            Check(typeof(SafeDiagnostics).GetMethod("Record").GetParameters().All(p => p.ParameterType != typeof(string) && p.ParameterType != typeof(Exception)), "unsafe logging API");
            Check(!new AccountToken("fictional-email@example.invalid", "fictional-secret-token").ToString().Contains("fictional"), "token ToString leaked");
            Directory.CreateDirectory(f.Paths.Diagnostics);
            File.WriteAllText(Path.Combine(f.Paths.Diagnostics, "Debug"), "blocked directory");
            diagnostics.BeginSession(); diagnostics.Record(DiagnosticEvent.Authentication);
            Check(!diagnostics.IsSessionActive && diagnostics.GetSummary().Contains("Authentication"), "log failure blocked memory buffer");
            return Task.CompletedTask;
        });
        await Case("Ciphertext tampering is rejected without rewriting the source", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0);
            var envelope = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(f.Paths.Drives), ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
            envelope.Payload[envelope.Payload.Length / 2] ^= 1;
            byte[] damaged = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
            await File.WriteAllBytesAsync(f.Paths.Drives, damaged);
            await ConfigFailure(ConfigurationFailure.Protection, () => f.Store.LoadAsync());
            Check(damaged.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Drives)), "tampered file overwritten");
        });
        await Case("Inner schema version and missing required fields are rejected", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0);
            foreach (string plaintext in new[] { "{\"SchemaVersion\":99,\"Revision\":1,\"Data\":[]}", "{\"Revision\":1,\"Data\":[]}" })
            {
                var envelope = new ProtectedEnvelope { Payload = new WindowsConfigurationProtector().Protect(Encoding.UTF8.GetBytes(plaintext), "OneDriveSimpleManagementTool/drives/v1") };
                await File.WriteAllBytesAsync(f.Paths.Drives, JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectedConfigurationJsonContext.Default.ProtectedEnvelope));
                await Expect<ConfigurationException>(() => f.Store.LoadAsync());
            }
        });
        await Case("Migration restarts after committed data but before marker or cleanup", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0);
            byte[] before = await File.ReadAllBytesAsync(f.Paths.Drives);
            File.Delete(f.Paths.Drives + ".migrated");
            await f.Legacy(new() { Drive("stale", "stale") });
            var loaded = await new DriveConfigurationStore(f.Paths).LoadAsync();
            Check(loaded.Data.Single().Provider.HomeAccountId == "fictional-account-A" && before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Drives)), "interrupted migration reimported stale data");
            Check(File.Exists(f.Paths.Drives + ".migrated") && !File.Exists(f.Paths.LegacyDrives), "cleanup not resumed");
        });
        await Case("Cancellation during encryption preserves the previous revision", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0);
            using var cancel = new CancellationTokenSource();
            var store = new DriveConfigurationStore(f.Paths, new CancellingProtector(cancel));
            await Expect<OperationCanceledException>(() => store.AddAsync(Drive("B", "B"), 1, cancel.Token));
            Check((await f.Store.LoadAsync()).Revision == 1, "cancelled encryption committed");
        });
        await Case("A recovered configuration cannot be overwritten by a stale recovery request", async () =>
        {
            using var f = new Fixture(); await f.Store.AddAsync(Drive(), 0); await f.Store.AddAsync(Drive("B", "B"), 1);
            await File.WriteAllTextAsync(f.Paths.Drives, "broken"); await f.Store.RestoreBackupAsync();
            await ConfigFailure(ConfigurationFailure.Conflict, () => f.Store.RebuildAsync());
            Check((await f.Store.LoadAsync()).Data.Count == 1, "stale rebuild lost valid data");
        });
        await Case("Sync damaged and missing files require explicit recovery", async () =>
        {
            using var f = new Fixture(); var store = new FolderSyncStore(f.Paths.FolderSync);
            var binding = new FolderSyncBinding { AccountId = "A", DriveId = "D", LocalPath = Path.Combine(f.Paths.Root, "folder"),
                FolderName = "folder", RemoteFolderId = "remote", RemotePath = "/folder", Enabled = false };
            await store.SaveAsync(binding, default); await store.SaveAsync(binding, default);
            string main = Path.Combine(f.Paths.FolderSync, binding.Id + ".dat");
            await File.WriteAllTextAsync(main, "damaged");
            Check((await store.LoadAsync(default)).Errors == 1, "sync damage hidden");
            await store.RestoreUnreadableBackupsAsync(Array.Empty<string>());
            Check((await store.LoadAsync(default)).Bindings.Count == 1, "sync backup restoration failed");
            File.Delete(main);
            Check((await store.LoadAsync(default)).Errors == 1, "missing sync main hidden");
        });
        await Case("Sync removal tombstone prevents resurrection after interrupted cleanup", async () =>
        {
            using var f = new Fixture(); var store = new FolderSyncStore(f.Paths.FolderSync);
            var binding = new FolderSyncBinding { AccountId = "A", DriveId = "D", LocalPath = Path.Combine(f.Paths.Root, "folder"),
                FolderName = "folder", RemoteFolderId = "remote", RemotePath = "/folder", Enabled = false };
            await store.SaveAsync(binding, default);
            string main = Path.Combine(f.Paths.FolderSync, binding.Id + ".dat");
            byte[] old = await File.ReadAllBytesAsync(main);
            await store.RemoveAsync(binding);
            await File.WriteAllBytesAsync(main, old);
            Check((await store.LoadAsync(default)).Bindings.Count == 0, "removed binding resurrected");
            await Expect<FolderSyncException>(() => store.SaveAsync(binding, default));
        });
        await Case("Diagnostic files rotate at 1 MiB and expire by mode", () =>
        {
            using var f = new Fixture();
            foreach (bool debug in new[] { false, true })
            {
                string directory = Path.Combine(f.Paths.Diagnostics, debug ? "Debug" : "Production");
                var diagnostic = new SafeDiagnostics(f.Paths.Diagnostics, debug);
                diagnostic.BeginSession(); diagnostic.Record(DiagnosticEvent.Startup);
                string first = Directory.GetFiles(directory, "*.log").Single();
                using (var stream = File.OpenWrite(first)) stream.SetLength(1024 * 1024 - 5);
                diagnostic.Record(DiagnosticEvent.Authentication);
                Check(Directory.GetFiles(directory, "*.log").Length == 2 && Directory.EnumerateFiles(directory).All(p => new FileInfo(p).Length <= 1024 * 1024), "rotation failed");
                File.SetCreationTimeUtc(first, DateTime.UtcNow.AddDays(debug ? -4 : -2));
                diagnostic.Record(DiagnosticEvent.Startup);
                Check(!File.Exists(first), "retention did not expire");
                diagnostic.EndSession(); Check(!diagnostic.IsSessionActive, "manual stop failed");
            }
            return Task.CompletedTask;
        });
        await Case("Diagnostic quota cleanup failure disables files and preserves memory events", () =>
        {
            using var f = new Fixture(); string directory = Path.Combine(f.Paths.Diagnostics, "Production");
            Directory.CreateDirectory(directory); string blocked = Path.Combine(directory, "old.log");
            using (var stream = File.Create(blocked)) stream.SetLength(5 * 1024 * 1024);
            var diagnostic = new SafeDiagnostics(f.Paths.Diagnostics, false); diagnostic.BeginSession();
            using (var locked = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
                diagnostic.Record(DiagnosticEvent.ConfigurationFailure);
            Check(!diagnostic.IsSessionActive && diagnostic.GetSummary().Contains("ConfigurationFailure"), "quota failure mishandled");
            return Task.CompletedTask;
        });
        await Case("MSAL invalid legacy cache is preserved and cannot report successful migration", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.LegacyTokenCache));
            byte[] invalid = Encoding.UTF8.GetBytes("fictional-broken-token-cache");
            await File.WriteAllBytesAsync(f.Paths.LegacyTokenCache, invalid);
            var app = Microsoft.Identity.Client.PublicClientApplicationBuilder.Create("12345678-1234-1234-1234-123456789abc").Build();
            await Expect<Exception>(() => MsalCacheInitialization.InitializeAsync(f.Paths, app));
            Check(invalid.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.LegacyTokenCache)) &&
                !File.Exists(Path.Combine(f.Paths.Authentication, "CloudFlowTokenCache.bin")), "bad legacy cache destroyed or installed");
        });
        await Case("MSAL invalid new cache never falls back to old data or clears the main cache", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.Authentication);
            string destination = Path.Combine(f.Paths.Authentication, "CloudFlowTokenCache.bin");
            byte[] invalid = Encoding.UTF8.GetBytes("fictional-broken-token-cache"); await File.WriteAllBytesAsync(destination, invalid);
            var app = Microsoft.Identity.Client.PublicClientApplicationBuilder.Create("12345678-1234-1234-1234-123456789abc").Build();
            await Expect<Exception>(() => MsalCacheInitialization.InitializeAsync(f.Paths, app));
            Check(invalid.SequenceEqual(await File.ReadAllBytesAsync(destination)), "bad cache cleared");
        });
        await Case("MSAL migrates a valid empty fictional cache using the library's encrypted format", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.LegacyTokenCache));
            var quiet = new TraceSource("RegressionCache", SourceLevels.Off); quiet.Listeners.Clear();
            var seed = await Microsoft.Identity.Client.Extensions.Msal.MsalCacheHelper.CreateAsync(
                new Microsoft.Identity.Client.Extensions.Msal.StorageCreationPropertiesBuilder("CloudFlowTokenCache.bin", Path.GetDirectoryName(f.Paths.LegacyTokenCache)).Build(), quiet);
            seed.SaveUnencryptedTokenCache(Encoding.UTF8.GetBytes("{\"AccessToken\":{},\"RefreshToken\":{},\"IdToken\":{},\"Account\":{},\"AppMetadata\":{}}"));
            var app = Microsoft.Identity.Client.PublicClientApplicationBuilder.Create("12345678-1234-1234-1234-123456789abc").Build();
            var result = await MsalCacheInitialization.InitializeAsync(f.Paths, app);
            Check(!result.CleanupPending && !File.Exists(f.Paths.LegacyTokenCache) && !(await app.GetAccountsAsync()).Any(), "valid cache migration failed");
            result.Cache.UnregisterCache(app.UserTokenCache);
        });
        Console.WriteLine($"Passed {_passed} account/configuration regression checks (real Windows DPAPI, isolated fictional data).");
        return 0;
    }

    private static Process Child(string root, string account)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("child-add"); start.ArgumentList.Add(root); start.ArgumentList.Add(account);
        return Process.Start(start);
    }
    private static async Task Case(string name, Func<Task> run) { await run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ConfigFailure(ConfigurationFailure failure, Func<Task> run)
    {
        try { await run(); } catch (ConfigurationException e) when (e.Failure == failure) { return; }
        throw new Exception("Expected configuration failure " + failure);
    }
    private static async Task Failure(AuthenticationFailure failure, Func<Task> run)
    {
        try { await run(); } catch (AccountAuthenticationException e) when (e.Failure == failure) { return; }
        throw new Exception("Expected authentication failure " + failure);
    }
    private sealed class FakeAuthentication : IAccountAuthenticationService
    {
        public int SilentCalls;
        public Func<string, CancellationToken, Task<AccountToken>> Interactive = (id, _) => Task.FromResult(new AccountToken(id ?? "A", "token"));
        public Func<string, CancellationToken, Task<AccountToken>> Silent = (id, _) => Task.FromResult(new AccountToken(id, "token"));
        public Task<AccountToken> AcquireInteractiveAsync(string id, CancellationToken token) => Interactive(id, token);
        public Task<AccountToken> AcquireSilentAsync(string id, CancellationToken token) { SilentCalls++; return Silent(id, token); }
    }
    private sealed class Resolver : IAccountDriveResolver
    {
        public string Existing;
        public Func<string, string, CancellationToken, Task<string>> Resolve = (account, drive, _) => Task.FromResult(drive ?? "drive-" + account);
        public Task<string> ResolveAsync(string account, string drive, CancellationToken token) { Existing = drive; return Resolve(account, drive, token); }
    }
    private sealed class FailingProtector : IConfigurationProtector
    {
        public byte[] Protect(byte[] data, string purpose) => throw new CryptographicException();
        public byte[] Unprotect(byte[] data, string purpose) => new WindowsConfigurationProtector().Unprotect(data, purpose);
    }
    private sealed class CancellingProtector(CancellationTokenSource cancel) : IConfigurationProtector
    {
        public byte[] Protect(byte[] data, string purpose)
        {
            var protectedData = new WindowsConfigurationProtector().Protect(data, purpose); cancel.Cancel(); return protectedData;
        }
        public byte[] Unprotect(byte[] data, string purpose) => new WindowsConfigurationProtector().Unprotect(data, purpose);
    }
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        public ApplicationDataPaths Paths { get; }
        public DriveConfigurationStore Store { get; }
        public Fixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "OneDriveAccountRegression", Guid.NewGuid().ToString("N"));
            Paths = new(root, Path.Combine(root, "application"));
            Store = new(Paths);
        }
        public async Task Legacy(List<DriveDTO> drives)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.LegacyDrives));
            await File.WriteAllBytesAsync(Paths.LegacyDrives, JsonSerializer.SerializeToUtf8Bytes(drives, DriveDTOSourceGenerationContext.Default.ListDriveDTO));
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Paths.Root);
            if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "OneDriveAccountRegression") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
