using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Mono.Options;
using VkDiag.POCOs;

namespace VkDiag
{
    internal static partial class Program
    {
        private const string VkDiagVersion = "1.1.11";

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

        public static async Task Main(string[] args)
        {
            try
            {
                Console.Title = "Vulkan Diagnostics Tool v" + VkDiagVersion;
                Console.WindowWidth = Math.Min(Console.LargestWindowWidth, 100);
                Console.WindowHeight = Math.Min(Console.LargestWindowHeight, 60);
                Console.BufferWidth = Console.WindowWidth;
            }
            catch {}
            
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
                    var curVerParts = VkDiagVersion.Split(new[] {' ', '-'}, 2);
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("vkdiag", curVerParts[0]));
                    var responseJson = await client.GetStringAsync("https://api.github.com/repos/13xforever/vkdiag/releases").ConfigureAwait(false);
                    var releaseList = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(responseJson, JsonOptions);
                    releaseList = releaseList?.OrderByDescending(r => Version.TryParse(r.TagName.TrimStart('v'), out var v) ? v : null).ToList();
                    var latest = releaseList?.FirstOrDefault(r => !r.Prerelease);
                    var latestBeta = releaseList?.FirstOrDefault(r => r.Prerelease);
                    Version.TryParse(curVerParts[0], out var curVer);
                    Version.TryParse(latest?.TagName.TrimStart('v') ?? "0", out var latestVer);
                    var latestBetaParts = latestBeta?.TagName.Split(new[] {' ', '-'}, 2);
                    Version.TryParse(latestBetaParts?[0] ?? "0", out var latestBetaVer);
                    if (latestVer > curVer || latestVer == curVer && curVerParts.Length > 1)
                    {
                        WriteLogLine(ConsoleColor.DarkYellow, "!", "VkDiag version: " + VkDiagVersion);
                        WriteLogLine(ConsoleColor.DarkYellow, "!", $"    Newer version available: {latestVer}");
                    }
                    else
                        WriteLogLine(ConsoleColor.Green, "+", "VkDiag version: " + VkDiagVersion);
                    if (latestBetaVer > latestVer
                        || (latestVer == latestBetaVer
                            && curVerParts.Length > 1
                            && (latestBetaParts?.Length > 1 && latestBetaParts[1] != curVerParts[1]
                                || (latestBetaParts?.Length ?? 0) == 0)))
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
                        if (validResponses.Contains('f'))
                            autofix = true;
                        if (validResponses.Contains('d'))
                            disableLayers = true;
                        if (validResponses.Contains('c'))
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
    }
}