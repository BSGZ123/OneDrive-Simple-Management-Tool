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
using System;
using System.Diagnostics;
using System.IO;
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
            var panel = new StackPanel { Padding = new Thickness(32), Spacing = 16 };
            panel.Children.Add(_startupMessage);
            panel.Children.Add(_retry);
            panel.Children.Add(exit);
            m_window = new Window { Content = panel, Title = "OneDrive Tool" };
            _retry.Click += async (_, _) => await InitializeAsync();
            exit.Click += (_, _) => m_window.Close();
            m_window.Activate();
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            if (_initializing) return;
            _initializing = true;
            _retry.IsEnabled = false;
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
                                    .AddSingleton<IAccountAuthenticationService, MsalAccountAuthenticationService>()
                                    .AddSingleton<DriveConfigurationStore>().AddSingleton<TaskManagerViewModel>()
                                    .AddSingleton<IAppearanceSettingsStore, AppearanceSettingsStore>().AddSingleton<SettingViewModel>()
                                    .AddSingleton<IHomeDriveService, HomeDriveService>().AddTransient<HomeViewModel>()
                                    .AddSingleton(new FolderSyncService(new FolderSyncStore(_paths.FolderSync), OneDrive.CreateFolderSyncTarget))
                                    .AddSingleton<FolderSyncViewModel>().BuildServiceProvider();
                                Ioc.Default.ConfigureServices(Services);
                            }
                            break;
                        case StartupPhase.Configurations:
                            _configurationNeedsAttention = false;
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
                            await Services.GetRequiredService<SettingViewModel>().InitializeAsync();
                            break;
                    }
                });
                var startup = m_window;
                var window = new MainWindow();
                m_window = window;
                var appearance = Services.GetRequiredService<SettingViewModel>();
                appearance.AttachAppearance(preferences => Helpers.ThemeHelper.Apply(window, preferences));
                bool waitingForPreferences = false;
                window.AppWindow.Closing += async (_, args) =>
                {
                    if (!appearance.IsSaving) return;
                    args.Cancel = true;
                    if (waitingForPreferences) return;
                    waitingForPreferences = true;
                    await appearance.FlushAsync();
                    window.Close();
                };
                window.Closed += (_, _) =>
                {
                    Services.GetRequiredService<FolderSyncService>().Stop();
                    SafeDiagnostics.Current.EndSession();
                    _instanceLock?.Dispose();
                };
                window.Activate();
                startup.Close();
                if (_configurationNeedsAttention) window.Navigate(typeof(CloudPage));
                else if (Services.GetRequiredService<FolderSyncService>().ErrorKey != null) window.Navigate(typeof(FolderSyncPage));
                if (_cacheCleanupPending) window.ShowStartupNotice("Startup_CacheCleanup".GetLocalized());
                SafeDiagnostics.Current.Record(DiagnosticEvent.Startup, DiagnosticLevel.Info);
            }
            catch (Exception exception)
            {
                string key = exception is StartupException startup ? "Startup_" + startup.Phase : "Startup_Services";
                _startupMessage.Text = key.GetLocalized() + " (" + key + ")";
                SafeDiagnostics.Current.Record(DiagnosticEvent.Startup, DiagnosticLevel.Error);
                _retry.IsEnabled = true;
            }
            finally { _initializing = false; }
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
