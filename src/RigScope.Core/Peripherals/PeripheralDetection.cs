using System.Text.RegularExpressions;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Peripherals;

/// <summary>One entry from Windows' device list (Win32_PnPEntity). Only the id, name and class are used.</summary>
public sealed record PnpDevice(string DeviceId, string Name, string PnpClass = "", string Manufacturer = "", bool Ok = true);

/// <summary>All device-list entries with the same vendor and product id, boiled down to one peripheral.</summary>
public sealed record PeripheralCandidate(
    string VendorId,
    string ProductId,
    string Name,
    PeripheralKind Kind,
    bool ViaReceiver,
    bool Bluetooth,
    bool DynamicLighting,
    string Manufacturer,
    IReadOnlyList<string> Names);

/// <summary>
/// Turns Windows' list of plugged-in devices into peripherals. Pure functions over names and ids: no device is opened,
/// no HID report is sent or read. One physical device shows up as several entries (a USB node, one per interface,
/// one per HID collection), mostly with generic names, so entries are grouped by vendor and product id and the most
/// specific name wins.
/// </summary>
public static partial class PeripheralDetection
{
    /// <summary>Windows' own names for interfaces and collections. They say nothing about the product.</summary>
    [GeneratedRegex(@"^(HID[ -]compliant\b.*|HID Keyboard Device|HID Consumer Control|USB Composite Device|USB Input Device|USB Audio Device|USB Mass Storage Device|USB Device|USB Serial Device|Generic USB Hub|USB Hub|USB Root Hub.*|Generic\b.*|Unknown USB Device.*|WinUsb Device|Bluetooth.*(Device|Service|Enumerator)|Microsoft Bluetooth LE Enumerator|Device Information Service|Generic Attribute Profile|Generic Access Profile|LE Generic Attribute Profile.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();

    /// <summary>Software devices that suites create (e.g. "Logitech G HUB Virtual Keyboard"). Not hardware.</summary>
    [GeneratedRegex(@"\bvirtual\b", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualName();

    [GeneratedRegex(@"\b(receiver|dongle)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransportName();

    /// <summary>USB\VID_046D&amp;PID_C543…, HID\VID_1038&amp;PID_1614&amp;MI_00…</summary>
    [GeneratedRegex(@"^(?<bus>[A-Z0-9_]+)\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbStyleId();

    /// <summary>Bluetooth HID with a USB-assigned vendor id: …_VID&amp;0002046D_PID&amp;B023 (classic) or …_VID&amp;02046D_PID&amp;B023 (LE).</summary>
    [GeneratedRegex(@"^(?<bus>[A-Z0-9_]+)\\\{[0-9A-F-]+\}_(?:DEV_)?VID&(?:0002|02)(?<vid>[0-9A-F]{4})_PID&(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothId();

    /// <summary>Buses that real hardware hangs off. Anything else with a VID (LGHUBDEVICE, CORSAIRBUS…) is a suite's software device.</summary>
    private static readonly HashSet<string> HardwareBuses = new(StringComparer.OrdinalIgnoreCase) { "USB", "HID", "BTHENUM", "BTHLEDEVICE", "BTHLE" };

    public static bool IsGenericName(string name) => string.IsNullOrWhiteSpace(name) || GenericName().IsMatch(name.Trim());

    public static bool IsVirtualName(string name) => VirtualName().IsMatch(name);

    /// <summary>Vendor id, product id, bus and whether it's Bluetooth, from a device instance id. null when it has no vendor/product id.</summary>
    public static (string Vid, string Pid, string Bus, bool Bluetooth)? ParseId(string deviceId)
    {
        if (BluetoothId().Match(deviceId) is { Success: true } bt)
            return (bt.Groups["vid"].Value.ToUpperInvariant(), bt.Groups["pid"].Value.ToUpperInvariant(), bt.Groups["bus"].Value.ToUpperInvariant(), true);
        if (UsbStyleId().Match(deviceId) is { Success: true } m)
            return (m.Groups["vid"].Value.ToUpperInvariant(), m.Groups["pid"].Value.ToUpperInvariant(), m.Groups["bus"].Value.ToUpperInvariant(), false);
        return null;
    }

    /// <summary>
    /// The most specific product name in a group, or "" if every name is generic. Receiver/dongle names lose to the
    /// device behind them ("PRO 2 LIGHTSPEED" beats "LIGHTSPEED Receiver"); then the most common name, then the longest.
    /// </summary>
    public static string PickName(IEnumerable<string> names) =>
        names.Select(n => n.Trim())
             .Where(n => !IsGenericName(n) && !IsVirtualName(n))
             .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
             .OrderBy(g => TransportName().IsMatch(g.Key) ? 1 : 0)
             .ThenByDescending(g => g.Count())
             .ThenByDescending(g => g.Key.Length)
             .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
             .Select(g => g.Key)
             .FirstOrDefault() ?? "";

    /// <summary>
    /// Groups device-list entries into peripherals. A group is dropped when it is a suite's virtual device, when it has no
    /// USB or Bluetooth node (a HID device on its own is software), or when nothing about it is specific.
    /// </summary>
    public static List<PeripheralCandidate> Group(IEnumerable<PnpDevice> devices)
    {
        var parsed = new List<(PnpDevice Device, string Vid, string Pid, string Bus, bool Bluetooth)>();
        foreach (var d in devices)
            if (ParseId(d.DeviceId) is { } id) parsed.Add((d, id.Vid, id.Pid, id.Bus, id.Bluetooth));

        var result = new List<PeripheralCandidate>();
        foreach (var g in parsed.GroupBy(x => (x.Vid, x.Pid)))
        {
            var entries = g.ToList();
            if (entries.Any(e => !HardwareBuses.Contains(e.Bus) || IsVirtualName(e.Device.Name))) continue;
            bool bluetooth = entries.Any(e => e.Bluetooth);
            if (!entries.Any(e => e.Bluetooth || e.Bus != "HID")) continue;

            var names = entries.Select(e => e.Device.Name.Trim()).Where(n => n.Length > 0).ToList();
            var classes = entries.Select(e => e.Device.PnpClass).Where(c => c.Length > 0).ToList();
            var name = PickName(names);
            var kind = GuessKind(name, names, classes);
            bool viaReceiver = !TransportName().IsMatch(name) && names.Any(n => TransportName().IsMatch(n));
            bool lampArray = entries.Any(e => e.Device.DeviceId.Contains("&LAMPARRAY", StringComparison.OrdinalIgnoreCase));
            var manufacturer = entries.Select(e => e.Device.Manufacturer.Trim())
                                      .FirstOrDefault(m => m.Length > 0 && !m.StartsWith('(') && !m.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                                                           && !m.Contains("WinUsb", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (name.Length == 0 && kind == PeripheralKind.Other) continue;

            result.Add(new PeripheralCandidate(g.Key.Vid, g.Key.Pid, name, kind, viaReceiver, bluetooth, lampArray, manufacturer,
                names.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return result.OrderBy(c => c.VendorId, StringComparer.Ordinal).ThenBy(c => c.ProductId, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"\b(headset|headphones?|earbuds|arctis|cloud|nova pro)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeadsetWords();

    [GeneratedRegex(@"\b(keyboard|keypad|apex|tkl)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardWords();

    [GeneratedRegex(@"\b(mouse|mice)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MouseWords();

    [GeneratedRegex(@"\b(led|rgb|aura|lighting|light controller|lamparray)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LightingWords();

    /// <summary>
    /// A first guess at what the device is, from its names and device classes. The catalogue overrides it for known models.
    /// Mice and keyboards both expose mouse and keyboard collections, so classes alone only decide when one of them is missing.
    /// </summary>
    public static PeripheralKind GuessKind(string name, IReadOnlyList<string> names, IReadOnlyList<string> classes)
    {
        // The receiver's own name only decides when nothing better is known: it usually has a mouse or keyboard behind it.
        foreach (var n in new[] { name }.Concat(names.Where(n => !IsGenericName(n) && !TransportName().IsMatch(n))))
        {
            if (n.Length == 0) continue;
            if (HeadsetWords().IsMatch(n)) return PeripheralKind.Headset;
            if (KeyboardWords().IsMatch(n)) return PeripheralKind.Keyboard;
            if (MouseWords().IsMatch(n)) return PeripheralKind.Mouse;
            if (LightingWords().IsMatch(n)) return PeripheralKind.Lighting;
        }
        bool keyboard = classes.Contains("Keyboard", StringComparer.OrdinalIgnoreCase);
        bool mouse = classes.Contains("Mouse", StringComparer.OrdinalIgnoreCase);
        if (keyboard && !mouse) return PeripheralKind.Keyboard;
        if (mouse && !keyboard) return PeripheralKind.Mouse;
        if (names.Any(n => TransportName().IsMatch(n))) return PeripheralKind.Receiver;
        return PeripheralKind.Other;
    }
}
