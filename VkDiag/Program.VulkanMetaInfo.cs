using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VkDiag.POCOs;

namespace VkDiag
{
    internal static partial class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = new SnakeCasePolicy(),
            WriteIndented = true,
        };

        private static readonly Dictionary<string, Version> KnownProblematicLayers = new Dictionary<string, Version>
        {
            ["MirillisActionVulkanLayer.json"] = null,
            ["ow-vulkan-overlay64.json"] = null,
            ["fpsmonvk64.json"] = null,
            ["fpsmonvk32.json"] = null,
            ["obs-vulkan64.json"] = new Version(1, 2, 2, 0),
            ["obs-vulkan32.json"] = new Version(1, 2, 2, 0),
        };

        private static void CheckVulkanMeta()
        {
            Console.WriteLine();
            Console.WriteLine("Vulkan registration information:");

            var basePaths = new[] {@"SOFTWARE\Khronos\Vulkan", @"SOFTWARE\WOW6432Node\Khronos\Vulkan"};
            var broken = false;
            var removedBroken = true;
            clear &= hasProperVulkanDrivers;
            foreach (var basePath in basePaths)
                using (var driversKey = Registry.LocalMachine.OpenSubKey(Path.Combine(basePath, "Drivers"), autofix || clear))
                {
                    if (driversKey == null)
                        continue;

                    var entries = driversKey.GetValueNames();
                    foreach (var driverPath in entries)
                    {
                        if (File.Exists(driverPath))
                        {
                            hasExplicitDriverReg = true;
                            if (clear)
                            {
                                RestartIfNotElevated();
                                try
                                {
                                    driversKey.DeleteValue(driverPath);
                                }
                                catch
                                {
                                    removedExplicitDriverReg = false;
#if DEBUG
                                    WriteLogLine(ConsoleColor.Red, "x", $"Failed to fix {driversKey} @{driverPath}");
#endif
                                }
                            }
                        }
                        else
                        {
                            hasBrokenEntries = broken = true;
                            if (autofix)
                            {
                                RestartIfNotElevated();
                                try
                                {
                                    driversKey.DeleteValue(driverPath);
                                }
                                catch
                                {
                                    fixedEverything = removedBroken = false;
#if DEBUG
                                    WriteLogLine(ConsoleColor.Red, "x", $"Failed to fix {driversKey} @{driverPath}");
#endif
                                }
                            }
                            else
                                fixedEverything = removedBroken = false;
                        }
                    }
                }

            if ((!hasExplicitDriverReg || removedExplicitDriverReg)
                && (!broken || removedBroken))
                WriteLogLine(ConsoleColor.Green, "+", "No explicit driver registration entries");
            else
                WriteLogLine(ConsoleColor.DarkYellow, "!", "Explicit driver registration issues");
            if (hasExplicitDriverReg)
            {
                if (removedExplicitDriverReg)
                    WriteLogLine(ConsoleColor.Green, "+", "    Removed explicit driver registration");
                else
                {
                    WriteLogLine(ConsoleColor.DarkYellow, "!", "    Explicit driver registration present (legacy)");
                    if (!hasProperVulkanDrivers)
                        WriteLogLine(ConsoleColor.DarkYellow, "!", "    Please update your GPU drivers");
                }
            }
            if (broken)
            {
                if (removedBroken)
                    WriteLogLine(ConsoleColor.Green, "+", "    Removed broken explicit Vulkan driver registration entries");
                else
                    WriteLogLine(ConsoleColor.DarkYellow, "!", "    There are broken explicit Vulkan driver registration entries");
            }

            var layersList = new List<string> {"Implicit", "Explicit"};
            var registryHiveList = new List<RegistryKey> {Registry.LocalMachine, Registry.CurrentUser};
            foreach (var rootKey in registryHiveList)
            foreach (var layer in layersList)
            foreach (var basePath in basePaths)
            {
                broken = false;
                removedBroken = true;
                var conflicts = false;
                var disabledConflicts = true;
                var layerInfoList = new List<(string path, bool broken, bool enabled, bool conflicting)>();
                using (var layerKey = rootKey.OpenSubKey(Path.Combine(basePath, layer + "Layers"), autofix || disableLayers))
                {
                    if (layerKey == null)
                        continue;

                    var registeredLayers = layerKey.GetValueNames();
                    foreach (var layerPath in registeredLayers)
                    {
                        var isBroken = !File.Exists(layerPath);
                        var isEnabled = ((int?)layerKey.GetValue(layerPath)) == 0;
                        var isConflicting = false;

                        bool disableLayer(string path)
                        {
                            hasConflictingLayers = conflicts = true;
                            if (disableLayers)
                            {
                                RestartIfNotElevated();
                                try
                                {
                                    layerKey.SetValue(path, 1);
#if DEBUG
                                    WriteLogLine(ConsoleColor.Green, "+", $"Disabled @{layerPath}");
#endif
                                    return true;
                                }
                                catch
                                {
                                    disabledConflictingLayers = disabledConflicts = false;
#if DEBUG
                                    WriteLogLine(ConsoleColor.Red, "x", $"Failed to fix {layerKey} @{layerPath}");
#endif
                                }
                            }
                            else
                            {
#if DEBUG
                                WriteLogLine(ConsoleColor.DarkYellow, "-", $"Autofix is disabled");
#endif
                                disabledConflictingLayers = disabledConflicts = false;
                            }
                            return false;
                        }

                        if (isBroken)
                        {
                            hasBrokenEntries = broken = true;
                            if (autofix)
                            {
                                RestartIfNotElevated();
                                try
                                {
                                    layerKey.DeleteValue(layerPath);
                                    continue;
                                }
                                catch
                                {
                                    fixedEverything = removedBroken = false;
#if DEBUG
                                    WriteLogLine(ConsoleColor.Red, "x", $"Failed to fix {layerKey} @{layerPath}");
#endif
                                }
                            }
                            else
                                fixedEverything = removedBroken = false;
                        }
                        else if (isEnabled)
                        {
                            var layerJsonName = Path.GetFileName(layerPath);
                            var layerInfo = GetLayerInfo(layerPath);
                            if (KnownProblematicLayers.TryGetValue(layerJsonName, out var minLayerVersion)
                                && (layerInfo.dllVer == null || minLayerVersion == null || layerInfo.dllVer < minLayerVersion))
                            {
                                isEnabled = !disableLayer(layerPath);
                                isConflicting = true;
                            }
                            else
                            {
                                var idx = -1;
                                for (var i = 0; i < layerInfoList.Count; i++)
                                {
                                    var l = layerInfoList[i];
                                    if (!l.broken && l.enabled && Path.GetFileName(l.path).Equals(layerJsonName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        idx = i;
                                        break;
                                    }
                                }
                                if (idx >= 0)
                                {
#if DEBUG
                                    WriteLogLine(ConsoleColor.Cyan, "i", $"    Found duplicate layer {layerJsonName}");
#endif
                                    var dupLayer = layerInfoList[idx];
                                    var dupLayerInfo = GetLayerInfo(dupLayer.path);
                                    var curIsNewer = true;
                                    var defVer = new Version(0, 0);

                                    curIsNewer = (layerInfo.dllVer ?? defVer) > (dupLayerInfo.dllVer ?? defVer)
                                                 || (layerInfo.apiVer ?? defVer) > (dupLayerInfo.apiVer ?? defVer);
                                    if ((layerInfo.apiVer == null || dupLayerInfo.apiVer == null)
                                        && (layerInfo.dllVer == null || dupLayerInfo.dllVer == null))
                                    {
                                        curIsNewer = StringComparer.OrdinalIgnoreCase.Compare(layerPath, dupLayer.path) > 0;
                                    }
                                    if (curIsNewer)
                                    {
#if DEBUG
                                        WriteLogLine(ConsoleColor.Cyan, "i", $"    Disabling older layer {layerJsonName}, v{dupLayerInfo.dllVer}, api v{dupLayerInfo.apiVer}");
#endif
                                        if (disableLayer(dupLayer.path))
                                            layerInfoList[idx] = (dupLayer.path, false, true, true);
                                    }
                                    else
                                    {
#if DEBUG
                                        WriteLogLine(ConsoleColor.Cyan, "i", $"    Disabling current layer {layerJsonName}, v{dupLayerInfo.dllVer}, api v{dupLayerInfo.apiVer}");
#endif
                                        isEnabled = !disableLayer(layerPath);
                                        isConflicting = true;
                                    }
                                }
                            }
                        }
                        layerInfoList.Add((layerPath, isBroken, isEnabled, isConflicting));
                    }
                }
                var is32 = basePath.Contains("WOW6432Node");
                if (layerInfoList.Count > 0)
                {
                    var msg = $"{layer} layers registration ({rootKey.Name}, {(is32 ? "32" : "64")}-bit):";
                    if (broken && !removedBroken)
                    {
                        if (layer == "Implicit")
                            WriteLogLine(ConsoleColor.Red, "x", msg);
                        else
                            WriteLogLine(ConsoleColor.DarkYellow, "!", msg);
                    }
                    else if (conflicts && !disabledConflicts)
                        WriteLogLine(ConsoleColor.DarkYellow, "!", msg);
                    else
                        WriteLogLine(ConsoleColor.Green, "+", msg);
                    foreach (var (layerPath, isBroken, isEnabled, isConflicting) in layerInfoList)
                    {
                        var color = ConsoleColor.Green;
                        var status = "+";
                        var layerInfo = GetLayerInfo(layerPath);
                        var name = layerInfo.title;
                        if (isEnabled)
                        {
                            if (isBroken)
                            {
                                status = "x";
                                if (layer == "Implicit")
                                    color = ConsoleColor.Red;
                                else
                                    color = ConsoleColor.DarkYellow;
                            }
                            else if (isConflicting)
                            {
                                color = ConsoleColor.DarkYellow;
                                status = "!";
                            }
                        }
                        else
                            status = " ";
                        name = "    " + name;
                        WriteLogLine(color, status, name);
                        if (isConflicting && isEnabled)
                            WriteLogLine(ConsoleColor.Cyan, "i", "        Please update the associated software or disable this layer");
                    }
                }
                else
                    WriteLogLine(ConsoleColor.Green, "+", $"No {(is32 ? "32" : "64")}-bit {layer.ToLower()} layers registered in {rootKey.Name}");
            }
        }

        private static (string title, Version dllVer, Version apiVer) GetLayerInfo(string layerPath)
        {
            var result = Path.GetFileName(layerPath);
            if (!File.Exists(layerPath))
                return (result, null, null);

            Version libDllVer = null;
            Version apiVer = null;
            try
            {
                var layerContent = File.ReadAllText(layerPath, Encoding.UTF8);
                var regInfo = JsonSerializer.Deserialize<VkRegInfo>(layerContent, JsonOptions);
                var layer = regInfo.Layer;
                var baseDir = Path.GetDirectoryName(layerPath);
                var layerImplLib = string.IsNullOrEmpty(layer.LibraryPath) ? null : Path.Combine(baseDir, layer.LibraryPath);
                string libVer = null;
                if (File.Exists(layerImplLib))
                {
                    var libVerInfo = FileVersionInfo.GetVersionInfo(layerImplLib);
                    if (!string.IsNullOrEmpty(libVerInfo.FileVersion))
                    {
                        libVer = ", v" + libVerInfo.FileVersion;
                        Version.TryParse(libVerInfo.FileVersion, out libDllVer);
                    }
                }

                var name = layer.Description ?? layer.Name;
                if (name.EndsWith(" vulkan layer", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - (" vulkan layer".Length)).TrimEnd();
                if (name.EndsWith(" layer", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - (" layer".Length)).TrimEnd();
                Version.TryParse(layer.ApiVersion, out apiVer);
                return ($"{name}{libVer}, API v{layer.ApiVersion} ({result})", libDllVer, apiVer);
            }
            catch
#if DEBUG
                (Exception e)
#endif
            {
#if DEBUG
                WriteLogLine(ConsoleColor.Red, "x", e.ToString());
#endif
                return (result, libDllVer, apiVer);
            }
        }
    }
}