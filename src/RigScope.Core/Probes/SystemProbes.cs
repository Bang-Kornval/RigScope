using System.Runtime.InteropServices;
using Microsoft.Win32;
using RigScope.Core.Parsing;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

public sealed class OsProbe : IProbe
{
    public string Name => "Windows version";

    public void Collect(SystemSnapshot s)
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        s.Os.DisplayVersion = Sys.RegStr(RegistryHive.LocalMachine, key, "DisplayVersion");
        s.Os.Build = int.TryParse(Sys.RegStr(RegistryHive.LocalMachine, key, "CurrentBuild"), out var b) ? b : Environment.OSVersion.Version.Build;
        s.Os.Ubr = Sys.RegInt(RegistryHive.LocalMachine, key, "UBR") ?? 0;
        var name = Sys.RegStr(RegistryHive.LocalMachine, key, "ProductName");
        // ProductName still says "Windows 10" on Windows 11 for compatibility.
        s.Os.ProductName = s.Os.IsWindows11 ? name.Replace("Windows 10", "Windows 11") : name;
    }
}

public sealed partial class CpuProbe : IProbe
{
    public string Name => "Processor";

    public void Collect(SystemSnapshot s)
    {
        var cpu = Sys.Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
        if (cpu is null) return;
        s.Cpu.Name = cpu.Str("Name");
        s.Cpu.Vendor = HardwareParsing.VendorFromName(cpu.Str("Manufacturer") + " " + s.Cpu.Name);
        s.Cpu.Cores = (int)cpu.Long("NumberOfCores");
        s.Cpu.Threads = (int)cpu.Long("NumberOfLogicalProcessors");
        s.Cpu.MaxClockMhz = (int)cpu.Long("MaxClockSpeed");
        s.Cpu.IsX3D = HardwareParsing.IsX3D(s.Cpu.Name);

        if (s.Cpu.Vendor == HardwareVendor.Intel)
        {
            var classes = CoreEfficiencyClasses();
            if (classes.Distinct().Count() > 1)
            {
                var top = classes.Max();
                s.Cpu.IsHybrid = true;
                s.Cpu.PerformanceCores = classes.Count(c => c == top);
                s.Cpu.EfficiencyCores = classes.Count(c => c != top);
            }
        }
    }

    /// <summary>One entry per physical core: its EfficiencyClass (higher = faster core).</summary>
    private static List<byte> CoreEfficiencyClasses()
    {
        const int RelationProcessorCore = 0;
        var result = new List<byte>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
        if (len == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len)) return result;
            int offset = 0;
            while (offset < len)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                // PROCESSOR_RELATIONSHIP starts at +8: Flags (1 byte), EfficiencyClass (1 byte).
                result.Add(Marshal.ReadByte(buffer, offset + 9));
                if (size <= 0) break;
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);
}

public sealed class MemoryProbe : IProbe
{
    public string Name => "Memory";

    public void Collect(SystemSnapshot s)
    {
        foreach (var m in Sys.Wmi("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            var part = m.Str("PartNumber");
            var spd = (int)m.Long("Speed");
            var module = new MemoryModule
            {
                CapacityBytes = m.Long("Capacity"),
                SpdMts = spd,
                ConfiguredMts = (int)m.Long("ConfiguredClockSpeed"),
                Manufacturer = m.Str("Manufacturer"),
                PartNumber = part,
            };
            var rated = HardwareParsing.RatedMtsFromPartNumber(part);
            module.RatedMts = rated > 0 ? rated : Math.Max(spd, module.ConfiguredMts);
            s.Memory.Modules.Add(module);
            s.Memory.TotalBytes += module.CapacityBytes;
            s.Memory.Generation = m.Long("SMBIOSMemoryType") switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => s.Memory.Generation };
        }
    }
}

public sealed partial class FirmwareProbe : IProbe
{
    public string Name => "BIOS and motherboard";

    public void Collect(SystemSnapshot s)
    {
        if (Sys.Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault() is { } bios)
        {
            s.Firmware.BiosVendor = bios.Str("Manufacturer");
            s.Firmware.BiosVersion = bios.Str("SMBIOSBIOSVersion");
            s.Firmware.BiosDate = HardwareParsing.ParseCimDate(bios.Str("ReleaseDate"));
        }
        if (Sys.Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault() is { } board)
        {
            s.Firmware.BoardManufacturer = board.Str("Manufacturer");
            s.Firmware.BoardProduct = board.Str("Product");
        }
        if (GetFirmwareType(out var type)) s.Firmware.IsUefi = type == 2;

        var sb = Sys.RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        if (sb is not null) s.Firmware.SecureBootEnabled = sb == 1;

        // Chassis types: 8–14, 30–32 are portable; a battery is the fallback signal.
        var portable = new HashSet<long> { 8, 9, 10, 11, 12, 14, 30, 31, 32 };
        var chassis = Sys.Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
        var isPortable = chassis?["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t));
        s.Firmware.IsLaptop = isPortable || Sys.Wmi("SELECT Name FROM Win32_Battery").Count > 0;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);
}

public sealed class SecurityProbe : IProbe
{
    public string Name => "Security features";

    public void Collect(SystemSnapshot s)
    {
        if (Sys.Wmi("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault() is { } dg)
        {
            s.Security.VbsRunning = dg.Long("VirtualizationBasedSecurityStatus") == 2;
            s.Security.MemoryIntegrityRunning = dg["SecurityServicesRunning"] is uint[] running && running.Contains(2u);
        }
        if (Sys.Wmi("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault() is { } cs)
            s.Security.HypervisorPresent = cs["HypervisorPresent"] is true;
    }
}
