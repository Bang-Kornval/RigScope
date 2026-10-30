using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

public sealed class NetworkProbe : IProbe
{
    public string Name => "Network";
    private const string Scope = @"root\StandardCimv2";

    public void Collect(SystemSnapshot s)
    {
        var defaultRoutes = Sys.Wmi("SELECT InterfaceIndex, RouteMetric FROM MSFT_NetRoute WHERE DestinationPrefix = '0.0.0.0/0'", Scope)
            .OrderBy(r => r.Long("RouteMetric")).Select(r => r.Long("InterfaceIndex")).ToList();
        var advanced = Sys.Wmi("SELECT Name, RegistryKeyword, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData", Scope);

        foreach (var a in Sys.Wmi("SELECT Name, InterfaceDescription, InterfaceIndex, ReceiveLinkSpeed, MediaConnectState, NdisPhysicalMedium, Virtual, HardwareInterface, DriverVersionString, PnPDeviceID FROM MSFT_NetAdapter", Scope))
        {
            var name = a.Str("Name");
            var desc = a.Str("InterfaceDescription");
            var isVirtual = a["Virtual"] is true || a["HardwareInterface"] is false;
            var medium = a.Long("NdisPhysicalMedium");
            var info = new NetworkAdapterInfo
            {
                Name = name,
                Description = desc,
                Kind = isVirtual ? AdapterKind.Virtual : medium switch { 9 => AdapterKind.Wifi, 14 => AdapterKind.Ethernet, _ => GuessKind(desc) },
                IsUp = a.Long("MediaConnectState") == 1,
                LinkSpeedBps = a.Long("ReceiveLinkSpeed"),
                DriverVersion = a.Str("DriverVersionString"),
                IsDefaultRoute = defaultRoutes.Count > 0 && defaultRoutes[0] == a.Long("InterfaceIndex"),
            };
            var pnp = a.Str("PnPDeviceID");
            if (pnp.Length > 0)
            {
                var entity = Sys.Wmi($"SELECT HardwareID FROM Win32_PnPEntity WHERE DeviceID = '{pnp.Replace(@"\", @"\\").Replace("'", "\\'")}'").FirstOrDefault();
                if (entity?["HardwareID"] is string[] { Length: > 0 } ids) info.HardwareId = ids[0];
            }

            foreach (var p in advanced.Where(p => p.Str("Name") == name))
            {
                var keyword = p.Str("RegistryKeyword");
                var value = p["RegistryValue"] is string[] { Length: > 0 } v ? v[0] : "";
                if (keyword.Equals("*EEE", StringComparison.OrdinalIgnoreCase) || keyword.Equals("EEE", StringComparison.OrdinalIgnoreCase)
                    || keyword.Equals("EnableGreenEthernet", StringComparison.OrdinalIgnoreCase) || keyword.Equals("*EEELinkAdvertisement", StringComparison.OrdinalIgnoreCase))
                    info.EnergyEfficientEthernet = value != "0";
                else if (keyword.Equals("*InterruptModeration", StringComparison.OrdinalIgnoreCase))
                    info.InterruptModeration = value != "0";
            }
            s.Network.Add(info);
        }
    }

    private static AdapterKind GuessKind(string description)
    {
        var d = description.ToLowerInvariant();
        if (d.Contains("wi-fi") || d.Contains("wireless") || d.Contains("802.11") || d.Contains("wlan")) return AdapterKind.Wifi;
        if (d.Contains("ethernet") || d.Contains("gbe") || d.Contains("family controller")) return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }
}

public sealed class DriveProbe : IProbe
{
    public string Name => "Drives";
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public void Collect(SystemSnapshot s)
    {
        var diskByLetter = Sys.Wmi("SELECT DriveLetter, DiskNumber FROM MSFT_Partition", Scope)
            .Where(p => p["DriveLetter"] is char c && c != '\0')
            .GroupBy(p => char.ToUpperInvariant((char)p["DriveLetter"]))
            .ToDictionary(g => g.Key, g => g.First().Long("DiskNumber"));
        var mediaByDisk = Sys.Wmi("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk", Scope)
            .ToDictionary(d => d.Str("DeviceId"), d => d.Long("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SSD", _ => "Unspecified" });
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.ToUpperInvariant() ?? @"C:\";

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\').ToUpperInvariant();
                var media = diskByLetter.TryGetValue(letter[0], out var disk) && mediaByDisk.TryGetValue(disk.ToString(), out var m) ? m : "Unspecified";
                s.Drives.Add(new DriveInfoSnapshot
                {
                    Letter = letter,
                    Label = d.VolumeLabel,
                    FileSystem = d.DriveFormat,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.TotalFreeSpace,
                    MediaType = media,
                    IsSystem = d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
