using RigScope.Core.Peripherals;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

/// <summary>
/// Lists mice, keyboards, headsets and lighting controllers from Windows' device list (WMI Win32_PnPEntity), the same list
/// Device Manager shows. It only reads names and ids: no device handle is opened, no HID report is sent or read.
/// </summary>
public sealed class PeripheralProbe : IProbe
{
    public string Name => "Peripherals";

    public void Collect(SystemSnapshot s)
    {
        var rows = Sys.Wmi("SELECT Name, DeviceID, PNPClass, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity");
        // Every PC has dozens of devices; an empty list means WMI didn't answer, not that nothing is plugged in.
        if (rows.Count == 0) throw new InvalidOperationException("Windows didn't return its device list.");
        var devices = rows.Select(r => new PnpDevice(r.Str("DeviceID"), r.Str("Name"), r.Str("PNPClass"), r.Str("Manufacturer"), r.Long("ConfigManagerErrorCode") == 0)).ToList();

        var catalogue = PeripheralCatalogue.LoadBuiltIn();
        s.Peripherals = catalogue.Resolve(PeripheralDetection.Group(devices));
        s.SuiteFeaturesInUse = catalogue.DetectFeatures(devices);
    }
}

/// <summary>Works out which suites the peripherals need. Runs after the apps and peripheral probes because it reads both.</summary>
public sealed class PeripheralSuitesProbe : IProbe
{
    public string Name => "Peripheral suites";

    public void Collect(SystemSnapshot s)
    {
        // Without a device list, "no device connected" would be a guess. Leave suites empty so the general advice applies.
        if (s.ProbeErrors.ContainsKey(new PeripheralProbe().Name)) return;
        s.Suites = PeripheralCatalogue.LoadBuiltIn().Summarize(s.Peripherals, s.Apps, s.SuiteFeaturesInUse);
    }
}
