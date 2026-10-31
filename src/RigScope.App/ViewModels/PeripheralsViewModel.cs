using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigScope.Core.Peripherals;
using RigScope.Core.Probes;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;

namespace RigScope.App.ViewModels;

/// <summary>One connected device as shown on the Peripherals page.</summary>
public sealed record DeviceCard(
    string Title,
    string Kind,
    string Onboard,
    FindingStatus OnboardStatus,
    string NeedsSuite,
    string Notes,
    string Extra,
    string SupportUrl,
    IReadOnlyList<AdviceSource> Sources)
{
    public bool HasSources => Sources.Count > 0;
}

/// <summary>A suite and the devices that belong to it. Devices without a suite are grouped under "Other devices".</summary>
public sealed record SuiteGroup(
    string Title,
    string State,
    string Verdict,
    FindingStatus VerdictStatus,
    IReadOnlyList<string> Reasons,
    string OnlyIfYouUse,
    string Notes,
    IReadOnlyList<DeviceCard> Devices)
{
    public bool HasVerdict => Verdict.Length > 0;
    public bool HasDevices => Devices.Count > 0;
}

/// <summary>Which suite each mouse, keyboard, headset and lighting controller belongs to, and which suites can stop starting with Windows. Read-only.</summary>
public sealed partial class PeripheralsViewModel : ObservableObject
{
    private readonly AppState _state;

    public ObservableCollection<SuiteGroup> Groups { get; } = [];

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _emptyText = "";

