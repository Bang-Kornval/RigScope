using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RigScope.Core.Games;
using RigScope.Core.Parsing;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

/// <summary>Finds installed games on Steam (all libraries), Riot, Epic and registry-installed launchers (Battle.net, EA, Ubisoft).</summary>
public sealed partial class GamesProbe : IProbe
{
    public string Name => "Installed games";

    public void Collect(SystemSnapshot s)
    {
        var games = new List<InstalledGame>();
        AddSteam(games);
        AddRiot(games);
        AddEpic(games);
        AddFromUninstallKeys(games);

        foreach (var g in games)
        {
            var def = GameCatalogue.Competitive.FirstOrDefault(d => d.Id == g.Id);
            g.IsCompetitive = def is not null;
            if (def is not null) g.Name = def.Name;
        }
        s.Games.AddRange(games
            .GroupBy(g => g.Id)
            .Select(grp => grp.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).First())
            .OrderByDescending(g => g.SizeBytes));
    }

    private static void AddSteam(List<InstalledGame> games)
    {
        if (SteamLocator.SteamRoot() is not { } root) return;
        foreach (var app in SteamLocator.InstalledApps(root))
        {
            if (GameCatalogue.SteamTools.Contains(app.AppId)) continue;
            var def = GameCatalogue.Competitive.FirstOrDefault(d => d.SteamAppId == app.AppId);
            games.Add(new InstalledGame
            {
                Id = def?.Id ?? $"steam:{app.AppId}",
                Name = app.Name,
                Launcher = "Steam",
                InstallPath = app.InstallPath,
                SizeBytes = app.SizeOnDisk,
                LastPlayed = app.LastPlayed,
            });
        }
    }

    private static void AddRiot(List<InstalledGame> games)
    {
        var metadata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
        if (!Directory.Exists(metadata)) return;
        foreach (var file in SafeFiles(metadata, "*.product_settings.yaml", recursive: true))
        {
            var product = Path.GetFileName(file).Split('.')[0]; // valorant, league_of_legends, bacon…
            if (product == "Riot Client") continue;
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            var m = RiotInstallPath().Match(text);
            if (!m.Success) continue;
            var path = m.Groups[1].Value.Replace('/', '\\').TrimEnd('\\');
            // Test realms (PBE) share a product id with live; list each install folder once.
            if (!Directory.Exists(path) || games.Any(g => SameOrNested(g.InstallPath, path))) continue;
            var def = GameCatalogue.Competitive.FirstOrDefault(d => d.RiotProduct == product);
            games.Add(new InstalledGame
            {
                Id = def?.Id ?? $"riot:{product}",
                Name = RiotName(product),
                Launcher = "Riot",
                InstallPath = path,
                SizeBytes = DirectorySize(path),
                LastPlayed = LastWrite(path),
            });
        }
    }

    private static string RiotName(string product) => product switch
    {
        "valorant" => "VALORANT",
        "league_of_legends" => "League of Legends",
        "teamfighttactics" => "Teamfight Tactics",
        "bacon" => "Legends of Runeterra",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(product.Replace('_', ' ')),
    };

    [GeneratedRegex(@"product_install_full_path:\s*""?([^""\r\n]+)""?")]
    private static partial Regex RiotInstallPath();

    private static void AddEpic(List<InstalledGame> games)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (var file in SafeFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var appName = r.TryGetProperty("AppName", out var a) ? a.GetString() ?? "" : "";
                var location = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
                if (location.Length == 0 || !Directory.Exists(location)) continue;
                var def = GameCatalogue.Competitive.FirstOrDefault(d => d.EpicAppName == appName);
                games.Add(new InstalledGame
                {
                    Id = def?.Id ?? $"epic:{appName}",
                    Name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? appName : appName,
                    Launcher = "Epic",
                    InstallPath = location,
                    SizeBytes = r.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var size) ? size : 0,
                    LastPlayed = LastWrite(location),
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    private static readonly string[] GamePublishers = ["Blizzard Entertainment", "Electronic Arts", "Ubisoft", "Activision", "Riot Games"];

    private static void AddFromUninstallKeys(List<InstalledGame> games)
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            foreach (var sub in Sys.RegSubKeys(hive, uninstall, view))
            {
                var key = $@"{uninstall}\{sub}";
                var name = Sys.RegStr(hive, key, "DisplayName", view);
                var publisher = Sys.RegStr(hive, key, "Publisher", view);
                var location = Sys.RegStr(hive, key, "InstallLocation", view).Trim('"');
                if (name.Length == 0 || location.Length == 0 || !Directory.Exists(location)) continue;

                var def = GameCatalogue.Competitive.FirstOrDefault(d => d.UninstallNames?.Any(u => name.StartsWith(u, StringComparison.OrdinalIgnoreCase)) == true);
                // Other games from big publishers are listed for the storage view; launchers and tools are not.
                var isOtherGame = def is null && GamePublishers.Any(p => publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    && !Regex.IsMatch(name, @"launcher|battle\.net|\bapp\b|connect|client|vanguard|redistributable", RegexOptions.IgnoreCase);
                if (def is null && !isOtherGame) continue;
                // Launchers often register the parent folder (Riot: "Teamfight Tactics" vs ".../Teamfight Tactics/Live").
                if (games.Any(g => SameOrNested(g.InstallPath, location))) continue;

                var kb = Sys.Reg(hive, key, "EstimatedSize", view) is int k ? k : 0;
                games.Add(new InstalledGame
                {
                    Id = def?.Id ?? $"app:{name}",
                    Name = name,
                    Launcher = LauncherFromPublisher(publisher),
                    InstallPath = location.TrimEnd('\\'),
                    SizeBytes = kb > 1024 * 100 ? kb * 1024L : DirectorySize(location),
                    LastPlayed = LastWrite(location),
                });
            }
        }
    }

    private static string LauncherFromPublisher(string p) =>
        p.StartsWith("Blizzard", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Activision", StringComparison.OrdinalIgnoreCase) ? "Battle.net"
        : p.StartsWith("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? "EA app"
        : p.StartsWith("Ubisoft", StringComparison.OrdinalIgnoreCase) ? "Ubisoft Connect"
        : p.StartsWith("Riot", StringComparison.OrdinalIgnoreCase) ? "Riot" : "Other";

    internal static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\') + '\\'; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    internal static bool SameOrNested(string a, string b)
    {
        var na = NormalizePath(a);
        var nb = NormalizePath(b);
        return na.StartsWith(nb, StringComparison.OrdinalIgnoreCase) || nb.StartsWith(na, StringComparison.OrdinalIgnoreCase);
    }

    internal static long DirectorySize(string path)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(path).EnumerateFiles("*", opts).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The newest write time among the folder's top-level entries: a rough "last updated or played" when the launcher doesn't record it.</summary>
    private static DateTime? LastWrite(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var times = dir.EnumerateFileSystemInfos().Select(e => e.LastWriteTime).DefaultIfEmpty(dir.LastWriteTime);
            return times.Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern, bool recursive = false)
    {
        if (!Directory.Exists(dir)) return [];
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Reads CS2's settings for every Steam account on this PC: video config, user convars, launch options and autoexec.</summary>
public sealed class Cs2Probe : IProbe
{
    public string Name => "Counter-Strike 2 settings";

    public void Collect(SystemSnapshot s)
    {
        var cs2 = s.Games.FirstOrDefault(g => g.Id == "cs2");
        if (cs2 is null || SteamLocator.SteamRoot() is not { } root) return;
        var info = new Cs2Info { InstallPath = cs2.InstallPath };

        var autoexecPath = Path.Combine(cs2.InstallPath, "game", "csgo", "cfg", "autoexec.cfg");
        var autoexec = File.Exists(autoexecPath) ? Cs2Config.ParseAutoexec(SafeRead(autoexecPath)) : null;

        foreach (var user in SteamLocator.UserDataFolders(root))
        {
            var cfg = Path.Combine(user, "730", "local", "cfg");
            var video = Path.Combine(cfg, "cs2_video.txt");
            if (!File.Exists(video)) continue;
            var account = new Cs2Account
            {
                SteamId3 = Path.GetFileName(user),
                ConfigModified = File.GetLastWriteTime(video),
                Video = Cs2Config.ParseVideo(VdfParser.TryParseFile(video)),
                Convars = Cs2Config.ParseConvars(VdfParser.TryParseFile(Path.Combine(cfg, "cs2_user_convars_0_slot0.vcfg"))),
                LaunchOptions = Cs2Config.LaunchOptions(VdfParser.TryParseFile(Path.Combine(user, "config", "localconfig.vdf"))),
                HasAutoexec = autoexec is not null,
                Autoexec = autoexec ?? [],
            };
            info.Accounts.Add(account);
        }
        info.Primary = info.Accounts.OrderByDescending(a => a.ConfigModified).FirstOrDefault();
        s.Cs2 = info;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}

public static class Cs2Config
{
    /// <summary>"setting.msaa_samples" → "msaa_samples"; "Autoconfig" stays.</summary>
    public static Dictionary<string, string> ParseVideo(VdfNode? root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var node = root?.Child("video.cfg") ?? root?.Children.Values.FirstOrDefault();
        if (node is null) return result;
        foreach (var (k, v) in node.Values)
        {
            var key = k.StartsWith("setting.", StringComparison.OrdinalIgnoreCase) ? k["setting.".Length..] : k;
            result[key.Replace('.', '_')] = v;
        }
        return result;
    }

    public static Dictionary<string, string> ParseConvars(VdfNode? root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(VdfNode n)
        {
            if (n.Child("convars") is { } c) foreach (var (k, v) in c.Values) result[k] = v;
            foreach (var child in n.Children.Values) Walk(child);
        }
        if (root is not null) Walk(root);
        return result;
    }

    public static string LaunchOptions(VdfNode? localConfig)
    {
        var apps = localConfig?.ChildPath("UserLocalConfigStore", "Software", "Valve", "Steam", "apps")
                   ?? localConfig?.ChildPath("UserLocalConfigStore", "Software", "Valve", "steam", "apps");
        return apps?.Child("730")?.Value("LaunchOptions") ?? "";
    }

    private static readonly HashSet<string> NonConvarCommands = new(StringComparer.OrdinalIgnoreCase)
        { "bind", "unbind", "alias", "echo", "exec", "say", "toggle", "incrementvar", "host_writeconfig", "unbindall", "bind_osx" };

    /// <summary>Settings in autoexec.cfg ("fps_max 0", "cl_net_buffer_ticks "0"" …). Binds and aliases are skipped.</summary>
    public static Dictionary<string, string> ParseAutoexec(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split("//")[0].Trim();
            foreach (var cmd in line.Split(';'))
            {
                var parts = cmd.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || NonConvarCommands.Contains(parts[0]) || parts[0].StartsWith('+') || parts[0].StartsWith('-')) continue;
                result[parts[0]] = parts[1].Trim().Trim('"');
            }
        }
        return result;
    }
}
