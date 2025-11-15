using System;
using Mono.Options;

namespace VkDiag;

internal static partial class Program
{
    private static void GetOptions(string[] args)
    {
        var help = false;
        var options = new OptionSet
        {
            {"?|h|help", _ => help = true},
            {"i|ignore-high-performance-check", _ => ignoreHighPerfCheck = true},
            {"f|fix", "Remove broken Vulkan entries", _ => autofix = true},
            {"c|clear-explicit-driver-reg", "Remove explicit Vulkan driver registration", _ => clear = true},
            {"d|disable-incompatible-layers", "Disable potentially incompatible implicit Vulkan layers", _ => disableLayers = true}
        };
        options.Parse(args);

        if (help)
        {
            WriteLogLine("RPCS3 Vulkan diagnostics tool");
            WriteLogLine("Usage:");
            WriteLogLine("  vkdiag [OPTIONS]");
            WriteLogLine("Available options:");
            lock (TheDoor) options.WriteOptionDescriptions(Console.Out);
            Environment.Exit(0);
        }
    }
}