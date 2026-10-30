namespace RigScope.Core.Games;

/// <summary>A competitive FPS RigScope knows about, and how to find it on each launcher.</summary>
public sealed record GameDefinition(
    string Id,
    string Name,
    int? SteamAppId = null,
    string? EpicAppName = null,
    string? RiotProduct = null,
    string[]? UninstallNames = null,
    string[]? Executables = null,
    bool DeepProfile = false,
    string ConfigHint = "",
    string LatencyTech = "");

public static class GameCatalogue
{
    public static readonly IReadOnlyList<GameDefinition> Competitive =
    [
        new("cs2", "Counter-Strike 2", SteamAppId: 730, Executables: ["cs2.exe"], DeepProfile: true,
            ConfigHint: @"Steam\userdata\<id>\730\local\cfg", LatencyTech: "NVIDIA Reflex, AMD Anti-Lag 2"),
        new("valorant", "VALORANT", RiotProduct: "valorant", Executables: ["VALORANT-Win64-Shipping.exe"],
            ConfigHint: @"%LOCALAPPDATA%\VALORANT\Saved\Config", LatencyTech: "NVIDIA Reflex"),
        new("apex", "Apex Legends", SteamAppId: 1172470, UninstallNames: ["Apex Legends"], Executables: ["r5apex.exe", "r5apex_dx12.exe"],
            ConfigHint: @"%USERPROFILE%\Saved Games\Respawn\Apex\local", LatencyTech: "NVIDIA Reflex"),
        new("overwatch2", "Overwatch 2", SteamAppId: 2357570, UninstallNames: ["Overwatch"], Executables: ["Overwatch.exe"],
            ConfigHint: @"Documents\Overwatch\Settings", LatencyTech: "NVIDIA Reflex"),
        new("fortnite", "Fortnite", EpicAppName: "Fortnite", Executables: ["FortniteClient-Win64-Shipping.exe"],
            ConfigHint: @"%LOCALAPPDATA%\FortniteGame\Saved\Config\WindowsClient", LatencyTech: "NVIDIA Reflex"),
        new("r6siege", "Rainbow Six Siege", SteamAppId: 359550, UninstallNames: ["Tom Clancy's Rainbow Six Siege", "Rainbow Six Siege"], Executables: ["RainbowSix.exe"],
            ConfigHint: @"Documents\My Games\Rainbow Six - Siege", LatencyTech: "NVIDIA Reflex"),
        new("marvelrivals", "Marvel Rivals", SteamAppId: 2767030, Executables: ["Marvel-Win64-Shipping.exe"],
            ConfigHint: @"%LOCALAPPDATA%\Marvel\Saved\Config\Windows", LatencyTech: "NVIDIA Reflex"),
    ];

    public static GameDefinition? ById(string id) => Competitive.FirstOrDefault(g => g.Id == id);

    /// <summary>Steam app ids that are tools, not games (redistributables, runtimes).</summary>
    public static readonly HashSet<int> SteamTools = [228980, 1070560, 1391110, 1628350, 1493710, 2180100];
}
