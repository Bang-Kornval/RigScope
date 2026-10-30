using System.Text.Json.Serialization;

namespace RigScope.Core.Snapshot;

/// <summary>
/// Everything RigScope knows about one PC at one moment. Probes fill it in (read-only),
/// rules read it. It is plain data so it can be saved, shared for bug reports and used as a test fixture.
/// Property names are camelCase in JSON; rule paths in the advice pack use those names.
/// </summary>
public sealed class SystemSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public string AppVersion { get; set; } = "";
    public bool IsElevated { get; set; }

    public OsInfo Os { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public List<DisplayInfo> Displays { get; set; } = [];
    public MemoryInfo Memory { get; set; } = new();
    public FirmwareInfo Firmware { get; set; } = new();
    public SecurityInfo Security { get; set; } = new();
    public WindowsGamingInfo Windows { get; set; } = new();
    public List<KnownAppInfo> Apps { get; set; } = [];
    public List<NetworkAdapterInfo> Network { get; set; } = [];
    public List<DriveInfoSnapshot> Drives { get; set; } = [];
    public List<InstalledGame> Games { get; set; } = [];
    public Cs2Info? Cs2 { get; set; }

    /// <summary>Probe name → what went wrong. A failed probe never stops the others.</summary>
    public Dictionary<string, string> ProbeErrors { get; set; } = [];

    /// <summary>Convenience views used by rules ("gpu" = the main discrete GPU, "display" = the primary display).</summary>
    public GpuInfo? Gpu => Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? Gpus.FirstOrDefault();
    public DisplayInfo? Display => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();
}

[JsonConverter(typeof(JsonStringEnumConverter<HardwareVendor>))]
public enum HardwareVendor { Unknown, Intel, Amd, Nvidia, Other }

public sealed class OsInfo
{
    public string ProductName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public int Build { get; set; }
    public int Ubr { get; set; }
    public bool IsWindows11 => Build >= 22000;
}

public sealed class CpuInfo
{
    public HardwareVendor Vendor { get; set; }
    public string Name { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMhz { get; set; }
    /// <summary>Intel 12th gen and later: performance + efficiency cores.</summary>
    public bool IsHybrid { get; set; }
    public int PerformanceCores { get; set; }
    public int EfficiencyCores { get; set; }
    /// <summary>AMD 3D V-Cache part (e.g. 7800X3D, 9950X3D).</summary>
    public bool IsX3D { get; set; }
    public bool SmtEnabled => Threads > Cores;
}

public sealed class GpuInfo
{
    public HardwareVendor Vendor { get; set; }
    public string Name { get; set; } = "";
    public bool IsIntegrated { get; set; }
    /// <summary>Windows driver version, e.g. 32.0.16.2101.</summary>
    public string DriverVersion { get; set; } = "";
    /// <summary>Vendor-facing version, e.g. NVIDIA 621.01. Empty when unknown.</summary>
    public string VendorDriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public long VramBytes { get; set; }
    /// <summary>null = could not tell (needs a vendor tool).</summary>
    public bool? ResizableBarEnabled { get; set; }
    public int? PcieGen { get; set; }
    public int? PcieWidth { get; set; }
    public int? DriverAgeDays => DriverDate is { } d ? (int)(DateTime.Today - d.Date).TotalDays : null;
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public string MonitorName { get; set; } = "";
    public bool IsPrimary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Current refresh rate, rounded (164.999 → 165).</summary>
    public int RefreshHz { get; set; }
    /// <summary>Highest refresh rate the display offers at its current resolution.</summary>
    public int MaxRefreshHz { get; set; }
    public bool RunningBelowMaxRefresh => MaxRefreshHz > 0 && RefreshHz + 1 < MaxRefreshHz;
}

public sealed class MemoryInfo
{
    public long TotalBytes { get; set; }
    public List<MemoryModule> Modules { get; set; } = [];
    public int TotalGb => (int)Math.Round(TotalBytes / 1024d / 1024 / 1024);
    /// <summary>Speed the RAM actually runs at (MT/s).</summary>
    public int ConfiguredMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredMts);
    /// <summary>Speed in the part number (e.g. CMK32GX4M2D3000C16 → 3000), falling back to the SPD speed.</summary>
    public int RatedMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.RatedMts);
    /// <summary>JEDEC speed reported by SPD. If the configured speed equals this and the rated speed is higher, XMP/EXPO is off.</summary>
    public int SpdMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.SpdMts);
    public bool LikelyRunningBelowRated => RatedMts > 0 && ConfiguredMts > 0 && ConfiguredMts + 100 < RatedMts;
    public bool LikelySingleChannel => Modules.Count == 1;
    /// <summary>DDR4 or DDR5, from SMBIOS memory type.</summary>
    public string Generation { get; set; } = "";
}

public sealed class MemoryModule
{
    public long CapacityBytes { get; set; }
    public int SpdMts { get; set; }
    public int ConfiguredMts { get; set; }
    public int RatedMts { get; set; }
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
}

public sealed class FirmwareInfo
{
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public string BoardManufacturer { get; set; } = "";
    public string BoardProduct { get; set; } = "";
    public bool? IsUefi { get; set; }
    public bool? SecureBootEnabled { get; set; }
    public bool IsLaptop { get; set; }
    public int? BiosAgeDays => BiosDate is { } d ? (int)(DateTime.Today - d.Date).TotalDays : null;
}