    public PeripheralsViewModel(AppState state)
    {
        _state = state;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Refresh(); };
    }

    private void Refresh()
    {
        var s = _state.Snapshot;
        if (s is null) return;
        var catalogue = PeripheralCatalogue.LoadBuiltIn();

        Groups.Clear();
        foreach (var suite in s.Suites.OrderByDescending(x => x.Active).ThenBy(x => x.NeededForAlwaysOn == false ? 0 : x.NeededForAlwaysOn is null ? 1 : 2))
        {
            var devices = s.Peripherals.Where(p => p.Suite == suite.Id).Select(p => Card(p, catalogue)).ToList();
            var (verdict, status) = Verdict(suite);
            Groups.Add(new SuiteGroup(suite.Name, State(suite), verdict, status, suite.Reasons,
                suite.OnlyIfYouUse.Count == 0 ? ""
                : suite.NeededForAlwaysOn == true ? $"It also provides: {string.Join(", ", suite.OnlyIfYouUse)}."
                : $"Only needed running if you use: {string.Join(", ", suite.OnlyIfYouUse)}.",
                suite.Notes, devices));
        }
        var others = s.Peripherals.Where(p => p.Suite.Length == 0 || s.Suites.All(x => x.Id != p.Suite)).Select(p => Card(p, catalogue)).ToList();
        if (others.Count > 0)
            Groups.Add(new SuiteGroup("Other devices", "", "", FindingStatus.Info, [], "", "", others));

        var active = s.Suites.Where(x => x.Active).ToList();
        int stoppable = active.Count(x => x.NeededForAlwaysOn == false);
        int needed = active.Count(x => x.NeededForAlwaysOn == true);
        int unsure = active.Count(x => x.NeededForAlwaysOn is null);

        EmptyText = "";
        if (s.ProbeErrors.TryGetValue(new PeripheralProbe().Name, out var error))
        {
            Headline = "RigScope couldn't read Windows' device list.";
            Detail = error;
        }
        else if (active.Count > 0)
        {
            Headline = (stoppable, unsure) switch
            {
                ( > 0, _) => $"You can stop {stoppable} of {active.Count} suite{(active.Count == 1 ? "" : "s")} from running all the time.",
                (0, 0) => active.Count == 1 ? "The suite that runs is needed by a device or feature you use." : $"All {active.Count} suites that run are needed by a device or feature you use.",
                _ => $"RigScope can't tell whether you need {unsure} of {active.Count} suite{(active.Count == 1 ? "" : "s")} running.",
            };
            var parts = new List<string>();
            if (needed > 0 && (stoppable > 0 || unsure > 0)) parts.Add($"{needed} {(needed == 1 ? "is" : "are")} needed for a device or feature you use.");
            if (unsure > 0 && stoppable > 0) parts.Add($"For {unsure}, RigScope doesn't know enough about your devices. Check below.");
            if (stoppable > 0) parts.Add("Save your profiles to the devices first, then turn the suite off in Task Manager › Startup apps.");
            Detail = string.Join(" ", parts);
        }
        else if (s.Peripherals.Count > 0)
        {
            Headline = "No peripheral suite is running or starting with Windows.";
            Detail = "Your devices use the settings stored on them. Open a suite only when you want to change something.";
        }
        else
        {
            Headline = "";
            Detail = "";
            EmptyText = "RigScope didn't find a mouse, keyboard, headset or lighting controller it recognises, and no peripheral suite is running. Nothing to do here.";
        }
    }

    private static string State(PeripheralSuiteInfo s) => (s.Running, s.StartsWithWindows) switch
    {
        (true, true) => "Running · starts with Windows",
        (true, false) => "Running · doesn't start with Windows",
        (false, true) => "Not running now · starts with Windows",
        _ => "Not running",
    };

    private static (string Text, FindingStatus Status) Verdict(PeripheralSuiteInfo s) => (s.Active, s.NeededForAlwaysOn) switch
    {
        (true, false) => ("Not needed all the time", FindingStatus.Suggest),
        (true, true) => ("Keep it running", FindingStatus.Good),
        (true, null) => ("Check", FindingStatus.Unknown),
        (false, true) => ("Needed, but not running", FindingStatus.Warn),
        _ => ("Nothing to do", FindingStatus.Good),
    };

    private static DeviceCard Card(PeripheralInfo p, PeripheralCatalogue catalogue)
    {
        var kind = char.ToUpperInvariant(PeripheralCatalogue.KindText(p.Kind)[0]) + PeripheralCatalogue.KindText(p.Kind)[1..];
        if (p.ViaReceiver) kind += " · via wireless receiver";
        if (p.Bluetooth) kind += " · Bluetooth";
        if (!p.KnownModel && p.CatalogueId.Length > 0) kind += " · model not in RigScope's list yet";

        var (onboard, status) = p.OnboardMemory switch
        {
            OnboardMemory.Yes => ("Settings stored on the device: yes", FindingStatus.Good),
            OnboardMemory.No => ("Settings stored on the device: no", FindingStatus.Info),
            _ => ("Settings stored on the device: check", FindingStatus.Unknown),
        };

        string suite = p.SuiteName.Length > 0 ? p.SuiteName : "its software";
        string needs = (p.CatalogueId.Length, p.OnboardMemory) switch
        {
            (0, _) => "RigScope has no notes for this vendor.",
            (_, OnboardMemory.Yes) when p.FeaturesNeedingSuite.Count > 0 => $"Needs {suite} running only for: {string.Join(", ", p.FeaturesNeedingSuite)}. Otherwise only to change settings.",
            (_, OnboardMemory.Yes) => $"Needs {suite} only to change settings.",
            (_, OnboardMemory.No) => $"Needs {suite} running to keep its settings.",
            _ => $"Check in {suite} whether it can save a profile to onboard memory.",
        };

        var extra = p.DynamicLighting ? "Windows lists it as a Dynamic Lighting device, so Settings › Personalization › Dynamic Lighting can also set its lighting." : "";

        var entry = catalogue.Entry(p.CatalogueId);
        var sources = entry is { } e ? (IReadOnlyList<AdviceSource>)((DeviceFacts?)e.Family ?? e.Vendor.Fallback).Sources : [];
        return new DeviceCard(p.DisplayName, kind, onboard, status, needs, p.Notes, extra, entry?.Vendor.SupportUrl ?? "", sources);
    }

    [RelayCommand]
    private static void Open(string? url)
    {
        if (url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
