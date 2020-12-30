using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VkDiag
{
    internal static partial class Program
    {
        private static readonly HashSet<string> ServiceBlockList = new HashSet<string>{"BasicDisplay", "WUDFRd", "HyperVideo"};

        private static void CheckGpuDrivers()
        {
            var gpuGuidList = new HashSet<string>();
            var inactiveGpuGuidList = new HashSet<string>();
            var driverVkEntries = new[]
            {
                "VulkanDriverName", "VulkanDriverNameWoW",
                "VulkanImplicitLayers", "VulkanImplicitLayersWow",
                "VulkanExplicitLayers", "VulkanExplicitLayersWow",
            };
            var baseVideoPath = @"SYSTEM\CurrentControlSet\Control\Video";
            using (var videoKey = Registry.LocalMachine.OpenSubKey(baseVideoPath))
            {
                if (videoKey == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed to enumerate GPU drivers");
                    Console.ForegroundColor = defaultFgColor;
                    return;
                }

                foreach (var gpuGuid in videoKey.GetSubKeyNames())
                    using (var gpuKey = videoKey.OpenSubKey(gpuGuid))
                    {
                        var gpuSubKeys = gpuKey?.GetSubKeyNames() ?? new string[0];
                        if (gpuSubKeys.Contains("Video"))
                        {
                            if (gpuSubKeys.Contains("0000"))
                            {
                                gpuGuidList.Add(gpuGuid);
                            }
                            else
                            {
                                using (var videoSubKey = gpuKey.OpenSubKey("Video"))
                                {
                                    if ((videoSubKey?.GetValueNames().Contains("Service") ?? false)
                                        && !ServiceBlockList.Contains(videoSubKey.GetValue("Service")))
                                    {
                                        inactiveGpuGuidList.Add(gpuGuid);
                                    }
                                }
                            }
                        }
                    }
                Console.WriteLine();
                Console.WriteLine($"Found {gpuGuidList.Count} active GPU{(gpuGuidList.Count == 1 ? "" : "s")}:");
                foreach (var gpuGuid in inactiveGpuGuidList.Concat(gpuGuidList))
                    using (var gpuKey = videoKey.OpenSubKey(gpuGuid))
                    {
                        if (gpuKey == null)
                        {
                            WriteLogLine(ConsoleColor.Red, "x", $"Failed to read driver info for GPU {gpuGuid}");
                            continue;
                        }
                        string name = null;
                        if (inactiveGpuGuidList.Contains(gpuGuid))
                        {
                            using (var gpuVideoKey = gpuKey.OpenSubKey("Video"))
                                name = ((string)gpuVideoKey?.GetValue("DeviceDesc"))?.Split(';').Last()
                                       ?? ((string)gpuVideoKey?.GetValue("Service"))
                                       ?? gpuGuid;
                            WriteLogLine(defaultFgColor, "-", name);
                        }
                        else
                        {
                            var vkReg = false;
                            var broken = false;
                            var removedBroken = true;
                            var driverVer = "";
                            var driverDate = "";
                            var outputList = gpuKey.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"\d{4}")).ToList();
                            foreach (var output in outputList)
                                using (var outputKey = gpuKey.OpenSubKey(output, autofix))
                                {
                                    if (outputKey == null)
                                        continue;

                                    if (string.IsNullOrEmpty(driverVer))
                                        driverVer = outputKey.GetValue("DriverVersion") as string;
                                    if (string.IsNullOrEmpty(driverDate))
                                        driverDate = outputKey.GetValue("DriverDate") as string;
                                    if (string.IsNullOrEmpty(name))
                                        name = outputKey.GetValue("DriverDesc") as string;

                                    foreach (var entry in driverVkEntries)
                                    {
                                        var entryValue = outputKey.GetValue(entry);
                                        if (entryValue == null)
                                            continue;

                                        var fixedList = new List<string>();

                                        void validatePath(string p)
                                        {
                                            if (File.Exists(p))
                                            {
                                                fixedList.Add(p);
                                                if (entry.StartsWith("VulkanDriverName"))
                                                    hasProperVulkanDrivers = vkReg = true;
                                            }
                                            else
                                                hasBrokenEntries = broken = true;
                                        }

                                        if (entryValue is string[] multiline)
                                            foreach (var p in multiline)
                                                validatePath(p);
                                        else
                                        {
                                            var p = (string)entryValue;
                                            validatePath(p);
                                        }
                                        if (!broken)
                                            continue;

                                        if (autofix)
                                        {
                                            RestartIfNotElevated();
                                            try
                                            {
                                                if (fixedList.Count == 0)
                                                    outputKey.DeleteValue(entry);
                                                else if (fixedList.Count == 1)
                                                    outputKey.SetValue(entry, fixedList[0]);
                                                else
                                                    outputKey.SetValue(entry, fixedList.ToArray());
                                            }
                                            catch
                                            {
                                                fixedEverything = removedBroken = false;
#if DEBUG
                                                WriteLogLine(ConsoleColor.Red, "x", $"Failed to fix {outputKey} @{entry}");
#endif
                                            }
                                        }
                                        else
                                            fixedEverything = removedBroken = false;
                                    }
                                }

                            // device name with overall status
                            var color = ConsoleColor.Green;
                            var status = "+";
                            if (!broken || removedBroken)
                            {
                                if (vkReg)
                                    status = "v";
                            }
                            else
                            {
                                color = ConsoleColor.DarkYellow;
                                status = "!";
                            }
                            WriteLogLine(color, status, name);
                            // per-gpu checks
                            if (!string.IsNullOrEmpty(driverVer))
                            {
                                if (!string.IsNullOrEmpty(driverDate))
                                    driverVer += $" ({driverDate})";
                                WriteLogLine(defaultFgColor, "+", "    Driver version: " + driverVer);
                                if (DateTime.TryParseExact(driverDate, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var driverDateTime))
                                {
                                    if (driverDateTime < DateTime.UtcNow.AddMonths(-1))
                                        WriteLogLine(ConsoleColor.DarkYellow, "!", "    Please update your video driver");
                                    else if (driverDateTime < DateTime.UtcNow.AddMonths(-6))
                                        WriteLogLine(ConsoleColor.Red, "x", "    Please update your video driver");
                                }
                            }
                            if (vkReg)
                                WriteLogLine(ConsoleColor.Green, "v", "    Proper Vulkan driver registration");
                            if (broken)
                            {
                                if (removedBroken)
                                    WriteLogLine(ConsoleColor.Green, "+", "    Removed broken Vulkan registration entries");
                                else
                                    WriteLogLine(ConsoleColor.DarkYellow, "!", "    Has broken Vulkan registration entries");
                            }
                        }
                    }
            }
        }
    }
}