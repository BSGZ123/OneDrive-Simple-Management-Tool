using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public enum StartupPhase { Settings, Directory, Cache, Services, Configurations }
    public sealed class StartupException(StartupPhase phase) : Exception(phase.ToString())
    {
        public StartupPhase Phase { get; } = phase;
    }

    public static class StartupInitialization
    {
        public static async Task RunAsync(Func<StartupPhase, Task> initialize)
        {
            foreach (StartupPhase phase in Enum.GetValues<StartupPhase>())
            {
                try { await initialize(phase); }
                catch { throw new StartupException(phase); }
            }
        }

        public static async Task<IConfigurationRoot> ReadSettingsAsync(string applicationDirectory, CancellationToken token = default)
        {
            byte[] bytes = await ProtectedConfigurationFile<object>.ReadBoundedAsync(
                Path.Combine(applicationDirectory, "appsettings.json"), 1024 * 1024, token);
            // Use the application's JSON provider so BOMs, comments and key casing have identical semantics.
            // Return this validated snapshot instead of reopening the file during startup.
            using var stream = new MemoryStream(bytes, writable: false);
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            if (!Guid.TryParse(configuration["AzureAD:ClientId"], out var clientId) || clientId == Guid.Empty)
            {
                (configuration as IDisposable)?.Dispose();
                throw new InvalidDataException();
            }
            return configuration;
        }
    }
}
