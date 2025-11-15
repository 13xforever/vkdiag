using System;
using System.Collections.Generic;
using VkDiag.Interop;

namespace VkDiag;

internal static partial class Program
{
    // ReSharper disable StringLiteralTypo
    private static readonly List<(string id, string title)> KnownPackages =
    [
        ("Microsoft.D3DMappingLayers_8wekyb3d8bbwe", "OpenCL, OpenGL, and Vulkan Compatibility Pack"),
    ];
    // ReSharper restore StringLiteralTypo
    
    private static void CheckAppxPackages()
    {
        
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 1))
            return;
        
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
            catch {}
        }
        if (found is not { Count: > 0 })
            return;
        
        everythingIsFine = false;
        WriteLogLine();
        WriteLogLine(ConsoleColor.DarkYellow, "!", "Potentially incompatible software:");
        foreach (var pkg in found)
            WriteLogLine(ConsoleColor.DarkYellow, "!", $"    {pkg.name}{pkg.version}");
    }
}