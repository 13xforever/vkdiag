using System;
using System.Collections.Generic;
using Windows.Win32;
using Windows.Win32.Foundation;
using VkDiag.Interop;

namespace VkDiag;

internal static partial class Program
{
    private static readonly List<(string id, string title)> KnownPackages =
    [
        ("Microsoft.D3DMappingLayers_8wekyb3d8bbwe", "OpenCL, OpenGL, and Vulkan Compatibility Pack"),
    ];
    
    private static unsafe void CheckAppxPackages()
    {
        var found = new List<(string name, string version)>();
        foreach (var pkg in KnownPackages)
        {
            try
            {
                var pkgFullNameList = PackageManager.FindPackagesByPackageFamily(pkg.id);
                foreach (var pkgFullName in pkgFullNameList)
                {
                    var appStoreName = PackageManager.GetAppStoreName(pkgFullName, pkg.title);
                    var ver = PackageManager.GetPackageVersion(pkgFullName, "");
                    found.Add((appStoreName, ver));
                }
            }
            catch
            {}
        }
        if (found is { Count: > 0 })
        {
            Console.WriteLine();
            WriteLogLine(ConsoleColor.DarkYellow, "!", "Potentially incompatible software:");
            foreach (var pkg in found)
                WriteLogLine(ConsoleColor.DarkYellow, "!", $"    {pkg.name}{pkg.version}");
        }
    }
}