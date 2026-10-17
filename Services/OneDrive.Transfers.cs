using System;
using OneDrive_Simple_Management_Tool.Models;

namespace OneDrive_Simple_Management_Tool.Services
{
    public partial class OneDrive
    {
        public IFileTransferService CreateFileTransferService()
        {
            string drive = DriveId, account = HomeAccountId;
            return new GraphFileTransferService(graphClient, drive, checkIdentity: () =>
            {
                CheckAccess();
                if (!IsAuthenticated || DriveId != drive || HomeAccountId != account)
                    throw new FileTransferException("FileOperation_AuthenticationFailed");
            });
        }
    }
}
