using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Peripherals;

/// <summary>
/// What RigScope knows about peripheral vendors, their suites and device families. Loaded from advice/peripherals.json,
/// so facts can be fixed without C#. Unknown models from a known vendor get the vendor's fallback, which says "check"
/// rather than guessing.
/// </summary>
public sealed class PeripheralCatalogue
{
    public string Version { get; set; } = "";
    public string Updated { get; set; } = "";
    public List<SuiteDef> Suites { get; set; } = [];
    public List<VendorDef> Vendors { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PeripheralCatalogue Parse(string json) =>
        JsonSerializer.Deserialize<PeripheralCatalogue>(json, JsonOptions) ?? throw new JsonException("Empty peripheral catalogue.");

    private static readonly Lazy<PeripheralCatalogue> BuiltIn = new(() =>
    {
        using var stream = typeof(PeripheralCatalogue).Assembly.GetManifestResourceStream("RigScope.Core.peripherals.json")
            ?? throw new InvalidOperationException("Built-in peripheral catalogue is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The catalogue built into RigScope.Core.</summary>
    public static PeripheralCatalogue LoadBuiltIn() => BuiltIn.Value;

    public SuiteDef? Suite(string id) => Suites.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The vendor for a USB vendor id. Vendors on a shared id (HyperX on HP's, Wooting on Atmel's) also need their name in the device name.</summary>
    public VendorDef? VendorFor(string vendorId, string name = "") =>
        Vendors.FirstOrDefault(v => v.VendorIds.Any(id => id.Equals(vendorId, StringComparison.OrdinalIgnoreCase))
                                    && (v.RequireName.Length == 0 || Regex.IsMatch(name, v.RequireName, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))));

    /// <summary>A device family by id, for the UI's sources and support links.</summary>
    public (VendorDef Vendor, DeviceFamilyDef? Family)? Entry(string catalogueId)
    {
        foreach (var v in Vendors)
        {
            if (catalogueId == $"vendor:{v.Id}") return (v, null);
            if (v.Families.FirstOrDefault(f => f.Id == catalogueId) is { } f) return (v, f);
        }
        return null;
    }

    /// <summary>
    /// Matches a detected device: vendor by USB vendor id, then a family by name pattern first and product id second.
    /// No family = the vendor's fallback. Unknown vendor = no suite, settings unknown.
    /// </summary>
    public PeripheralInfo Match(PeripheralCandidate c)
    {
        var vendor = VendorFor(c.VendorId, c.Name);
        var info = new PeripheralInfo
        {
            VendorId = c.VendorId, ProductId = c.ProductId, Name = c.Name, Kind = c.Kind,
            ViaReceiver = c.ViaReceiver, Bluetooth = c.Bluetooth, DynamicLighting = c.DynamicLighting,
            Vendor = vendor?.Name ?? c.Manufacturer,
        };
        if (vendor is null)
        {
            info.DisplayName = DisplayName(info.Vendor, c.Name, c.Kind);
            return info;
        }

        var family = vendor.Families.FirstOrDefault(f => f.Match.Length > 0 && c.Name.Length > 0 && Regex.IsMatch(c.Name, f.Match, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                     ?? vendor.Families.FirstOrDefault(f => f.Pids.Any(p => p.Equals(c.ProductId, StringComparison.OrdinalIgnoreCase)));
        var facts = (DeviceFacts?)family ?? vendor.Fallback;
        var suite = Suite(vendor.Suite);

        info.CatalogueId = family?.Id ?? $"vendor:{vendor.Id}";
        info.KnownModel = family is not null;
        info.Kind = family?.Kind ?? c.Kind;
        info.Suite = suite?.Id ?? "";
        info.SuiteName = suite?.Name ?? "";
        info.OnboardMemory = facts.OnboardMemory;
        info.FeaturesNeedingSuite = [.. facts.FeaturesNeedingSuite];
        info.Notes = facts.Notes;
        info.DisplayName = family is { DisplayName.Length: > 0 } ? family.DisplayName : DisplayName(vendor.Name, c.Name, info.Kind);
        return info;
    }

    /// <summary>
    /// Matches every candidate and keeps the ones that are peripherals: known models, plus anything that looks like a
    /// mouse, keyboard, headset, lighting controller or receiver. Webcams, hubs and Bluetooth adapters drop out.
    /// </summary>
    public List<PeripheralInfo> Resolve(IEnumerable<PeripheralCandidate> candidates) =>
        candidates.Select(Match).Where(p => p.KnownModel || p.Kind != PeripheralKind.Other).ToList();

    /// <summary>"Logitech" + "PRO 2 LIGHTSPEED" → "Logitech PRO 2 LIGHTSPEED"; no name → "Logitech mouse".</summary>
    public static string DisplayName(string vendor, string name, PeripheralKind kind)
    {
        if (name.Length == 0) return $"{(vendor.Length > 0 ? vendor : "Unknown")} {KindText(kind)}";
        if (vendor.Length == 0 || name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{vendor} {name}";
    }

    public static string KindText(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Mouse => "mouse",
        PeripheralKind.Keyboard => "keyboard",
        PeripheralKind.Headset => "headset",
        PeripheralKind.Lighting => "lighting controller",
        PeripheralKind.Receiver => "wireless receiver",
        _ => "device",
    };

    /// <summary>Suite features that are set up, found by matching device names (e.g. Sonar's virtual audio devices) against the catalogue.</summary>
    public List<SuiteFeatureInUse> DetectFeatures(IEnumerable<PnpDevice> devices)
    {
        var working = devices.Where(d => d.Ok && d.Name.Length > 0).ToList();
        var found = new List<SuiteFeatureInUse>();
        foreach (var suite in Suites)
            foreach (var feature in suite.Features.Where(f => f.InUseWhen.Length > 0))
                if (working.FirstOrDefault(d => Regex.IsMatch(d.Name, feature.InUseWhen, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))) is { } hit)
                    found.Add(new SuiteFeatureInUse { Suite = suite.Id, Feature = feature.Name, Seen = hit.Name });
        return found;
    }

    /// <summary>
    /// Per suite: does anything on this PC need it running all the time?
    /// true  — a feature that needs it is set up, or a device keeps its settings in the suite instead of onboard;
    /// null  — a device's onboard memory is unknown, or (for suites that also drive hardware RigScope can't see) no device was found;
    /// false — every device keeps its settings onboard, or no device of that vendor is connected.
    /// </summary>
    public List<PeripheralSuiteInfo> Summarize(IReadOnlyList<PeripheralInfo> peripherals, IReadOnlyList<KnownAppInfo> apps, IReadOnlyList<SuiteFeatureInUse> featuresInUse)
    {
        var result = new List<PeripheralSuiteInfo>();
        foreach (var suite in Suites)
        {
            var app = apps.FirstOrDefault(a => a.Id.Equals(suite.Id, StringComparison.OrdinalIgnoreCase));
            var devices = peripherals.Where(p => p.Suite.Equals(suite.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var features = featuresInUse.Where(f => f.Suite.Equals(suite.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (app is null && devices.Count == 0 && features.Count == 0) continue;

            var info = new PeripheralSuiteInfo
            {
                Id = suite.Id, Name = suite.Name, Notes = suite.Notes,
                Running = app?.Running ?? false, StartsWithWindows = app?.StartsWithWindows ?? false,
                Devices = devices.Select(d => d.DisplayName).ToList(),
                OnlyIfYouUse = devices.SelectMany(d => d.FeaturesNeedingSuite).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };

            var needs = new List<string>();
            var unsure = new List<string>();
            // "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)" → "SteelSeries Sonar - Gaming"
            foreach (var f in features)
                needs.Add($"{f.Feature} is set up on this PC (Windows lists a device called {(f.Seen.IndexOf(" (", StringComparison.Ordinal) is > 0 and var i ? f.Seen[..i] : f.Seen)})");
            foreach (var d in devices.Where(d => d.OnboardMemory == OnboardMemory.No))
                needs.Add($"{d.DisplayName} keeps its settings in {suite.Name}, not on the device");
            foreach (var d in devices.Where(d => d.OnboardMemory == OnboardMemory.Unknown))
                unsure.Add($"RigScope doesn't know whether {d.DisplayName} saves its settings on the device");

            if (needs.Count > 0) { info.NeededForAlwaysOn = true; info.Reasons = needs; }
            else if (unsure.Count > 0) { info.NeededForAlwaysOn = null; info.Reasons = unsure; }
            else if (devices.Count > 0)
            {
                info.NeededForAlwaysOn = false;
                info.Reasons = [$"{Join(info.Devices)} {(devices.Count == 1 ? "keeps its" : "keep their")} settings on the device"];
            }
            else if (suite.NoDevice == NoDeviceMeans.NotNeeded)
            {
                info.NeededForAlwaysOn = false;
                info.Reasons = [$"No device that uses {suite.Name} is connected"];
            }
            else
            {
                info.NeededForAlwaysOn = null;
                info.Reasons = [$"No {suite.Name} device was found over USB, but it may also control parts RigScope can't see"];
            }
            result.Add(info);
        }
        return result;
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<NoDeviceMeans>))]
public enum NoDeviceMeans { Unknown, NotNeeded }

/// <summary>A peripheral suite. The id is a <see cref="Probes.KnownAppsProbe"/> catalogue id, so running/startup state comes from there.</summary>
public sealed class SuiteDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>What it means when no device of the vendor is connected: notNeeded for suites that only drive peripherals.</summary>
    public NoDeviceMeans NoDevice { get; set; }
    /// <summary>Other things the suite does that RigScope can't see.</summary>
    public string Notes { get; set; } = "";
    public List<SuiteFeatureDef> Features { get; set; } = [];
    public List<AdviceSource> Sources { get; set; } = [];
}

/// <summary>A suite feature that needs it running, and how to tell it's set up (a regex on Windows device names).</summary>
public sealed class SuiteFeatureDef
{
    public string Name { get; set; } = "";
    public string InUseWhen { get; set; } = "";
    public Evidence Evidence { get; set; }
    public List<AdviceSource> Sources { get; set; } = [];
}

public sealed class VendorDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>USB vendor ids in hex (046D…).</summary>
    public List<string> VendorIds { get; set; } = [];
    /// <summary>Regex the device name must match, for vendor ids shared with other products. Empty = any name.</summary>
    public string RequireName { get; set; } = "";
    /// <summary>Suite id, or empty when the vendor's devices don't need a background app.</summary>
    public string Suite { get; set; } = "";
    public string SupportUrl { get; set; } = "";
    public List<AdviceSource> VendorIdSources { get; set; } = [];
    /// <summary>For models RigScope doesn't know. Says "check", never guesses specifics.</summary>
    public DeviceFacts Fallback { get; set; } = new();
    public List<DeviceFamilyDef> Families { get; set; } = [];
}

/// <summary>What is stored on a device and what needs the suite, with how sure we are.</summary>
public class DeviceFacts
{
    public OnboardMemory OnboardMemory { get; set; }
    public List<string> FeaturesNeedingSuite { get; set; } = [];
    public string Notes { get; set; } = "";
    public Evidence Evidence { get; set; }
    public List<AdviceSource> Sources { get; set; } = [];
}

public sealed class DeviceFamilyDef : DeviceFacts
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Regex on the Windows device name. Checked before product ids.</summary>
    public string Match { get; set; } = "";
    /// <summary>USB product ids seen for this family (hex). Used when the name doesn't match.</summary>
    public List<string> Pids { get; set; } = [];
    public PeripheralKind? Kind { get; set; }
}
