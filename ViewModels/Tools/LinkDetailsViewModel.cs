using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Models;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class LinkDetailsViewModel : ObservableObject
    {
        public LinkDetailsViewModel(Link link)
        {
            _link = link;
        }

        public LinkDetailsViewModel(string linkId)
        {
        }

        public string Title => Link.title;
        public string Content => Link.content;
        public string Password => Link.password;
        public string ExpireDate => Link.expire_date.ToString();
        public string CreatedAt => Link.CreatedAt.ToString();
        public string UpdatedAt => Link.UpdatedAt.ToString();
        public int Views => Link.views;
        [ObservableProperty] private Link _link;
    }
}