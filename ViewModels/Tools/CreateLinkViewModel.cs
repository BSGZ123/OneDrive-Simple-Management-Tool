using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels.Tools
{
    public partial class CreateLinkViewModel : ObservableObject
    {
        public CreateLinkViewModel(ShareCommunityViewModel community)
        {
            _community = community;
        }

        [RelayCommand]
        public Task CreateLink()
        {
            if (string.IsNullOrEmpty(Title) || string.IsNullOrEmpty(Content) || string.IsNullOrEmpty(Category))
            {
                return Task.CompletedTask;
            }
            return Task.CompletedTask;
        }


        [ObservableProperty] private string _title;
        [ObservableProperty] private string _content;
        [ObservableProperty] private string _password;
        [ObservableProperty] private DateTimeOffset _expiration;
        [ObservableProperty] private string _category = "OneDrive";

        private ShareCommunityViewModel _community;
        public List<string> Categories { get; } = new() { "OneDrive" };
        public static DateTimeOffset Today { get; } = DateTimeOffset.Now;
    }
}