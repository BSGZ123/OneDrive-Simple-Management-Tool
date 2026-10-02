using System.Net;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDrive_Simple_Management_Tool.Services;

namespace OneDrive_Simple_Management_Tool.Tests.ShareRegression
{
    // All Graph requests stay in this handler; no account or cloud data is used.
    internal sealed class ShareFixture : IDisposable
    {
        public ShareHandler Handler { get; } = new();
        public OneDrive Provider { get; }
        private readonly GraphServiceClient _client;

        public ShareFixture(string driveId = "test-drive")
        {
            _client = new GraphServiceClient(new HttpClient(Handler), new AnonymousAuthenticationProvider());
            Provider = new OneDrive(driveId, "test-account");
            typeof(OneDrive).GetField("graphClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Provider, _client);
        }

        public ViewModels.FileViewModel File(string id = "file-id", bool folder = false) => new(
            new ViewModels.DriveViewModel(Provider),
            new DriveItem { Id = id, Name = folder ? "folder" : "file.txt", Folder = folder ? new Folder() : null });

        public void Dispose() => _client.Dispose();
    }

    internal sealed class ShareHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public string Response { get; set; } = "{\"id\":\"permission-id\",\"link\":{\"webUrl\":\"https://example.com/share\"}}";
        public Exception Error { get; set; }
        public TaskCompletionSource Gate { get; set; }
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/createLink"))
            {
                throw new InvalidOperationException("Unexpected Graph request.");
            }
            Requests.Add((request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (Gate != null) await Gate.Task.WaitAsync(cancellationToken);
            if (Error != null) throw Error;

            return new HttpResponseMessage(Status)
            {
                Content = Status == HttpStatusCode.NoContent ? null : new StringContent(
                    (int)Status >= 400 ? "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Simulated rejection.\"}}" : Response,
                    Encoding.UTF8, "application/json")
            };
        }
    }
}

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    // Only the file/UI boundary is replaced. ShareFileViewModel and OneDrive are production sources.
    public sealed class FileViewModel(DriveViewModel drive, DriveItem item)
    {
        public DriveViewModel Drive { get; } = drive;
        public string Id => item.Id;
        public string Name => item.Name;
    }
}

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class ResourceHelper
    {
        public static string Language { get; set; } = "en-US";

        public static string GetLocalized(this string key)
        {
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", Language, "Resources.resw"));
            return document.Root!.Elements("data").Single(element => (string)element.Attribute("name") == key).Element("value")!.Value;
        }
    }
}
