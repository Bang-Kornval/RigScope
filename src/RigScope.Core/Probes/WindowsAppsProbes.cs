using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

public sealed partial class WindowsGamingProbe : IProbe
{
    public string Name => "Windows game settings";

    public void Collect(SystemSnapshot s)
    {
        var w = s.Windows;
        var gameMode = Sys.RegInt(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        w.GameModeEnabled = gameMode is null ? null : gameMode != 0;

        const string dvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        var appCapture = Sys.RegInt(RegistryHive.CurrentUser, dvr, "AppCaptureEnabled");
        var historical = Sys.RegInt(RegistryHive.CurrentUser, dvr, "HistoricalCaptureEnabled");
        w.BackgroundRecording = appCapture == 0 ? false : historical == 1;

        var hags = Sys.RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        w.HagsEnabled = hags switch { 2 => true, 1 => false, _ => null };

        var dx = Sys.RegStr(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
        if (dx.Contains("SwapEffectUpgradeEnable=1")) w.WindowedOptimizations = true;
        else if (dx.Contains("SwapEffectUpgradeEnable=0")) w.WindowedOptimizations = false;

        w.MouseAcceleration = Sys.RegStr(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") is { Length: > 0 } ms && ms != "0";

        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) == 0)
        {
            var guid = Marshal.PtrToStructure<Guid>(schemePtr);
            LocalFree(schemePtr);
            w.PowerPlanGuid = guid.ToString();
            w.PowerPlanName = PlanName(guid);
        }

        if (GetSystemPowerStatus(out var ps) && ps.ACLineStatus != 255)
            w.OnBattery = ps.ACLineStatus == 0;

        try
        {
            var hiber = new FileInfo(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys"));
            w.HiberfilBytes = hiber.Exists ? hiber.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string PlanName(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return "";
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0
                ? Marshal.PtrToStringUni(buf) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr powerSetting, IntPtr buffer, ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>A background app RigScope knows about. Matching is by process name and by startup entry name/command.</summary>
public sealed record KnownApp(string Id, string Name, string Category, string[] Processes, string[] StartupMatches);

public sealed class KnownAppsProbe : IProbe
{
    public string Name => "Background apps";

    /// <summary>Categories: rgb (peripheral/lighting suites), recorder, vm, overlay, sync, wallpaper, launcher.</summary>
    public static readonly IReadOnlyList<KnownApp> Catalogue =
    [
        new("icue", "Corsair iCUE", "rgb", ["iCUE", "Corsair.Service", "CorsairCpuIdService"], ["iCUE", "Corsair"]),
        new("ghub", "Logitech G HUB", "rgb", ["lghub", "lghub_agent", "lghub_system_tray", "lghub_updater"], ["LGHUB", "lghub"]),
        new("steelseries", "SteelSeries GG", "rgb", ["SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesGGEZ", "SteelSeriesSonar", "SteelSeriesPrism"], ["SteelSeriesGG", "SteelSeries"]),
        new("razer", "Razer Synapse", "rgb", ["RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service", "RazerCentralService"], ["Razer"]),
        new("armoury", "ASUS Armoury Crate", "rgb", ["ArmouryCrate", "ArmouryCrate.Service", "LightingService"], ["ArmouryCrate", "ArmourySocketServer"]),
        new("msicenter", "MSI Center / Mystic Light", "rgb", ["MSI.CentralServer", "MSI Center", "MysticLight", "LEDKeeper2"], ["MSI Center", "MSI.CentralServer"]),
        new("nzxt", "NZXT CAM", "rgb", ["NZXT CAM"], ["NZXT CAM", "NZXT.CAM"]),
        new("signalrgb", "SignalRGB", "rgb", ["SignalRgb", "SignalRgbLauncher"], ["SignalRGB"]),
        new("hyperx", "HyperX NGENUITY", "rgb", ["NGenuity2Helper", "HyperX NGENUITY"], ["NGENUITY"]),
        new("wooting", "Wootility", "rgb", ["wootility"], ["wootility"]),
        new("docker", "Docker Desktop (WSL2 VM)", "vm", ["Docker Desktop", "com.docker.backend", "vmmem", "vmmemWSL"], ["Docker Desktop"]),
        new("vmware", "VMware Workstation", "vm", ["vmware", "vmware-vmx"], ["vmware"]),
        new("virtualbox", "VirtualBox", "vm", ["VirtualBox", "VBoxHeadless", "VBoxSVC"], ["VirtualBox"]),
        new("medal", "Medal.tv", "recorder", ["Medal", "MedalEncoder"], ["Medal"]),
        new("overwolf", "Overwolf / Outplayed", "overlay", ["Overwolf", "OverwolfBrowser", "OverwolfHelper"], ["Overwolf"]),
        new("obs", "OBS Studio", "recorder", ["obs64"], ["obs64", "OBS Studio"]),
        new("rtss", "RivaTuner Statistics Server", "overlay", ["RTSS", "RTSSHooksLoader64"], ["RTSS", "RivaTuner"]),
        new("afterburner", "MSI Afterburner", "overlay", ["MSIAfterburner"], ["MSIAfterburner", "MSI Afterburner"]),
        new("wallpaperengine", "Wallpaper Engine", "wallpaper", ["wallpaper32", "wallpaper64", "webwallpaper32"], ["Wallpaper Engine", "wallpaper32"]),
        new("onedrive", "OneDrive", "sync", ["OneDrive"], ["OneDrive"]),
        new("googledrive", "Google Drive", "sync", ["GoogleDriveFS"], ["GoogleDriveFS", "Google Drive"]),
        new("dropbox", "Dropbox", "sync", ["Dropbox"], ["Dropbox"]),
        new("adobecc", "Adobe Creative Cloud", "sync", ["Creative Cloud", "Adobe Desktop Service", "CCXProcess", "AdobeCollabSync"], ["Adobe Creative Cloud", "CCXProcess", "Adobe Acrobat Synchronizer"]),
        new("epic", "Epic Games Launcher", "launcher", ["EpicGamesLauncher"], ["EpicGamesLauncher"]),
        new("eaapp", "EA app", "launcher", ["EADesktop", "EABackgroundService"], ["EADesktop", "EA Desktop"]),
        new("battlenet", "Battle.net", "launcher", ["Battle.net"], ["Battle.net"]),
        new("ubisoft", "Ubisoft Connect", "launcher", ["upc", "UbisoftConnect"], ["Ubisoft"]),
    ];

    public void Collect(SystemSnapshot s)
    {
        var startup = StartupEntries();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var app in Catalogue)
            {
                var matches = processes.Where(p => app.Processes.Any(n => p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                bool autostart = startup.Any(e => app.StartupMatches.Any(m =>
                    e.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || e.Command.Contains(m, StringComparison.OrdinalIgnoreCase)));
                if (matches.Count == 0 && !autostart) continue;

                var info = new KnownAppInfo
                {
                    Id = app.Id, Name = app.Name, Category = app.Category,
                    Running = matches.Count > 0, StartsWithWindows = autostart, ProcessCount = matches.Count,
                };
                foreach (var p in matches)
                {
                    try { info.CpuSeconds += p.TotalProcessorTime.TotalSeconds; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    try { info.MemoryBytes += p.WorkingSet64; } catch (InvalidOperationException) { }
                }
                info.CpuSeconds = Math.Round(info.CpuSeconds, 1);
                s.Apps.Add(info);
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    internal sealed record StartupEntry(string Name, string Command);

    /// <summary>Enabled startup entries from the Run keys and Startup folders. Entries switched off in Task Manager are skipped.</summary>
    internal static List<StartupEntry> StartupEntries()
    {
        var list = new List<StartupEntry>();
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        var sources = new (RegistryHive Hive, RegistryView View, string Approved)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, approved),
            (RegistryHive.LocalMachine, RegistryView.Registry64, approved),
            (RegistryHive.LocalMachine, RegistryView.Registry32, approved.Replace(@"\Run", @"\Run32")),
        };
        foreach (var (hive, view, approvedKey) in sources)
        {
            foreach (var name in Sys.RegValueNames(hive, run, view))
            {
                if (IsDisabled(hive, approvedKey, name)) continue;
                list.Add(new(name, Sys.RegStr(hive, run, name, view)));
            }
        }
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (!IsDisabled(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", Path.GetFileName(f)))
                        list.Add(new(name, f));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Task Manager marks a disabled startup item with an odd first byte (0x03, 0x07…) in StartupApproved.</summary>
    private static bool IsDisabled(RegistryHive hive, string key, string name) =>
        Sys.Reg(hive, key, name) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
}
