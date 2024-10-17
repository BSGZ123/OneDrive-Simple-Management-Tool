using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Configuration;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class ShareCommunityViewModel : ObservableObject
    {
        [RelayCommand]
        public async Task Refresh()
        {
            //string response = await _client.GetStringAsync(_apiUrl + "/api/links");
            //LinksResponse linksResponse = JsonSerializer.Deserialize(response, LinksResponseJsonContext.Default.LinksResponse);
            //Links = linksResponse.data;
        }

        [JsonSerializable(typeof(LinksResponse), GenerationMode = JsonSourceGenerationMode.Metadata)]
        internal partial class LinksResponseJsonContext : JsonSerializerContext { }

        [ObservableProperty] private IEnumerable<Link> links;
        //private readonly HttpClient _client = new();
        //获取配置文件中的分享服务地址(需要单独部署)
        //private readonly string _apiUrl = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build().GetSection("Tools:ShareCommunity:Url").Value;
    }
}