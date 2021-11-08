using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace VkDiag
{
    internal static partial class Program
    {
        private static readonly Dictionary<string, Version> VulkanLoaderExpectedVersions = new Dictionary<string, Version>
        {
            ["1"] = new Version(1, 2, 141, 0),
        };

        private static void CheckOs()
        {
            try
            {
                var scope = ManagementPath.DefaultPath.ToString();
                using (var searcher = new ManagementObjectSearcher(scope, "SELECT Name FROM CIM_Processor"))
                using (var collection = searcher.Get())
                {
                    foreach (var cpui in collection)
                    {
                        var cpuName = cpui.GetPropertyValue("Name") as string;
                        WriteLogLine(ConsoleColor.Cyan, "i", "CPU: " + cpuName);
                    }
                }
            }
#if DEBUG
            catch (Exception e)
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get CPU information");
                WriteLogLine(ConsoleColor.Red, "x", e.ToString());
            }
#else
            catch
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get CPU information");
            }
#endif
            try
            {
                var scope = ManagementPath.DefaultPath.ToString();
                using (var searcher = new ManagementObjectSearcher(scope, "SELECT Caption, Version FROM CIM_OperatingSystem"))
                using (var collection = searcher.Get())
                {
                    foreach (var osi in collection)
                    {
                        var osName = osi.GetPropertyValue("Caption") as string;
                        var osVersion = osi.GetPropertyValue("Version") as string ?? "";
                        var color = defaultFgColor;
                        var verColor = color;
                        var status = "+";
                        var verStatus = "+";
                        if (Version.TryParse(osVersion, out var osVer))
                        {
                            if (osVer.Major < 10)
                            {
                                color = ConsoleColor.DarkYellow;
                                status = "!";
                                verColor = color;
                                verStatus = status;
                            }
                            else if (osVer.Build < 19041)
                            {
                                verColor = ConsoleColor.DarkYellow;
                                verStatus = "!";
                            }
                            else
                            {
                                color = ConsoleColor.Green;
                                verColor = color;
                            }
                            var osVerName = GetWindowsVersion(osVer);
                            if (!string.IsNullOrEmpty(osVerName))
                                osVersion += $" (Windows {osVerName})";
                        }
                        WriteLogLine(color, status, "OS: " + osName);
                        WriteLogLine(verColor, verStatus, "Version: " + osVersion);
                        if (verStatus != "+")
                            WriteLogLine(verColor, "!", "    This version of Windows has reached the End of Service status for mainstream support");
                    }
                }
            }
#if DEBUG
            catch (Exception e)
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get OS information");
                WriteLogLine(ConsoleColor.Red, "x", e.ToString());
            }
#else
            catch
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get OS information");
            }
#endif      
            try
            {
                var vulkanLoaderLibs = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-?.dll", SearchOption.TopDirectoryOnly);
                if (vulkanLoaderLibs.Length == 0)
                {
                    WriteLogLine(ConsoleColor.Red, "x", "No Vulkan Loader library was found; please reinstall latest GPU drivers");
                }
                else
                {
                    foreach (var libPath in vulkanLoaderLibs)
                    {
                        try
                        {
                            var libVerInfo = FileVersionInfo.GetVersionInfo(libPath);
                            if (!string.IsNullOrEmpty(libVerInfo.FileVersion))
                            {
                                var abiVersion = Path.GetFileNameWithoutExtension(libPath).Split('-').Last();
                                var color = defaultFgColor;
                                if (Version.TryParse(libVerInfo.FileVersion, out var libDllVersion)
                                    && VulkanLoaderExpectedVersions.TryGetValue(abiVersion, out var expectedVersion)
                                    && libDllVersion >= expectedVersion)
                                    color = ConsoleColor.Green;
                                WriteLogLine(color, "+", $"System Vulkan loader version: {libVerInfo.FileVersion}");
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get system Vulkan loader info");
            }
        }

        private static string GetWindowsVersion(Version windowsVersion)
        {
            switch (windowsVersion.Major)
            {
                case 5:
                    switch (windowsVersion.Minor)
                    {
                        case 0: return "2000";
                        case 1: return "XP";
                        case 2: return "XP x64";
                        default: return null;
                    }
                case 6:
                    switch (windowsVersion.Minor)
                    {
                        case 0: return "Vista";
                        case 1: return "7";
                        case 2: return "8";
                        case 3: return "8.1";
                        default: return null;
                    }
                case 10:
                    switch (windowsVersion.Build)
                    {
                        case int v when v < 10240: return ("10 TH1 Build " + v);
                        case 10240: return "10 1507";
                        case int v when v < 10586: return ("10 TH2 Build " + v);
                        case 10586: return "10 1511";
                        case int v when v < 14393: return ("10 RS1 Build " + v);
                        case 14393: return "10 1607";
                        case int v when v < 15063: return ("10 RS2 Build " + v);
                        case 15063: return "10 1703";
                        case int v when v < 16299: return ("10 RS3 Build " + v);
                        case 16299: return "10 1709";
                        case int v when v < 17134: return ("10 RS4 Build " + v);
                        case 17134: return "10 1803";
                        case int v when v < 17763: return ("10 RS5 Build " + v);
                        case 17763: return "10 1809";
                        case int v when v < 18362: return ("10 19H1 Build " + v);
                        case 18362: return "10 1903";
                        case 18363: return "10 1909";
                        case int v when v < 19041: return ("10 20H1 Build " + v);
                        case 19041: return "10 2004";
                        case 19042: return "10 20H2";
                        case 19043: return "10 21H1";
                        case 19044: return "10 21H2";
                        case int v when v < 21390: return ("10 Dev Build " + v);
                        case int v when v < 22000: return ("11 Internal Build " + v);
                        case 22000: return "11 21H2";
                        default: return ("11 Dev Build " + windowsVersion.Build);
                    }
                default:
                    return null;
            }
        }
    }
}