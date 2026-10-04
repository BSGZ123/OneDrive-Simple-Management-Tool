using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System.Xml.Linq;

int passed = 0;
await Check("Missing preferences use defaults without creating a file", async () =>
{
    using var f = new Fixture();
    Assert(await f.Store.LoadAsync() == new AppearancePreferences());
    Assert(!File.Exists(f.Paths.Appearance));
});
await Check("All theme/material combinations survive a new store instance", async () =>
{
    using var f = new Fixture();
    foreach (var theme in Enum.GetValues<AppearanceTheme>())
    foreach (var material in Enum.GetValues<AppearanceMaterial>())
    {
        var expected = new AppearancePreferences { Theme = theme, Material = material };
        await f.Store.SaveAsync(expected);
        Assert(await new AppearanceSettingsStore(f.Paths).LoadAsync() == expected);
    }
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.Appearance), "*.tmp").Length == 0);
});
await Check("Corrupt, unknown-version, oversized and invalid enums do not block startup or overwrite input", async () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.Appearance));
    foreach (string content in new[] { "broken", "null", "{\"Version\":99}", "{\"Theme\":99}", "{\"Material\":-1}", new string(' ', 17000) })
    {
        await File.WriteAllTextAsync(f.Paths.Appearance, content);
        var vm = new SettingViewModel(f.Store);
        await vm.InitializeAsync();
        AppearancePreferences applied = null;
        vm.AttachAppearance(p => { applied = p; return true; });
        Assert(vm.HasError && applied == new AppearancePreferences());
        Assert(await File.ReadAllTextAsync(f.Paths.Appearance) == content);
        Assert(!vm.HasUnsavedChanges && !vm.RetrySaveCommand.CanExecute(null));
    }
});
await Check("Valid preferences load and apply before any change; loading makes no writes", async () =>
{
    var store = new MemoryStore { Value = new() { Theme = AppearanceTheme.Dark, Material = AppearanceMaterial.MicaAlt } };
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    AppearancePreferences applied = null;
    vm.AttachAppearance(p => { applied = p; return true; });
    Assert(vm.ThemeIndex == 2 && vm.MaterialIndex == 2 && applied == store.Value && store.Writes.Count == 0);
});
await Check("An immediate appearance change is persisted and restored", async () =>
{
    using var f = new Fixture();
    var vm = new SettingViewModel(f.Store);
    await vm.InitializeAsync();
    AppearancePreferences applied = null;
    vm.AttachAppearance(p => { applied = p; return true; });
    vm.ThemeIndex = 2;
    Assert(applied.Theme == AppearanceTheme.Dark);
    vm.MaterialIndex = 3;
    Assert(applied.Material == AppearanceMaterial.Acrylic);
    await vm.FlushAsync();
    var restored = new SettingViewModel(new AppearanceSettingsStore(f.Paths));
    await restored.InitializeAsync();
    Assert(restored.ThemeIndex == 2 && restored.MaterialIndex == 3 && !vm.HasUnsavedChanges && !vm.IsSaving);
});
await Check("Save failure keeps the applied preference and retries successfully", async () =>
{
    var store = new MemoryStore { Fail = true };
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    AppearancePreferences applied = null;
    vm.AttachAppearance(p => { applied = p; return true; });
    vm.ThemeIndex = 1;
    await vm.FlushAsync();
    Assert(applied.Theme == AppearanceTheme.Light && vm.ThemeIndex == 1);
    Assert(vm.HasError && vm.HasUnsavedChanges && !vm.IsSaving && vm.RetrySaveCommand.CanExecute(null));
    Assert(!vm.ErrorMessage.Contains("sensitive"));
    store.Fail = false;
    await vm.RetrySaveCommand.ExecuteAsync(null);
    Assert(store.Value.Theme == AppearanceTheme.Light && !vm.HasError && !vm.HasUnsavedChanges);
});
await Check("Rapid choices are serialized; flush waits for the final choice", async () =>
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new MemoryStore { Gate = gate.Task };
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    vm.ThemeIndex = 1;
    vm.MaterialIndex = 1;
    vm.ThemeIndex = 2;
    vm.MaterialIndex = 3;
    var flush = vm.FlushAsync();
    Assert(vm.IsSaving && !flush.IsCompleted && store.Writes.Count == 1);
    gate.SetResult();
    await flush.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(store.Value == new AppearancePreferences { Theme = AppearanceTheme.Dark, Material = AppearanceMaterial.Acrylic });
    Assert(store.Writes.Count == 4 && !vm.IsSaving && !vm.HasUnsavedChanges && !vm.HasError);
});
await Check("An older failed save cannot overwrite the final successful state", async () =>
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new MemoryStore { Gate = gate.Task, FailFirst = true };
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    vm.ThemeIndex = 1;
    vm.ThemeIndex = 2;
    gate.SetResult();
    await vm.FlushAsync();
    Assert(!vm.HasError && !vm.HasUnsavedChanges && store.Value.Theme == AppearanceTheme.Dark);
});
await Check("Unavailable material preserves the desired selection and reports fallback", async () =>
{
    var store = new MemoryStore();
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    vm.AttachAppearance(p => p.Material == AppearanceMaterial.None);
    vm.MaterialIndex = 2;
    await vm.FlushAsync();
    Assert(vm.MaterialNotice.Length > 0 && store.Value.Material == AppearanceMaterial.MicaAlt);
    vm.MaterialIndex = 0;
    await vm.FlushAsync();
    Assert(vm.MaterialNotice.Length == 0);
});
await Check("Invalid UI selection indices neither change preferences nor write", async () =>
{
    var store = new MemoryStore();
    var vm = new SettingViewModel(store);
    await vm.InitializeAsync();
    vm.ThemeIndex = -1;
    vm.MaterialIndex = 999;
    await vm.FlushAsync();
    Assert(vm.ThemeIndex == 0 && vm.MaterialIndex == 0 && store.Writes.Count == 0);
});
await Check("A real replacement failure leaves previous bytes intact and removes its temporary file", async () =>
{
    using var f = new Fixture();
    await f.Store.SaveAsync(new() { Theme = AppearanceTheme.Dark });
    var before = await File.ReadAllBytesAsync(f.Paths.Appearance);
    using (var locked = new FileStream(f.Paths.Appearance, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { await f.Store.SaveAsync(new() { Theme = AppearanceTheme.Light }); throw new Exception("Save unexpectedly succeeded"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    Assert(before.SequenceEqual(await File.ReadAllBytesAsync(f.Paths.Appearance)));
    Assert(Directory.GetFiles(Path.GetDirectoryName(f.Paths.Appearance), "*.tmp").Length == 0);
});
await Check("Both language resources contain nonempty and unique settings strings", () =>
{
    foreach (string language in new[] { "en-US", "zh-CN" })
    {
        var entries = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw"))
            .Root.Elements("data").ToDictionary(e => (string)e.Attribute("name"), e => e.Element("value").Value);
        foreach (string key in new[] { "Settings_SaveFailed", "Settings_LoadFailed", "Settings_MaterialFallback", "Settings_Saving",
            "Settings_Saved", "Settings_RetrySave.Content", "Settings_Version", "Settings_Material.Header", "Settings_Material_None.Content" })
            Assert(entries.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value));
        Assert(string.Format(entries["Settings_Version"], "1.2.3").Contains("1.2.3"));
    }
    return Task.CompletedTask;
});
Console.WriteLine($"PASS: {passed} settings regression checks");

async Task Check(string name, Func<Task> check)
{
    await check();
    Console.WriteLine("PASS " + name);
    passed++;
}
static void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed");
}

sealed class Fixture : IDisposable
{
    public ApplicationDataPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "OneDriveSettingsRegression", Guid.NewGuid().ToString("N")));
    public AppearanceSettingsStore Store => new(Paths);
    public void Dispose()
    {
        if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
    }
}
sealed class MemoryStore : IAppearanceSettingsStore
{
    public AppearancePreferences Value = new();
    public List<AppearancePreferences> Writes = new();
    public bool Fail, FailFirst;
    public Task Gate = Task.CompletedTask;
    public Task<AppearancePreferences> LoadAsync() => Task.FromResult(Value);
    public async Task SaveAsync(AppearancePreferences value)
    {
        Writes.Add(value);
        int write = Writes.Count;
        await Gate;
        if (Fail || (FailFirst && write == 1)) throw new IOException("sensitive-path");
        Value = value;
    }
}
