using System.Collections.Generic;
using Newtonsoft.Json;
using Serenity.Services;

namespace PosDashboard.Administration
{
    public class RolePermissionUpdateRequest : ServiceRequest
    {
        public int? RoleID { get; set; }
        [JsonProperty(Required = Required.AllowNull)]
        public string Module { get; set; }
        [JsonProperty(Required = Required.AllowNull)]
        public string Submodule { get; set; }
        public List<string> Permissions { get; set; }
    }
}