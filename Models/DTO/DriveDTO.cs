using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Models.DTO
{
    public class DriveDTO
    {
        public string DisplayName { get; set; }
        public ProviderDTO Provider { get; set; }
    }

    public class ProviderDTO
    {
        public string HomeAccountId { get; set; }
        public string DriveId { get; set; }
    }

    [JsonSourceGenerationOptions()]
    [JsonSerializable(typeof(List<DriveDTO>))]
    public partial class DriveDTOSourceGenerationContext : JsonSerializerContext
    {
    }
}
