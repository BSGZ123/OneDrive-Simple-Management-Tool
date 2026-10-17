using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IDownloadSession
    {
        string DestinationPath { get; }
        DownloadSnapshot Snapshot { get; }
        event Action<DownloadSnapshot> Changed;
        Task StartAsync();
        Task PauseAsync();
        Task CancelAsync();
    }
}
