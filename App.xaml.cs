using Microsoft.Identity.Client;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client.Extensions.Msal;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OneDrive_Simple_Management_Tool
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {


            m_window = new MainWindow();
            m_window.Activate();

        }


        private void LoadSettings()
        {
            Configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            Current.Resources["Configuration"] = Configuration;
        }

        private IPublicClientApplication BuildPublicApp()
        {
            IPublicClientApplication publicClientApp = PublicClientApplicationBuilder.Create(Configuration.GetSection("AzureAD:ClientId").Value)
                .WithClientName(Assembly.GetEntryAssembly().GetName().Name)
                .WithRedirectUri("http://localhost")
                .WithLogging((level, message, containsPii) =>
                {
                    Debug.WriteLine($"MSAL: {level} {message}");
                }, LogLevel.Verbose, enablePiiLogging: true, enableDefaultPlatformLogging: true)
                .Build();
            return publicClientApp;
        }

        private static async Task<MsalCacheHelper> GetCacheHelper()
        {
            string cacheFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "cache");
            var storageProperties =
                    new StorageCreationPropertiesBuilder("OneDriveTokenCache.bin", cacheFolderPath)
                    .WithLinuxKeyring(
                        "OneDriveSimTool.TokenCache",
                        MsalCacheHelper.LinuxKeyRingDefaultCollection,
                        "MSAL token cache for OneDriveSimTool.",
                        new KeyValuePair<string, string>("Version", Utils.GetVersion()),
                        new KeyValuePair<string, string>("ProductGroup", "OneDriveSimTool"))
                    .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            cacheHelper.VerifyPersistence();
            return cacheHelper;
        }


        private static Window m_window;
        public static Window StartupWindow => m_window;
        public IServiceProvider Services { get; }
        public IConfigurationRoot Configuration { get; set; }
    }
}
