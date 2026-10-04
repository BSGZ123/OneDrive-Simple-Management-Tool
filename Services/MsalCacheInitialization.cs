using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class MsalCacheInitialization
    {
        private static TraceSource QuietLogger()
        {
            // The extensions library has its own TraceSource, separate from WithLogging.
            var logger = new TraceSource("OneDriveCache", SourceLevels.Off);
            logger.Listeners.Clear();
            return logger;
        }

        public static async Task<(MsalCacheHelper Cache, bool CleanupPending)> InitializeAsync(ApplicationDataPaths paths, IPublicClientApplication app)
        {
            Directory.CreateDirectory(paths.Authentication);
            string destination = Path.Combine(paths.Authentication, "CloudFlowTokenCache.bin");
            string marker = destination + ".migrated";
            if (!File.Exists(destination) && File.Exists(marker)) throw new IOException();
            string source = File.Exists(destination) ? destination : File.Exists(paths.LegacyTokenCache) ? paths.LegacyTokenCache : null;
            if (source != null)
            {
                byte[] bytes = await ProtectedConfigurationFile<object>.ReadBoundedAsync(source, 64 * 1024 * 1024, default);
                await ValidateCopyAsync(paths.Authentication, app.AppConfig.ClientId, bytes);
                if (source != destination)
                    await ProtectedConfigurationFile<object>.WriteBytesAtomicallyAsync(destination, bytes, null, default);
            }

            var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("CloudFlowTokenCache.bin", paths.Authentication).Build(), QuietLogger());
            cache.VerifyPersistence();
            cache.RegisterCache(app.UserTokenCache);
            try { await app.GetAccountsAsync(); }
            catch { cache.UnregisterCache(app.UserTokenCache); throw; }
            bool cleanupPending = false;
            if (File.Exists(paths.LegacyTokenCache))
            {
                try
                {
                    await ProtectedConfigurationFile<object>.WriteBytesAtomicallyAsync(marker, new byte[] { 1 }, null, default);
                    File.Delete(paths.LegacyTokenCache);
                }
                catch { cleanupPending = true; }
            }
            return (cache, cleanupPending);
        }

        private static async Task ValidateCopyAsync(string parent, string clientId, byte[] bytes)
        {
            if (bytes.Length == 0) throw new IOException();
            // Let MSAL validate its format on an encrypted disposable copy. Some library failure paths
            // clear unreadable caches; those must never run against the user's only original copy.
            string directory = Path.Combine(parent, "validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "cache.bin");
            MsalCacheHelper helper = null;
            var validator = PublicClientApplicationBuilder.Create(clientId).Build();
            var validationLog = new TraceSource("OneDriveCacheValidation", SourceLevels.Warning);
            validationLog.Listeners.Clear();
            var failures = new CacheFailureListener();
            validationLog.Listeners.Add(failures);
            try
            {
                await File.WriteAllBytesAsync(file, bytes);
                helper = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("cache.bin", directory).Build(), validationLog);
                helper.RegisterCache(validator.UserTokenCache);
                await validator.GetAccountsAsync();
                if (failures.Failed || !File.Exists(file) || !bytes.SequenceEqual(await File.ReadAllBytesAsync(file))) throw new IOException();
            }
            finally
            {
                helper?.UnregisterCache(validator.UserTokenCache);
                // This directory was created by this operation and contains only its encrypted copy.
                try { Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // MSAL Extensions can report unreadable persistence through TraceSource while returning an empty
        // account list. Observe only that a warning/error occurred; never forward or retain SDK text.
        private sealed class CacheFailureListener : TraceListener
        {
            public bool Failed { get; private set; }
            public override void Write(string message) => Failed = true;
            public override void WriteLine(string message) => Failed = true;
        }
    }
}
