using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using Microsoft.Win32;

namespace VkDiag;

internal static partial class Program
{
    private static readonly Dictionary<string, Version> VulkanLoaderExpectedVersions = new()
    {
        ["1"] = new(1, 2, 141, 0),
    };

    private static Version CheckOs()
    {
        Version osVer = default;
        try
        {
            var scope = ManagementPath.DefaultPath.ToString();
            using var searcher = new ManagementObjectSearcher(scope, "SELECT Name FROM CIM_Processor");
            using var collection = searcher.Get();
            foreach (var cpuInfo in collection)
            {
                var cpuName = cpuInfo.GetPropertyValue("Name") as string;
                WriteLogLine(ConsoleColor.Cyan, "i", "CPU: " + cpuName);
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
            using var searcher = new ManagementObjectSearcher(scope, "SELECT Caption, Version FROM CIM_OperatingSystem");
            using var collection = searcher.Get();
            foreach (var osi in collection)
            {
                var osName = osi.GetPropertyValue("Caption") as string;
                var osVersion = osi.GetPropertyValue("Version") as string ?? "";
                var color = DefaultFgColor;
                var verColor = color;
                var status = "+";
                var verStatus = "+";
                var osStatus = OsSupportStatus.Unknown;
                if (Version.TryParse(osVersion, out osVer))
                {
                    (osStatus, var osVerName) = GetWindowsInfo(osVer);
                    if (!string.IsNullOrEmpty(osVerName))
                        osVersion += $" (Windows {osVerName})";
                }
                if (osStatus != OsSupportStatus.Unknown)
                {
                    if (osStatus == OsSupportStatus.Deprecated)
                    {
                        color = ConsoleColor.DarkYellow;
                        status = "!";
                        verColor = color;
                        verStatus = status;
                    }
                    else if (osStatus == OsSupportStatus.Prerelease)
                    {
                        verColor = ConsoleColor.DarkYellow;
                        verStatus = "!";
                    }
                    else
                    {
                        color = ConsoleColor.Green;
                        verColor = color;
                    }
                }
                WriteLogLine(color, status, "OS: " + osName);
                WriteLogLine(verColor, verStatus, "Version: " + osVersion);
                if (osStatus == OsSupportStatus.Deprecated)
                    WriteLogLine(verColor, "!", "    This version of Windows has reached the End of Service status for mainstream support");
                else if (osStatus == OsSupportStatus.Prerelease)
                    WriteLogLine(verColor, "!", "    This version of Windows is a pre-release software and may contain all kinds of issues");
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
                everythingIsFine = false;
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
                            var color = DefaultFgColor;
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
        return osVer;
    }

    private static (OsSupportStatus status, string name) GetWindowsInfo(Version windowsVersion)
    {
        switch (windowsVersion.Major)
        {
            case 5:
                switch (windowsVersion.Minor)
                {
                    case 0: return (OsSupportStatus.Deprecated, "2000");
                    case 1: return (OsSupportStatus.Deprecated, "XP");
                    case 2: return (OsSupportStatus.Deprecated, "XP x64");
                    default: return (OsSupportStatus.Unknown, null);
                }
            case 6:
                switch (windowsVersion.Minor)
                {
                    case 0: return (OsSupportStatus.Deprecated, "Vista");
                    case 1: return (OsSupportStatus.Deprecated, "7");
                    case 2: return (OsSupportStatus.Deprecated, "8");
                    case 3: return (OsSupportStatus.Deprecated, "8.1");
                    default: return (OsSupportStatus.Unknown, null);
                }
            case 10:
                switch (windowsVersion.Build)
                {
                    // https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro
                    case int v when v < 10240: return (OsSupportStatus.Deprecated, $"10 TH1 Build {v}");
                    case 10240: return (OsSupportStatus.Deprecated, "10 1507");
                    case int v when v < 10586: return (OsSupportStatus.Deprecated, $"10 TH2 Build {v}");
                    case 10586: return (OsSupportStatus.Deprecated, "10 1511");
                    case int v when v < 14393: return (OsSupportStatus.Deprecated, $"10 RS1 Build {v}");
                    case 14393: return (OsSupportStatus.Deprecated, "10 1607");
                    case int v when v < 15063: return (OsSupportStatus.Deprecated, $"10 RS2 Build {v}");
                    case 15063: return (OsSupportStatus.Deprecated, "10 1703");
                    case int v when v < 16299: return (OsSupportStatus.Deprecated, $"10 RS3 Build {v}");
                    case 16299: return (OsSupportStatus.Deprecated, "10 1709");
                    case int v when v < 17134: return (OsSupportStatus.Deprecated, $"10 RS4 Build {v}");
                    case 17134: return (OsSupportStatus.Deprecated, "10 1803");
                    case int v when v < 17763: return (OsSupportStatus.Deprecated, $"10 RS5 Build {v}");
                    case 17763: return (OsSupportStatus.Deprecated, "10 1809");
                    case int v when v < 18362: return (OsSupportStatus.Deprecated, $"10 19H1 Build {v}");
                    case 18362: return (OsSupportStatus.Deprecated, "10 1903");
                    case 18363: return (OsSupportStatus.Deprecated, "10 1909");
                    case int v when v < 19041: return (OsSupportStatus.Deprecated, $"10 20H1 Build {v}");
                    case 19041: return (OsSupportStatus.Deprecated, "10 2004");
                    case 19042: return (OsSupportStatus.Deprecated, "10 20H2");
                    case 19043: return (OsSupportStatus.Deprecated, "10 21H1");
                    case 19044: return (OsSupportStatus.Deprecated, "10 21H2");
                    case 19045: return (OsSupportStatus.Supported, "10 22H2");
                        
                    // https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro
                    case int v when v < 21390: return (OsSupportStatus.Deprecated, $"10 Dev Build {v}");
                    case int v when v < 22000: return (OsSupportStatus.Deprecated, $"11 21H2 Internal Build {v}");
                    case 22000: return (OsSupportStatus.Supported, "11 21H2");
                    case int v when v < 22621: return (OsSupportStatus.Deprecated, $"11 22H2 Beta Build {v}");
                    case 22621: return (OsSupportStatus.Supported, "11 22H2");
                    case 22631: return (OsSupportStatus.Supported, "11 23H2");
                    case int v when v < 23000: return (OsSupportStatus.Prerelease, $"11 Beta Build {windowsVersion.Build}");
                    case int v when v < 24000: return (OsSupportStatus.Prerelease, $"11 Dev Build {windowsVersion.Build}");
                    case int v when v < 25000: return (OsSupportStatus.Prerelease, $"11 ??? Build {windowsVersion.Build}");
                    case int v when v < 26052: return (OsSupportStatus.Prerelease, $"11 Canary Build {windowsVersion.Build}");
                    case int v when v < 27000: return (OsSupportStatus.Prerelease, $"11 Dev/Canary Build {windowsVersion.Build}");
                    default: return (OsSupportStatus.Prerelease, $"11 Unknown/private Build {windowsVersion.Build}");
                }
            default:
                return (OsSupportStatus.Unknown, null);
        }
    }

    private static bool HasPerformanceModeProfile()
    {
        if (Assembly.GetEntryAssembly()?.Location is not { } imagePath)
            return false;
        
        var basePath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        using var userGpuPrefs = Registry.CurrentUser.OpenSubKey(basePath, true);
        if (userGpuPrefs is null)
            return true;

        var globalPrefValue = userGpuPrefs.GetValue("DirectXUserGlobalSettings") as string;
        if (globalPrefValue?.Contains("GpuPreference=2") ?? false)
            return true;
                
        var profile = userGpuPrefs.GetValueNames().Any(v => v == imagePath);
        if (profile)
        {
            var curVal = userGpuPrefs.GetValue(imagePath) as string;
            if (curVal?.Contains("GpuPreference=2") ?? false)
                return true;
        }
                
        userGpuPrefs.SetValue(imagePath, "GpuPreference=2;");
        return false;
    }
}