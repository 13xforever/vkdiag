using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Mono.Options;

namespace VkDiag
{
    internal static class Program
    {
        private static bool isAdmin = false;
        private static bool autofix = false;
        private static bool clear = false;
        private static bool disableLayers = false;

        //private static readonly ConsoleColor defaultBgColor = Console.BackgroundColor;
        private static readonly ConsoleColor defaultFgColor = Console.ForegroundColor;

        private static bool hasBrokenEntries = false;
        private static bool hasProperVulkanDrivers = false;
        private static bool hasExplicitDriverReg = false;
        private static bool hasConflictingLayers = false;
        private static bool disabledConflictingLayers = true;
        private static bool removedExplicitDriverReg = true;
        private static bool fixedEverything = true;
        
        public static void Main(string[] args)
        {
            Console.Title = "Vulkan Diagnostics Tool";
            if (!Environment.Is64BitOperatingSystem)
            {
                Console.WriteLine("Only 64-bit OS is supported");
                Environment.Exit(-1);
            }
            
            GetOptions(args);
            CheckPermissions();
            CheckOs();

            CheckGpuDrivers();
            CheckVulkanMeta();

            ShowMenu();
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

        private static void RestartIfNotElevated()
        {
            if (isAdmin)
                return;
            
            Console.WriteLine("Restarting with elevated permissions...");
            var args = "";
            if (autofix)
                args += " -f";
            if (clear)
                args += " -c";
            if (disableLayers)
                args += " -d";
            var psi = new ProcessStartInfo
            {
                Verb = "runAs",
                UseShellExecute = true,
                FileName = Environment.GetCommandLineArgs()[0],
                Arguments = args.TrimStart(),
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
                        Console.WriteLine($"OS: {osi.GetPropertyValue("Caption")}");
                        Console.WriteLine($"Version: {osi.GetPropertyValue("Version")}");
                    }
                }
            }
            catch
            {
                Console.WriteLine("Failed to get OS information");
            }
        }

        private static readonly HashSet<string> serviceBlockList = new HashSet<string>{"BasicDisplay", "WUDFRd"};

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
                                using (var outputKey = gpuKey.OpenSubKey(output))
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
            Console.WriteLine("Vulkan registration information:");

            var knownProblematicLayers = new HashSet<string>
            {
                "MirillisActionVulkanLayer.json",
                "obs-vulkan64.json",
            };
            var basePaths = new[] {@"SOFTWARE\Khronos\Vulkan", @"SOFTWARE\WOW6432Node\Khronos\Vulkan"};
            
            var broken = false;
            var removedBroken = true;
            clear &= hasProperVulkanDrivers;
            foreach (var basePath in basePaths)
                using (var driversKey = Registry.LocalMachine.OpenSubKey(Path.Combine(basePath, "Drivers")))
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
                var layerInfoList = new List<(string path, bool broken, bool enabled)>();
                using (var layerKey = Registry.LocalMachine.OpenSubKey(Path.Combine(basePath, layer + "Layers")))
                {
                    if (layerKey == null)
                        continue;

                    var registeredLayers = layerKey.GetValueNames();
                    foreach (var layerPath in registeredLayers)
                    {
                        var isBroken = !File.Exists(layerPath);
                        var isEnabled = ((int?)layerKey.GetValue(layerPath)) == 0;
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
                                }
                            }
                            else
                                fixedEverything = removedBroken = false;
                        }
                        else if (isEnabled && knownProblematicLayers.Contains(Path.GetFileName(layerPath)))
                        {
                            hasConflictingLayers = conflicts = true;
                            if (autofix)
                            {
                                RestartIfNotElevated();
                                try
                                {
                                    layerKey.SetValue(layerPath, 1);
                                    isEnabled = false;
                                }
                                catch
                                {
                                    disabledConflictingLayers = disabledConflicts = false;
                                }
                            }
                            else
                                disabledConflictingLayers = disabledConflicts = false;
                        }
                        layerInfoList.Add((layerPath, isBroken, isEnabled));
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
                    foreach (var (layerPath, isBroken, isEnabled) in layerInfoList)
                    {
                        var color = ConsoleColor.Green;
                        var status = "+";
                        var name = Path.GetFileName(layerPath);
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
                            else if (knownProblematicLayers.Contains(name))
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
                Console.WriteLine("Remember to screenshot or copy this screen content if you were asked to.");
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
                RestartIfNotElevated();
                Environment.Exit(0);
            }
            Console.WriteLine("Everything seems to be fine.");
            Console.WriteLine("Remember to screenshot or copy this screen content if you were asked to.");
            Console.WriteLine("Press any key to exit the tool...");
            Console.ReadKey();
            Environment.Exit(0);
        }
    }
}