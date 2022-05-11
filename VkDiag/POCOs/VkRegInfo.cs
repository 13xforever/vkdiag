using System.Collections.Generic;

namespace VkDiag.POCOs
{
    public class VkRegInfo
    {
        public string FileFormatVersion { get; set; } // Version
        public List<VkLayer> Layers { get; set; }
        public VkLayer Layer { get; set; }
    }
}