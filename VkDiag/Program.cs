using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Mono.Options;
using VkDiag.POCOs;

namespace VkDiag;

internal static partial class Program
{
    private const string VkDiagVersion = "1.3.8";

    private static bool isAdmin;
    private static bool autofix;
    private static bool clear;
    private static bool disableLayers;
    private static bool ignoreHighPerfCheck;

    private static bool everythingIsFine = true;
    private static bool hasBrokenEntries;
    private static bool hasProperVulkanDrivers;
    private static bool hasExplicitDriverReg;
    private static bool hasConflictingLayers;
    private static bool disabledConflictingLayers = true;
    private static bool removedExplicitDriverReg = true;
    private static bool fixedEverything = true;

    public static async Task Main(string[] args)
    {
        CheckPermissions();
        GetOptions(args);
            
        try
        {
            Console.Title = "Vulkan Diagnostics Tool v" + VkDiagVersion;
            Console.WindowWidth = Math.Min(Console.LargestWindowWidth, 100);
            Console.WindowHeight = Math.Min(Console.LargestWindowHeight, 60);
            Console.BufferWidth = Console.WindowWidth;
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch {}
            
        if (!Environment.Is64BitOperatingSystem)
        {
            WriteLogLine(ConsoleColor.Red, "Only 64-bit OS is supported");
            Environment.Exit(-1);
        }

        await CheckVkDiagVersionAsync().ConfigureAwait(false);
        var osVer = CheckOs();
        if (osVer.Major >= 10)
            try { CheckAppxPackages(); } catch { }

        var (hasInactiveGpus, hasVulkanGpus) = CheckGpuDrivers();
        if (!hasVulkanGpus)
        {
            everythingIsFine = false;
            WriteLogLine(ConsoleColor.Red, "x", "No GPUs registered with Vulkan support");
        }
        if (hasInactiveGpus && osVer.Major >= 10)
        {
            WriteLogLine();
            WriteLogLine("User GPU Preferences:");
            try
            {
                if (!HasPerformanceModeProfile())
                {
                    WriteLogLine(ConsoleColor.DarkYellow, "x", "Running without High performance GPU profile");
                    if (!ignoreHighPerfCheck)
                        Restart(false, false);
                }
                else
                    WriteLogLine(ConsoleColor.Green, "+", "Running with High performance GPU profile");
            }
            catch
            {
                WriteLogLine(ConsoleColor.DarkYellow, "x", "Failed to set High performance GPU profile");
            }
        }
        CheckVulkanMeta();

        ShowMenu();
    }

    private static async Task CheckVkDiagVersionAsync()
    {
        try
        {
            using var client = new HttpClient();
            var curVerParts = VkDiagVersion.Split([' ', '-'], 2);
            client.DefaultRequestHeaders.UserAgent.Add(new("vkdiag", curVerParts[0]));
            var responseJson = await client.GetStringAsync("https://api.github.com/repos/13xforever/vkdiag/releases").ConfigureAwait(false);
            var releaseList = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(responseJson, JsonOptions);
            releaseList = releaseList?.OrderByDescending(r => Version.TryParse(r.TagName.TrimStart('v'), out var v) ? v : null).ToList();
            var latest = releaseList?.FirstOrDefault(r => !r.Prerelease);
            var latestBeta = releaseList?.FirstOrDefault(r => r.Prerelease);
            Version.TryParse(curVerParts[0], out var curVer);
            Version.TryParse(latest?.TagName.TrimStart('v') ?? "0", out var latestVer);
            var latestBetaParts = latestBeta?.TagName.Split([' ', '-'], 2);
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
                WriteLogLine(DefaultFgColor, "+", $"    Newer prerelease version available: {latestBetaVer}");
        }
        catch
        {
            WriteLogLine(DefaultFgColor, "+", "VkDiag version: " + VkDiagVersion);
            WriteLogLine(ConsoleColor.DarkYellow, "!", $"    Failed to check for updates");
        }
    }

    private static void GetOptions(string[] args)
    {
        var help = false;
        var options = new OptionSet
        {
            {"?|h|help", _ => help = true},
            {"i|ignore-high-performance-check", _ => ignoreHighPerfCheck = true},
            {"f|fix", "Remove broken Vulkan entries", _ => autofix = true},
            {"c|clear-explicit-driver-reg", "Remove explicit Vulkan driver registration", _ => clear = true},
            {"d|disable-incompatible-layers", "Disable potentially incompatible implicit Vulkan layers", _ => disableLayers = true}
        };
        options.Parse(args);

        if (help)
        {
            WriteLogLine("RPCS3 Vulkan diagnostics tool");
            WriteLogLine("Usage:");
            WriteLogLine("  vkdiag [OPTIONS]");
            WriteLogLine("Available options:");
            lock (TheDoor) options.WriteOptionDescriptions(Console.Out);
            Environment.Exit(0);
        }
    }

    private static void CheckPermissions()
        => isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void RestartIfNotElevated() => Restart();
        
    private static void Restart(bool onlyToElevate = true, bool requireElevation = true)
    {
        if (isAdmin && onlyToElevate)
            return;
            
        if (requireElevation)
            WriteLogLine("Restarting with elevated permissions...");
        else
            WriteLogLine("Restarting...");
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
            Verb = requireElevation ? "runas" : "open",
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
                
            WriteLogLine();
            WriteLogLine("Remember to screenshot or copy this screen content for support.");
            WriteLogLine();
            WriteLogLine("There are some issues, what would you like to do?");
            foreach (var (key, prompt) in menu)
            {
                WriteLogLine(ConsoleColor.Cyan, key.ToString(), prompt);
                validResponses.Add(key);
            }
            lock (TheDoor) Console.Write("Selected option: ");
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
        
        if (everythingIsFine)
            WriteLogLine("Everything seems to be fine.");
        else
            WriteLogLine("There are some issues that require manual checks and/or fixes.");
        WriteLogLine();
        WriteLogLine("Remember to screenshot or copy this screen content for support.");
        WriteLogLine();
        WriteLogLine("Press any key to exit the tool...");
        Console.ReadKey();
        Environment.Exit(0);
    }
}