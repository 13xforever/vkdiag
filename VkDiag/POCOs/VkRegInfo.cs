using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VkDiag.POCOs;

public class VkRegInfo
{
    public string FileFormatVersion { get; set; } // Version
    public List<VkLayer> Layers { get; set; }
    public VkLayer Layer { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(VkRegInfo))]
internal partial class VkRegInfoSerializer: JsonSerializerContext;