using CommunityToolkit.Mvvm.ComponentModel;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public class CreateLinkViewModel : ObservableObject
    {
        public CreateLinkViewModel(ShareCommunityViewModel community)
        {
            _community = community;
        }

        private ShareCommunityViewModel _community;
    }
}