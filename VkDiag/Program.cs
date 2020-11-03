using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Mono.Options;
using VkDiag.POCOs;

namespace VkDiag
{
    internal static class Program
    {
        //private static readonly ConsoleColor defaultBgColor = Console.BackgroundColor;
        private static readonly ConsoleColor defaultFgColor = Console.ForegroundColor;
        private const string VkDiagVersion = "1.1.0";

        private static bool isAdmin = false;
        private static bool autofix = false;
        private static bool clear = false;
        private static bool disableLayers = false;

        private static bool hasBrokenEntries = false;
        private static bool hasProperVulkanDrivers = false;
        private static bool hasExplicitDriverReg = false;
        private static bool hasConflictingLayers = false;
        private static bool disabledConflictingLayers = true;
        private static bool removedExplicitDriverReg = true;
        private static bool fixedEverything = true;

        private static readonly Dictionary<string, Version> KnownProblematicLayers = new Dictionary<string, Version>
        {
            ["MirillisActionVulkanLayer.json"] = null,
            ["obs-vulkan64.json"] = new Version(1, 2, 2, 0),
            ["obs-vulkan32.json"] = new Version(1, 2, 2, 0),
        };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = new SnakeCasePolicy(),
            WriteIndented = true,
        };
        
        public static async Task Main(string[] args)
        {
            Console.Title = "Vulkan Diagnostics Tool v" + VkDiagVersion;
            if (!Environment.Is64BitOperatingSystem)
            {
                Console.WriteLine("Only 64-bit OS is supported");
                Environment.Exit(-1);
            }

            GetOptions(args);
            CheckPermissions();
            await CheckVkDiagVersionAsync().ConfigureAwait(false);
            CheckOs();

            CheckGpuDrivers();
            CheckVulkanMeta();

            ShowMenu();
        }

