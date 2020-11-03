using System.Collections.Generic;

namespace VkDiag.POCOs
{
    public class VkLayer
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string LibraryPath { get; set; }
        public string ApiVersion { get; set; } // Version
        public string ImplementationVersion { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> EnableEnvironment { get; set; }
        public Dictionary<string, string> DisableEnvironment { get; set; }
    }
}