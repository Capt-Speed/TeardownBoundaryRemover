using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TeardownBoundaryRemover.Services;

internal sealed class SteamDiscovery
{
    public List<string> LibraryRoots { get; } = [];
    public List<string> WorkshopRoots { get; } = [];
    public string? TeardownInstallPath { get; set; }
}

internal static class SteamLocator
{
    public const string TeardownAppId = "1167630";

    private static readonly Regex PathRegex = new("\"path\"\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirRegex = new("\"installdir\"\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SteamDiscovery Discover()
    {
        var result = new SteamDiscovery();
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryAddRegistrySteamPath(steamRoots, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
        TryAddRegistrySteamPath(steamRoots, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath");
        TryAddRegistrySteamPath(steamRoots, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");

        AddIfDirectory(steamRoots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddIfDirectory(steamRoots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            AddIfDirectory(libraries, steamRoot);
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match match in PathRegex.Matches(text))
                {
                    var path = UnescapeVdf(match.Groups["value"].Value);
                    AddIfDirectory(libraries, path);
                }
            }
            catch
            {
                // Ignore a single unreadable VDF. Other libraries are still usable.
            }
        }

        foreach (var library in libraries.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            result.LibraryRoots.Add(library);

            var workshop = Path.Combine(library, "steamapps", "workshop", "content", TeardownAppId);
            if (Directory.Exists(workshop))
                result.WorkshopRoots.Add(workshop);

            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{TeardownAppId}.acf");
            if (!File.Exists(manifest))
                continue;

            try
            {
                var text = File.ReadAllText(manifest);
                var match = InstallDirRegex.Match(text);
                if (!match.Success)
                    continue;

                var installDir = UnescapeVdf(match.Groups["value"].Value);
                var candidate = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(candidate))
                    result.TeardownInstallPath ??= Path.GetFullPath(candidate);
            }
            catch
            {
                // Continue scanning other Steam libraries.
            }
        }

        return result;
    }

    private static void TryAddRegistrySteamPath(HashSet<string> output, RegistryHive hive, RegistryView view, string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is string path)
                AddIfDirectory(output, path.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            // Registry access is optional.
        }
    }

    private static void AddIfDirectory(HashSet<string> output, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (Directory.Exists(path))
                output.Add(Path.GetFullPath(path));
        }
        catch
        {
            // Ignore malformed paths.
        }
    }

    private static string UnescapeVdf(string value)
        => value.Replace("\\\\", "\\").Replace("\\/", "/");
}
