using Microsoft.Win32;
using RigScope.Core.Parsing;
using RigScope.Core.Probes;

namespace RigScope.Core.Games;

public sealed record SteamApp(int AppId, string Name, string InstallPath, long SizeOnDisk, DateTime? LastPlayed);

/// <summary>Finds Steam, all its library folders and the games in them, from Steam's own files.</summary>
public static class SteamLocator
{
    public static string? SteamRoot()
    {
        var path = Sys.RegStr(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (path.Length == 0) path = Sys.RegStr(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", RegistryView.Registry32);
        if (path.Length == 0) return null;
        path = path.Replace('/', '\\');
        return Directory.Exists(path) ? path : null;
    }

    public static List<string> Libraries(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        var vdf = VdfParser.TryParseFile(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
        var folders = vdf?.Child("libraryfolders");
        if (folders is not null)
        {
            foreach (var lib in folders.Children.Values)
                if (lib.Value("path") is { Length: > 0 } p) libs.Add(p.Replace(@"\\", @"\"));
        }
        return libs.Select(l => l.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public static List<SteamApp> InstalledApps(string steamRoot)
    {
        var apps = new List<SteamApp>();
        foreach (var lib in Libraries(steamRoot))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in manifests)
                if (ParseManifest(VdfParser.TryParseFile(file), steamapps) is { } app) apps.Add(app);
        }
        return apps;
    }

    public static SteamApp? ParseManifest(VdfNode? root, string steamappsDir)
    {
        var state = root?.Child("AppState");
        if (state is null || !int.TryParse(state.Value("appid"), out var id)) return null;
        long.TryParse(state.Value("SizeOnDisk"), out var size);
        DateTime? lastPlayed = long.TryParse(state.Value("LastPlayed"), out var lp) && lp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(lp).LocalDateTime : null;
        var dir = Path.Combine(steamappsDir, "common", state.Value("installdir") ?? "");
        return new SteamApp(id, state.Value("name") ?? $"App {id}", dir, size, lastPlayed);
    }

    /// <summary>userdata\&lt;SteamID3&gt; folders (one per Steam account that has signed in on this PC).</summary>
    public static IEnumerable<string> UserDataFolders(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        try { return Directory.EnumerateDirectories(userdata).Where(d => Path.GetFileName(d).All(char.IsDigit) && Path.GetFileName(d) != "0").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