        private static async Task CheckVkDiagVersionAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("vkdiag", VkDiagVersion));
                    var responseJson = await client.GetStringAsync("https://api.github.com/repos/13xforever/vkdiag/releases").ConfigureAwait(false);
                    var releaseList = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(responseJson, JsonOptions);
                    releaseList = releaseList.OrderByDescending(r => Version.TryParse(r.TagName.TrimStart('v'), out var v) ? v : null).ToList();
                    var latest = releaseList.FirstOrDefault(r => !r.Prerelease);
                    var latestBeta = releaseList.FirstOrDefault(r => r.Prerelease);
                    Version.TryParse(VkDiagVersion, out var curVer);
                    Version.TryParse(latest?.TagName.TrimStart('v') ?? "0", out var latestVer);
                    Version.TryParse(latestBeta?.TagName.TrimStart('v') ?? "0", out var latestBetaVer);
                    if (latestVer > curVer)
                    {
                        WriteLogLine(ConsoleColor.DarkYellow, "!", "VkDiag version: " + VkDiagVersion);
                        WriteLogLine(ConsoleColor.DarkYellow, "!", $"    Newer version available: {latestVer}");
                    }
                    else
                        WriteLogLine(ConsoleColor.Green, "+", "VkDiag version: " + VkDiagVersion);
                    if (latestBetaVer > latestVer)
                        WriteLogLine(defaultFgColor, "+", $"    Newer prerelease version available: {latestBetaVer}");
                }
            }
            catch
            {
                WriteLogLine(defaultFgColor, "+", "VkDiag version: " + VkDiagVersion);
                WriteLogLine(ConsoleColor.DarkYellow, "!", $"    Failed to check for updates");
            }
        }

        private static void GetOptions(string[] args)
        {
            var help = false;
            var options = new OptionSet
            {
                {"?|h|help", _ => help = true},
                {"f|fix", "Remove broken Vulkan entries", _ => autofix = true},
                {"c|clear-explicit-driver-reg", "Remove explicit Vulkan driver registration", _ => clear = true},
                {"d|disable-incompatible-layers", "Disable potentially incompatible implicit Vulkan layers", _ => disableLayers = true}
            };
            options.Parse(args);

            if (help)
            {
                Console.WriteLine("RPCS3 Vulkan diagnostics tool");
                Console.WriteLine("Usage:");
                Console.WriteLine("  vkdiag [OPTIONS]");
                Console.WriteLine("Available options:");
                options.WriteOptionDescriptions(Console.Out);
                Environment.Exit(0);
            }
        }

        private static void CheckPermissions()
            => isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        private static void RestartIfNotElevated() => Restart();
        
        private static void Restart(bool onlyToElevate = true)
        {
            if (isAdmin && onlyToElevate)
                return;
            
            Console.WriteLine("Restarting with elevated permissions...");
            var args = "";
            if (autofix)
                args += " -f";
            if (clear)
                args += " -c";
            if (disableLayers)
                args += " -d";
            args = args.TrimStart();
            var cmd = Environment.GetCommandLineArgs()[0];
            var wtProfile = Environment.GetEnvironmentVariable("WT_PROFILE_ID");
            if (!string.IsNullOrEmpty(wtProfile))
            {
                args = $"\"{cmd}\" {args}";
                cmd = "wt";
            }
            var psi = new ProcessStartInfo
            {
                Verb = "runAs",
                UseShellExecute = true,
                FileName = cmd,
                Arguments = args,
            };
            Process.Start(psi);
            Environment.Exit(0);
        }

        private static void CheckOs()
        {
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
                        if (Version.TryParse(osVersion, out var osv))
                        {
                            var osVerName = GetWindowsVersion(osv);
                            if (!string.IsNullOrEmpty(osVerName))
                                osVersion += $" (Windows {osVerName})";
                        }
                        var color = defaultFgColor;
                        var status = "+";
                        if (Version.TryParse(osVersion, out var osVer))
                        {
                            if (osVer.Major < 10)
                                color = ConsoleColor.DarkYellow;
                            else
                                color = ConsoleColor.Green;
                        }
                        WriteLogLine(color, status, "OS: " + osName);
                        WriteLogLine(color, status, "Version: " + osVersion);
                    }
                }
            }
            catch
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to get OS information");
            }
        }

        private static readonly HashSet<string> serviceBlockList = new HashSet<string>{"BasicDisplay", "WUDFRd", "HyperVideo"};

        private static void WriteLogLine(ConsoleColor statusColor, string status, string description)
        {
            var val = description.TrimStart();
            string prefix = "";
            if (val.Length < description.Length)
                prefix = description.Substring(0, description.Length - val.Length);
            Console.Write(prefix + '[');
            Console.ForegroundColor = statusColor;
            Console.Write(status);
            Console.ForegroundColor = defaultFgColor;
            Console.WriteLine("] " + val);
        }
        
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
                                        && !serviceBlockList.Contains(videoSubKey.GetValue("Service")))
                                    {
                                        inactiveGpuGuidList.Add(gpuGuid);
                                    }
                                }
                            }
                        }
                    }
                Console.WriteLine();
                Console.WriteLine($"Found {gpuGuidList.Count} active GPU{(gpuGuidList.Count == 1 ? "": "s")}:");
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
                WriteLogLine(ConsoleColor.Green, "+", "Clean explicit driver registration");
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
            foreach (var layer in layersList)
            foreach (var basePath in basePaths)
            {
                broken = false;
                removedBroken = true;
                var conflicts = false;
                var disabledConflicts = true;
                var layerInfoList = new List<(string path, bool broken, bool enabled, bool conflicting)>();
                using (var layerKey = Registry.LocalMachine.OpenSubKey(Path.Combine(basePath, layer + "Layers"), autofix || disableLayers))
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
                if (layerInfoList.Count > 0)
                {
                    var is32 = basePath.Contains("WOW6432Node");
                    var msg = $"{layer} layers registration ({(is32 ? "32" : "64")}-bit):";
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
                    }
                }
                else
                    WriteLogLine(ConsoleColor.Green, "+", $"No {layer.ToLower()} layers registered");
            }
        }

        private static void ShowMenu()
        {
            var menu = new List<(char key, string prompt)>();
            if (hasBrokenEntries && !fixedEverything)
                menu.Add(('f', "Remove broken entries"));
            if (hasConflictingLayers && !disabledConflictingLayers)
                menu.Add(('d', "Disable incompatible Vulkan layers"));
            if (hasExplicitDriverReg && hasProperVulkanDrivers)
                menu.Add(('c', "Clear explicit (legacy) Vulkan driver registration"));
            if (menu.Count > 1)
                menu.Add(('a', "All of the above"));
            if (menu.Count > 0)
            {
                menu.Add(('n', "Do nothing and exit (default)"));
                var validResponses = new HashSet<char>{'\r', '\n'};
                
                Console.WriteLine();
                Console.WriteLine("Remember to screenshot or copy this screen content for support.");
                Console.WriteLine();
                Console.WriteLine("There are some issues, what would you like to do?");
                foreach (var (key, prompt) in menu)
                {
                    WriteLogLine(ConsoleColor.Cyan, key.ToString(), prompt);
                    validResponses.Add(key);
                }
                Console.Write("Selected option: ");
                char result;
                do
                {
                    var key = Console.ReadKey(true);
                    result = char.ToLower(key.KeyChar);
                } while (!validResponses.Contains(result));
                switch (result)
                {
                    case 'a':
                        autofix = true;
                        disableLayers = true;
                        clear = true;
                        break;
                    case 'f':
                        autofix = true;
                        break;
                    case 'd':
                        disableLayers = true;
                        break;
                    case 'c':
                        clear = true;
                        break;
                    default:
                        Environment.Exit(0);
                        break;
                }
                Restart(false);
                Environment.Exit(0);
            }
            Console.WriteLine("Everything seems to be fine.");
            Console.WriteLine();
            Console.WriteLine("Remember to screenshot or copy this screen content for support.");
            Console.WriteLine();
            Console.WriteLine("Press any key to exit the tool...");
            Console.ReadKey();
            Environment.Exit(0);
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
                        case int v when v < 19536: return ("10 Beta Build " + v);
                        default: return ("10 21H1 Build " + windowsVersion.Build);
                    }
                default: return null;
            }
        }
    }
}