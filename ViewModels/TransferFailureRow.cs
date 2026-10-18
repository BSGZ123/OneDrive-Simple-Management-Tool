using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public sealed class TransferFailureRow
    {
        public string Path { get; }
        public string Message { get; }
        public IAsyncRelayCommand RetryCommand { get; }
        public TransferFailureRow(string path, string message, Func<Task> retry, Func<bool> canRetry = null)
        {
            Path = path; Message = message;
            RetryCommand = new AsyncRelayCommand(retry, canRetry ?? (() => true));
        }
    }
}
