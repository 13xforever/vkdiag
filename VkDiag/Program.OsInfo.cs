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
        => windowsVersion.Major switch
        {
            5 => windowsVersion.Minor switch
            {
                0 => (OsSupportStatus.Deprecated, "2000"),
                1 => (OsSupportStatus.Deprecated, "XP"),
                2 => (OsSupportStatus.Deprecated, "XP x64"),
                _ => (OsSupportStatus.Unknown, null)
            },
            6 => windowsVersion.Minor switch
            {
                0 => (OsSupportStatus.Deprecated, "Vista"),
                1 => (OsSupportStatus.Deprecated, "7"),
                2 => (OsSupportStatus.Deprecated, "8"),
                3 => (OsSupportStatus.Deprecated, "8.1"),
                _ => (OsSupportStatus.Unknown, null)
            },
            10 => windowsVersion.Build switch
            {
                // https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client
                // https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro
                var v and < 10240 => (OsSupportStatus.Deprecated, $"10 TH1 Build {v}"),
                10240 => (OsSupportStatus.Deprecated, "10 1507"), // 2017-05-09
                var v and < 10586 => (OsSupportStatus.Deprecated, $"10 TH2 Build {v}"),
                10586 => (OsSupportStatus.Deprecated, "10 1511"), // 2017-10-10
                var v and < 14393 => (OsSupportStatus.Deprecated, $"10 RS1 Build {v}"),
                14393 => (OsSupportStatus.Deprecated, "10 1607"), // 2018-04-10
                var v and < 15063 => (OsSupportStatus.Deprecated, $"10 RS2 Build {v}"),
                15063 => (OsSupportStatus.Deprecated, "10 1703"), // 2018-10-09
                var v and < 16299 => (OsSupportStatus.Deprecated, $"10 RS3 Build {v}"),
                16299 => (OsSupportStatus.Deprecated, "10 1709"), // 2019-04-09
                var v and < 17134 => (OsSupportStatus.Deprecated, $"10 RS4 Build {v}"),
                17134 => (OsSupportStatus.Deprecated, "10 1803"), // 2019-11-12
                var v and < 17763 => (OsSupportStatus.Deprecated, $"10 RS5 Build {v}"),
                17763 => (OsSupportStatus.Deprecated, "10 1809"), // 2020-11-10
                var v and < 18362 => (OsSupportStatus.Deprecated, $"10 19H1 Build {v}"),
                18362 => (OsSupportStatus.Deprecated, "10 1903"), // 2020-12-08
                18363 => (OsSupportStatus.Deprecated, "10 1909"), // 2021-05-11
                var v and < 19041 => (OsSupportStatus.Deprecated, $"10 20H1 Build {v}"),
                19041 => (OsSupportStatus.Deprecated, "10 2004"), // 2021-12-14
                19042 => (OsSupportStatus.Deprecated, "10 20H2"), // 2022-05-10
                19043 => (OsSupportStatus.Deprecated, "10 21H1"), // 2022-12-13
                19044 => (OsSupportStatus.Deprecated, "10 21H2"), // 2023-06-13
                19045 => (OsSupportStatus.Deprecated, "10 22H2"), // 2025-10-14
                // https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro
                var v and < 21390 => (OsSupportStatus.Deprecated, $"10 Dev Build {v}"),
                var v and < 22000 => (OsSupportStatus.Deprecated, $"11 21H2 Internal Build {v}"),
                22000 => (OsSupportStatus.Deprecated, "11 21H2"), // 2023-10-10
                var v and < 22621 => (OsSupportStatus.Deprecated, $"11 22H2 Beta Build {v}"),
                22621 => (OsSupportStatus.Deprecated, "11 22H2"), // 2024-10-08
                22631 => (OsSupportStatus.Supported, "11 23H2"), // 2025-11-11
                < 23000 => (OsSupportStatus.Deprecated, $"11 Beta Build {windowsVersion.Build}"),
                < 24000 => (OsSupportStatus.Deprecated, $"11 Dev Build {windowsVersion.Build}"),
                < 25000 => (OsSupportStatus.Deprecated, $"11 ??? Build {windowsVersion.Build}"),
                < 26052 => (OsSupportStatus.Deprecated, $"11 Canary Build {windowsVersion.Build}"),
                26100 => (OsSupportStatus.Supported, "11 24H2"), //2026-10-13
                < 26120 => (OsSupportStatus.Prerelease, $"11 Dev/Canary Build {windowsVersion.Build}"),
                26120 => (OsSupportStatus.Prerelease, $"11 24H2 Beta Build {windowsVersion.Build}"),
                26200 => (OsSupportStatus.Prerelease, "11 25H2"), //2027-10-12
                26220 => (OsSupportStatus.Prerelease, $"11 24H2 Dev Build {windowsVersion.Build}"),
                < 28000 => (OsSupportStatus.Prerelease, $"11 Canary Build {windowsVersion.Build}"),
                _ => (OsSupportStatus.Prerelease, $"11 Unknown/private Build {windowsVersion.Build}")
            },
            _ => (OsSupportStatus.Unknown, null)
        };

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