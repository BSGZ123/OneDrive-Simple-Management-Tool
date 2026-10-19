using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Pages;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool
{
    public partial class App : Application
    {
        private readonly ApplicationDataPaths _paths = new();
        private FileStream _instanceLock;
        private bool _initializing;
        private string _clientId;
        private IPublicClientApplication _publicClient;
        private MsalCacheHelper _cache;
        private TextBlock _startupMessage;
        private Button _retry;
        private StackPanel _accessRepair;
        private Button _restoreAccess;
        private bool _accessNeedsRepair;
        private bool _accessRepaired;
        private bool _configurationNeedsAttention;
        private bool _cacheCleanupPending;
        private static Window m_window;
        public static Window StartupWindow => m_window;
        public IServiceProvider Services { get; private set; }
        public IConfigurationRoot Configuration { get; private set; }

        public App() { InitializeComponent(); }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // This surface requires no DI services or account data and can display initialization errors.
            _startupMessage = new TextBlock { Text = "Startup_Loading".GetLocalized(), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
            _retry = new Button { Content = "Configuration_Retry".GetLocalized(), IsEnabled = false };
            var exit = new Button { Content = "Startup_Exit".GetLocalized() };
            // Shown only for a damaged account access record. Every action keeps all accounts locked until it succeeds.
            var checkBackup = new Button { Content = "Startup_AccessCheckBackup".GetLocalized() };
            _restoreAccess = new Button { Content = "Startup_AccessRestore".GetLocalized(), IsEnabled = false };
            var rebuild = new Button { Content = "Startup_AccessRebuild".GetLocalized() };
            _accessRepair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
            _accessRepair.Children.Add(checkBackup);
            _accessRepair.Children.Add(_restoreAccess);
            _accessRepair.Children.Add(rebuild);
            var panel = new StackPanel { Padding = new Thickness(32), Spacing = 16 };
            panel.Children.Add(_startupMessage);
            panel.Children.Add(_accessRepair);
            panel.Children.Add(_retry);
            panel.Children.Add(exit);
            m_window = new Window { Content = panel, Title = "OneDrive Tool" };
            _retry.Click += async (_, _) => await InitializeAsync();
            checkBackup.Click += async (_, _) => await CheckAccessBackupAsync();
            _restoreAccess.Click += async (_, _) => await RepairAccessAsync(true);
            rebuild.Click += async (_, _) => await RepairAccessAsync(false);
            exit.Click += (_, _) => m_window.Close();
            m_window.Activate();
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            if (_initializing) return;
            _initializing = true;
            _retry.IsEnabled = false;
            _accessRepair.Visibility = Visibility.Collapsed;
            _accessNeedsRepair = false;
            _startupMessage.Text = "Startup_Loading".GetLocalized();
            try
            {
                await StartupInitialization.RunAsync(async phase =>
                {
                    switch (phase)
                    {
                        case StartupPhase.Settings:
                            Configuration = await StartupInitialization.ReadSettingsAsync(_paths.ApplicationDirectory);
                            _clientId = Guid.Parse(Configuration["AzureAD:ClientId"]).ToString();
                            Current.Resources["Configuration"] = Configuration;
                            break;
                        case StartupPhase.Directory:
                            if (_instanceLock == null)
                            {
                                // Older versions do not honor our file lock. Refuse migration while one is running.
                                using var current = Process.GetCurrentProcess();
                                foreach (var process in Process.GetProcessesByName(current.ProcessName))
                                {
                                    using (process) if (process.Id != current.Id) throw new IOException();
                                }
                                Directory.CreateDirectory(_paths.Root);
                                _instanceLock = new FileStream(Path.Combine(_paths.Root, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                            }
                            SafeDiagnostics.Configure(new SafeDiagnostics(_paths.Diagnostics));
                            break;
                        case StartupPhase.Cache:
                            if (_cache == null) await InitializeCacheAsync();
                            break;
                        case StartupPhase.Services:
                            if (Services == null)
                            {
                                Services = new ServiceCollection().AddSingleton(_paths).AddSingleton(_cache).AddSingleton(_publicClient)
                                    .AddSingleton<AccountAccessState>()
                                    .AddSingleton<IAccountAuthenticationService, MsalAccountAuthenticationService>()
                                    .AddSingleton<IAccountLifecycleEffects, AccountLifecycleEffects>().AddSingleton<AccountLifecycleService>()
                                    .AddSingleton<DriveConfigurationStore>().AddSingleton<TaskManagerViewModel>()
                                    .AddSingleton<IAppearanceSettingsStore, AppearanceSettingsStore>().AddSingleton<SettingViewModel>()
                                    .AddSingleton<IHomeDriveService, HomeDriveService>().AddTransient<HomeViewModel>()
                                    .AddSingleton<IBookmarkStore, BookmarkStore>().AddSingleton<IBookmarkResolver, BookmarkResolver>()
                                    .AddSingleton<ReaderWorkspace>()
                                    .AddTransient<BookmarkViewModel>()
                                    .AddSingleton(sp => new FolderSyncService(new FolderSyncStore(_paths.FolderSync), OneDrive.CreateFolderSyncTarget,
                                        sp.GetRequiredService<AccountAccessState>()))
                                    .AddSingleton<FolderSyncViewModel>().BuildServiceProvider();
                                Ioc.Default.ConfigureServices(Services);
                            }
                            break;
                        case StartupPhase.Configurations:
                            _configurationNeedsAttention = false;
                            try { await Services.GetRequiredService<AccountAccessState>().InitializeAsync(); }
                            catch (ConfigurationException exception) when (exception.Failure != ConfigurationFailure.Unavailable)
                            {
                                // Damaged, not just busy. Fail closed: every account stays blocked until repaired.
                                _accessNeedsRepair = true;
                                SafeDiagnostics.Current.Record(DiagnosticEvent.ConfigurationFailure);
                                throw;
                            }
                            try
                            {
                                var store = Services.GetRequiredService<DriveConfigurationStore>();
                                await store.LoadAsync();
                                _configurationNeedsAttention = store.LegacyCleanupPending;
                            }
                            catch
                            {
                                _configurationNeedsAttention = true;
                                SafeDiagnostics.Current.Record(DiagnosticEvent.ConfigurationFailure);
                            }
                            await Services.GetRequiredService<FolderSyncService>().InitializeAsync();
                            try { await Services.GetRequiredService<AccountLifecycleService>().RetryPendingAsync(); }
                            catch { _configurationNeedsAttention = true; }
                            await Services.GetRequiredService<SettingViewModel>().InitializeAsync();
                            break;
                    }
                });
                var startup = m_window;
                var window = new MainWindow();
                m_window = window;
                var appearance = Services.GetRequiredService<SettingViewModel>();
                appearance.AttachAppearance(preferences => Helpers.ThemeHelper.Apply(window, preferences));
                TransferWindowCloseGuard.Attach(window, Services.GetRequiredService<TaskManagerViewModel>(),
                    async () =>
                    {
                        await Services.GetRequiredService<ReaderWorkspace>().CloseAllAsync();
                        await appearance.FlushAsync();
                    });
                window.Closed += (_, _) =>
                {
                    Services.GetRequiredService<FolderSyncService>().Stop();
                    SafeDiagnostics.Current.EndSession();
                    _instanceLock?.Dispose();
                };
                window.Activate();
                startup.Close();
                if (_configurationNeedsAttention || _accessRepaired) window.Navigate(typeof(CloudPage));
                else if (Services.GetRequiredService<FolderSyncService>().ErrorKey != null) window.Navigate(typeof(FolderSyncPage));
                var notices = new List<string>();
                if (_accessRepaired) notices.Add("Startup_AccessRepaired".GetLocalized());
                if (_cacheCleanupPending) notices.Add("Startup_CacheCleanup".GetLocalized());
                if (notices.Count > 0) window.ShowStartupNotice(string.Join(" ", notices));
                SafeDiagnostics.Current.Record(DiagnosticEvent.Startup, DiagnosticLevel.Info);
            }
            catch (Exception exception)
            {
                string key = _accessNeedsRepair ? "Startup_AccessRepair" : exception is StartupException startup ? "Startup_" + startup.Phase : "Startup_Services";
                _startupMessage.Text = key.GetLocalized() + " (" + key + ")";
                SafeDiagnostics.Current.Record(DiagnosticEvent.Startup, DiagnosticLevel.Error);
                _restoreAccess.IsEnabled = false;
                if (_accessNeedsRepair) _accessRepair.Visibility = Visibility.Visible;
                _retry.IsEnabled = true;
            }
            finally { _initializing = false; }
        }

        private async Task CheckAccessBackupAsync()
        {
            if (_initializing || !_accessNeedsRepair) return;
            _initializing = true;
            try
            {
                var records = await Services.GetRequiredService<AccountAccessState>().ReadBackupAsync();
                int accounts = records.Select(x => x.Scope.AccountId).Distinct(StringComparer.Ordinal).Count();
                _startupMessage.Text = string.Format("Startup_AccessBackupReady".GetLocalized(), records.Count, accounts);
                _restoreAccess.IsEnabled = true;
            }
            catch (Exception exception) when (exception is ConfigurationException or IOException or UnauthorizedAccessException)
            {
                _startupMessage.Text = "Startup_AccessBackupUnavailable".GetLocalized();
                _restoreAccess.IsEnabled = false;
            }
            finally { _initializing = false; }
        }

        private async Task RepairAccessAsync(bool useBackup)
        {
            if (_initializing) return;
            var dialog = new ContentDialog
            {
                XamlRoot = m_window.Content.XamlRoot,
                Title = (useBackup ? "Startup_AccessRestore" : "Startup_AccessRebuild").GetLocalized(),
                Content = new TextBlock { Text = "Startup_AccessRepairExplanation".GetLocalized(), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                PrimaryButtonText = "Startup_AccessRepairAction".GetLocalized(),
                CloseButtonText = "Account_Cancel".GetLocalized(),
                DefaultButton = ContentDialogButton.Close
            };
            // Retry may have run while the dialog was open; act only on a record that still needs repair.
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _initializing || !_accessNeedsRepair) return;
            _initializing = true;
            bool repaired = false;
            try
            {
                await Services.GetRequiredService<AccountLifecycleService>().RepairAccessAsync(useBackup, await ReadSyncAccountsAsync());
                repaired = _accessRepaired = true;
            }
            catch (ConfigurationException exception) when (exception.Failure == ConfigurationFailure.Conflict)
            {
                repaired = true; // Already readable again: continue with normal startup instead of overwriting it.
            }
            catch (Exception)
            {
                _startupMessage.Text = "Startup_AccessRepairFailed".GetLocalized();
                SafeDiagnostics.Current.Record(DiagnosticEvent.ConfigurationFailure);
            }
            finally { _initializing = false; }
            // Startup then retries the journaled sign-outs, which remove credentials and pause sync.
            if (repaired) await InitializeAsync();
        }

        // Sync jobs are not started during repair. Unreadable bindings cannot run, and their error
        // keeps the journaled sign-out pending, so they cannot resume on their own either.
        private async Task<IEnumerable<string>> ReadSyncAccountsAsync()
        {
            try { return (await new FolderSyncStore(_paths.FolderSync).LoadAsync(CancellationToken.None)).Bindings.Select(x => x.AccountId); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private async Task InitializeCacheAsync()
        {
            var app = PublicClientApplicationBuilder.Create(_clientId).WithClientName("OneDriveSimpleManagementTool")
                .WithRedirectUri("http://localhost")
                .WithLogging((level, _, containsPii) =>
                {
                    if (containsPii) return;
                    SafeDiagnostics.Current.Record(DiagnosticEvent.Msal, level switch
                    {
                        LogLevel.Error => DiagnosticLevel.Error, LogLevel.Warning => DiagnosticLevel.Warning,
                        LogLevel.Info => DiagnosticLevel.Info, _ => DiagnosticLevel.Debug
                    });
                }, LogLevel.Info, enablePiiLogging: false, enableDefaultPlatformLogging: false).Build();
            var initialized = await MsalCacheInitialization.InitializeAsync(_paths, app);
            _publicClient = app;
            _cache = initialized.Cache;
            _cacheCleanupPending = initialized.CleanupPending;
        }
    }
}