public sealed class SecurityInfo
{
    public bool? VbsRunning { get; set; }
    /// <summary>Memory Integrity (HVCI).</summary>
    public bool? MemoryIntegrityRunning { get; set; }
    public bool HypervisorPresent { get; set; }
}

public sealed class WindowsGamingInfo
{
    /// <summary>null = Windows default (on).</summary>
    public bool? GameModeEnabled { get; set; }
    /// <summary>Game Bar "record what happened" background capture.</summary>
    public bool? BackgroundRecording { get; set; }
    /// <summary>Hardware-accelerated GPU scheduling. null = driver/OS default.</summary>
    public bool? HagsEnabled { get; set; }
    /// <summary>"Optimizations for windowed games".</summary>
    public bool? WindowedOptimizations { get; set; }
    public bool MouseAcceleration { get; set; }
    public string PowerPlanName { get; set; } = "";
    public string PowerPlanGuid { get; set; } = "";
    public bool PowerPlanIsBalanced => PowerPlanGuid.Equals("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase);
    public bool PowerPlanIsPowerSaver => PowerPlanGuid.Equals("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase);
    public bool? OnBattery { get; set; }
    public long HiberfilBytes { get; set; }
}

/// <summary>A background app from RigScope's catalogue (RGB suites, recorders, VMs, overlays).</summary>
public sealed class KnownAppInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Running { get; set; }
    public bool StartsWithWindows { get; set; }
    public int ProcessCount { get; set; }
    public double CpuSeconds { get; set; }
    public long MemoryBytes { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AdapterKind>))]
public enum AdapterKind { Other, Ethernet, Wifi, Virtual }

public sealed class NetworkAdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public AdapterKind Kind { get; set; }
    public bool IsUp { get; set; }
    public long LinkSpeedBps { get; set; }
    public bool IsDefaultRoute { get; set; }
    public string DriverVersion { get; set; } = "";
    public string HardwareId { get; set; } = "";
    /// <summary>Energy Efficient Ethernet; null when the adapter has no such setting.</summary>
    public bool? EnergyEfficientEthernet { get; set; }
    public bool? InterruptModeration { get; set; }
}

public sealed class DriveInfoSnapshot
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>SSD, HDD or Unspecified.</summary>
    public string MediaType { get; set; } = "";
    public bool IsSystem { get; set; }
    public double FreePercent => TotalBytes == 0 ? 0 : FreeBytes * 100d / TotalBytes;
}

public sealed class InstalledGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Launcher { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? LastPlayed { get; set; }
    /// <summary>True for games in RigScope's competitive FPS catalogue.</summary>
    public bool IsCompetitive { get; set; }
    public string DriveLetter => InstallPath.Length >= 2 && InstallPath[1] == ':' ? InstallPath[..2].ToUpperInvariant() : "";
    public int? DaysSincePlayed => LastPlayed is { } d ? (int)(DateTime.Now - d).TotalDays : null;
}

public sealed class Cs2Info
{
    public string InstallPath { get; set; } = "";
    /// <summary>The Steam account whose CS2 config changed most recently.</summary>
    public Cs2Account? Primary { get; set; }
    public List<Cs2Account> Accounts { get; set; } = [];
}

public sealed class Cs2Account
{
    public string SteamId3 { get; set; } = "";
    public DateTime? ConfigModified { get; set; }
    /// <summary>cs2_video.txt values with the "setting." prefix removed and dots turned into underscores.</summary>
    public Dictionary<string, string> Video { get; set; } = [];
    /// <summary>User convars from cs2_user_convars_0_slot0.vcfg (only non-default values are stored there).</summary>
    public Dictionary<string, string> Convars { get; set; } = [];
    public string LaunchOptions { get; set; } = "";
    public bool HasAutoexec { get; set; }
    /// <summary>Convar commands found in autoexec.cfg (binds excluded).</summary>
    public Dictionary<string, string> Autoexec { get; set; } = [];

    /// <summary>Refresh rate CS2 is set to use, from refreshrate_numerator/denominator (164999/1000 → 165).</summary>
    public int? ConfiguredRefreshHz =>
        double.TryParse(Video.GetValueOrDefault("refreshrate_numerator"), System.Globalization.CultureInfo.InvariantCulture, out var n)
        && double.TryParse(Video.GetValueOrDefault("refreshrate_denominator"), System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? (int)Math.Round(n / d) : null;

    /// <summary>The fps_max CS2 ends up with: launch options run last, then autoexec, then saved convars. null = game default.</summary>
    public string? FpsMax => Effective("fps_max");
    public string FpsMaxSource => Source("fps_max");
    public string? NetBufferTicks => Effective("cl_net_buffer_ticks");
    public string? MaxPing => Effective("mm_dedicated_search_maxping");
    public string? PlayerContrast => Video.GetValueOrDefault("r_player_visibility_mode") ?? Effective("r_player_visibility_mode");

    private string? Effective(string convar) =>
        LaunchConvar(convar) ?? Autoexec.GetValueOrDefault(convar) ?? Convars.GetValueOrDefault(convar);

    private string Source(string convar) =>
        LaunchConvar(convar) is not null ? "launch options"
        : Autoexec.ContainsKey(convar) ? "autoexec.cfg"
        : Convars.ContainsKey(convar) ? "game settings" : "game default";

    /// <summary>"+fps_max 0 -nojoy" → fps_max = 0.</summary>
    private string? LaunchConvar(string convar)
    {
        var m = System.Text.RegularExpressions.Regex.Match(LaunchOptions, $@"\+{convar}\s+""?([^\s""]+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
