using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Existing UI probes can pass through Home without account/network access.
internal static class HomeProbeServices
{
    public static IServiceCollection AddOfflineHome(this IServiceCollection services, ApplicationDataPaths paths)
    {
        services.TryAddSingleton<TaskManagerViewModel>();
        services.TryAddSingleton(new FolderSyncService(new FolderSyncStore(paths.FolderSync), _ => throw new InvalidOperationException("No sync targets in this probe")));
        services.TryAddSingleton<FolderSyncViewModel>();
        services.AddSingleton<IHomeDriveService, EmptyDrives>();
        services.AddTransient<HomeViewModel>();
        services.TryAddSingleton<IBookmarkStore>(new BookmarkStore(paths));
        services.TryAddSingleton<IBookmarkResolver, OfflineBookmarks>();
        services.TryAddTransient<BookmarkViewModel>();
        return services;
    }

    private sealed class OfflineBookmarks : IBookmarkResolver
    {
        public Task<BookmarkLocation> ResolveAsync(Bookmark bookmark, CancellationToken token) =>
            throw new BookmarkException("Bookmarks_SignInRequired");
    }

    private sealed class EmptyDrives : IHomeDriveService
    {
        public Task<IReadOnlyList<HomeDrive>> LoadDrivesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<HomeDrive>>(Array.Empty<HomeDrive>());
        public Task<HomeQuota> GetQuotaAsync(HomeDrive drive, CancellationToken token) => throw new InvalidOperationException("No drives in this probe");
    }
}
