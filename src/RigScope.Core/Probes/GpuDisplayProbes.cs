using System.Runtime.InteropServices;
using Microsoft.Win32;
using RigScope.Core.Parsing;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

public sealed class GpuProbe : IProbe
{
    public string Name => "Graphics card";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public void Collect(SystemSnapshot s)
    {
        var vram = VramByDriverDesc();
        foreach (var vc in Sys.Wmi("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = vc.Str("Name");
            var pnp = vc.Str("PNPDeviceID");
            if (IsVirtualAdapter(name, pnp)) continue;

            var vendor = HardwareParsing.VendorFromPciId(pnp);
            if (vendor == HardwareVendor.Unknown) vendor = HardwareParsing.VendorFromName(name);
            var gpu = new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                DriverVersion = vc.Str("DriverVersion"),
                DriverDate = HardwareParsing.ParseCimDate(vc.Str("DriverDate")),
                VramBytes = vram.TryGetValue(name, out var v) ? v : 0,
            };
            gpu.IsIntegrated = IsIntegrated(gpu);
            if (vendor == HardwareVendor.Nvidia) gpu.VendorDriverVersion = HardwareParsing.NvidiaVersionFromWindows(gpu.DriverVersion);
            s.Gpus.Add(gpu);
        }

        if (s.Gpus.Any(g => g.Vendor == HardwareVendor.Nvidia)) AddNvidiaDetails(s);
    }

    private static void AddNvidiaDetails(SystemSnapshot s)
    {
        var smi = Sys.FindOnPath("nvidia-smi.exe", @"%SystemRoot%\System32", @"%ProgramFiles%\NVIDIA Corporation\NVSMI");
        if (smi is null) return;
        var csv = Sys.Run(smi, "--query-gpu=name,driver_version,pcie.link.gen.max,pcie.link.width.current --format=csv,noheader");
        var rows = csv is null ? [] : HardwareParsing.ParseNvidiaSmiCsv("name,driver_version,gen,width\n" + csv);
        var memory = Sys.Run(smi, "-q -d MEMORY");
        bool? rebar = memory is null ? null : HardwareParsing.ResizableBarFromNvidiaSmiMemory(memory);

        var nvidia = s.Gpus.Where(g => g.Vendor == HardwareVendor.Nvidia).ToList();
        for (int i = 0; i < nvidia.Count; i++)
        {
            var gpu = nvidia[i];
            var row = rows.FirstOrDefault(r => r.GetValueOrDefault("name") == gpu.Name) ?? (i < rows.Count ? rows[i] : null);
            if (row is not null)
            {
                if (row.GetValueOrDefault("driver_version") is { Length: > 0 } dv) gpu.VendorDriverVersion = dv;
                if (int.TryParse(row.GetValueOrDefault("gen"), out var gen)) gpu.PcieGen = gen;
                if (int.TryParse(row.GetValueOrDefault("width"), out var width)) gpu.PcieWidth = width;
            }
            // -q -d MEMORY lists GPUs in order; with one NVIDIA GPU (the usual case) the first BAR1 block is it.
            if (nvidia.Count == 1) gpu.ResizableBarEnabled = rebar;
        }
    }

    private static Dictionary<string, long> VramByDriverDesc()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in Sys.RegSubKeys(RegistryHive.LocalMachine, DisplayClass))
        {
            if (!sub.All(char.IsDigit)) continue;
            var key = $@"{DisplayClass}\{sub}";
            var desc = Sys.RegStr(RegistryHive.LocalMachine, key, "DriverDesc");
            var mem = Sys.Reg(RegistryHive.LocalMachine, key, "HardwareInformation.qwMemorySize");
            long bytes = mem switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b), _ => 0 };
            if (desc.Length > 0 && bytes > 0) map[desc] = bytes;
        }
        return map;
    }

    internal static bool IsVirtualAdapter(string name, string pnp) =>
        name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
        || pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
        || pnp.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsIntegrated(GpuInfo g)
    {
        var n = g.Name;
        return g.Vendor switch
        {
            HardwareVendor.Intel => !n.Contains("Arc", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc(TM) Graphics", StringComparison.OrdinalIgnoreCase),
            HardwareVendor.Amd => !System.Text.RegularExpressions.Regex.IsMatch(n, @"\b(RX|Pro|FirePro|Vega \d{2})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            _ => false,
        };
    }
}

public sealed partial class DisplayProbe : IProbe
{
    public string Name => "Displays";

    public void Collect(SystemSnapshot s)
    {
        var names = MonitorNamesByPnpCode();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;

            var mode = NewDevMode();
            if (!EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref mode)) continue;

            var info = new DisplayInfo
            {
                DeviceName = adapter.DeviceName,
                IsPrimary = (adapter.StateFlags & PrimaryDevice) != 0,
                Width = (int)mode.dmPelsWidth,
                Height = (int)mode.dmPelsHeight,
                RefreshHz = (int)mode.dmDisplayFrequency,
            };
            info.MaxRefreshHz = MaxRefreshAt(adapter.DeviceName, info.Width, info.Height);
            // The current mode reports a rounded-down integer (164 for 164.999 Hz); trust the mode list's max when they're 1 apart.
            if (info.MaxRefreshHz == info.RefreshHz + 1) info.RefreshHz = info.MaxRefreshHz;

            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0))
            {
                var code = PnpCode(monitor.DeviceID);
                info.MonitorName = code is not null && names.TryGetValue(code, out var n) ? n : monitor.DeviceString;
            }
            s.Displays.Add(info);
        }
    }

    private static int MaxRefreshAt(string device, int width, int height)
    {
        int max = 0;
        for (int m = 0; ; m++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettingsW(device, m, ref mode)) break;
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height) max = Math.Max(max, (int)mode.dmDisplayFrequency);
        }
        return max;
    }

    /// <summary>MONITOR\AUS2701\{...} → AUS2701</summary>
    internal static string? PnpCode(string deviceId)
    {
        var parts = deviceId.Split('\\');
        return parts.Length >= 2 ? parts[1].ToUpperInvariant() : null;
    }

    private static Dictionary<string, string> MonitorNamesByPnpCode()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Sys.Wmi("SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", @"root\wmi"))
        {
            var code = PnpCode(m.Str("InstanceName"));
            if (code is null || m["UserFriendlyName"] is not ushort[] chars) continue;
            var name = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
            if (name.Length > 0) map[code] = name;
        }
        return map;
    }

    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    // DllImport (not LibraryImport) because these structs use ByValTStr marshalling.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int modeNum, ref DEVMODE devMode);
#pragma warning restore SYSLIB1054
}
