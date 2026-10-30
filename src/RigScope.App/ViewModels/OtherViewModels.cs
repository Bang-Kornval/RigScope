using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RigScope.Core.Games;
using RigScope.Core.Report;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;

namespace RigScope.App.ViewModels;

public sealed record SpecRow(string Label, string Value, string Note = "");

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly MainViewModel _main;

    public ObservableCollection<SpecRow> Specs { get; } = [];
    public ObservableCollection<FindingItem> Top { get; } = [];
    public ObservableCollection<DriveInfoSnapshot> Drives { get; } = [];
    public ObservableCollection<InstalledGame> Games { get; } = [];

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _gameName = "";

    public OverviewViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        state.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.Snapshot) or nameof(AppState.GameId)) Refresh(); };
    }

    private void Refresh()
    {
        var s = _state.Snapshot;
        if (s is null) return;

        Specs.Clear();
        Specs.Add(new("Processor", s.Cpu.Name, s.Cpu.Cores > 0 ? $"{s.Cpu.Cores} cores, {s.Cpu.Threads} threads" + (s.Cpu.IsHybrid ? $" ({s.Cpu.PerformanceCores}P + {s.Cpu.EfficiencyCores}E)" : "") : ""));
        foreach (var g in s.Gpus)
            Specs.Add(new(g.IsIntegrated ? "Integrated graphics" : "Graphics", g.Name,
                (g.VendorDriverVersion.Length > 0 ? $"Driver {g.VendorDriverVersion}" : $"Driver {g.DriverVersion}") + (g.DriverAgeDays is { } d ? $", {d} days old" : "")));
        if (s.Memory.TotalBytes > 0)
            Specs.Add(new("Memory", $"{s.Memory.TotalGb} GB {s.Memory.Generation}", $"{s.Memory.ConfiguredMts} MT/s, {s.Memory.Modules.Count} stick(s)"));
        foreach (var d in s.Displays)
            Specs.Add(new(d.IsPrimary ? "Main display" : "Display", $"{d.MonitorName}", $"{d.Width}×{d.Height} at {d.RefreshHz} Hz" + (d.RunningBelowMaxRefresh ? $" (supports {d.MaxRefreshHz} Hz)" : "")));
        if (s.Network.FirstOrDefault(n => n.IsDefaultRoute) is { } net)
            Specs.Add(new("Network", net.Kind == AdapterKind.Wifi ? "Wi-Fi" : "Wired", net.Description));
        Specs.Add(new("Windows", s.Os.ProductName, $"{s.Os.DisplayVersion} (build {s.Os.Build})"));
        if (s.Firmware.BoardProduct.Length > 0)
            Specs.Add(new("Motherboard", s.Firmware.BoardProduct, $"BIOS {s.Firmware.BiosVersion}" + (s.Firmware.BiosDate is { } bd ? $" from {bd:MMM yyyy}" : "")));

        var game = _state.GameId is null ? null : GameCatalogue.ById(_state.GameId);
        GameName = game?.Name ?? "your PC";
        var findings = _state.Engine.Evaluate(s, _state.GameId);
        Top.Clear();
        foreach (var f in findings.Where(f => f.Status is FindingStatus.Warn or FindingStatus.Suggest).Take(5)) Top.Add(new FindingItem(f));
        var worth = findings.Count(f => f.Status is FindingStatus.Warn or FindingStatus.Suggest);
        var good = findings.Count(f => f.Status == FindingStatus.Good);
        Headline = worth == 0
            ? $"Your PC already follows the advice RigScope has for {GameName}. {good} settings checked and good."
            : $"{worth} thing{(worth == 1 ? "" : "s")} worth changing for {GameName}. {good} already set well.";

        Drives.Clear();
        foreach (var d in s.Drives) Drives.Add(d);
        Games.Clear();
        foreach (var g in s.Games.Where(g => g.IsCompetitive)) Games.Add(g);
    }

    [RelayCommand] private void ShowCheck() => _main.Navigate(_main.Check);
    [RelayCommand] private void ShowStorage() => _main.Navigate(_main.Storage);
}

public sealed record ToolLink(string Name, string What, string Url);

public sealed partial class MeasureViewModel(AppState state) : ObservableObject
{
    public AppState State { get; } = state;

    public IReadOnlyList<ToolLink> Tools { get; } =
    [
        new("CapFrameX", "Records frame times and shows average, 1% and 0.1% lows. Easiest way to compare before and after.", "https://www.capframex.com/"),
        new("Intel PresentMon", "Frame times plus whether you're CPU- or GPU-bound (\"GPU Busy\"), and the present mode the game uses.", "https://github.com/GameTechDev/PresentMon"),
        new("NVIDIA FrameView", "Frame times and the PC latency figure that Reflex reports (NVIDIA cards).", "https://www.nvidia.com/en-us/geforce/technologies/frameview/"),
        new("LatencyMon", "Finds drivers and background apps that cause DPC latency spikes and stutter.", "https://www.resplendence.com/latencymon"),
        new("Waveform bufferbloat test", "Shows whether your ping jumps when the connection is busy.", "https://www.waveform.com/tools/bufferbloat"),
    ];

    [RelayCommand]
    private static void Open(string? url)
    {
        if (url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

public sealed partial class ReportViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly CheckViewModel _check;
    private readonly StorageViewModel _storage;

    [ObservableProperty] private bool _redact = true;
    [ObservableProperty] private bool _includeGood = true;
    [ObservableProperty] private bool _includeMyths;
    [ObservableProperty] private bool _includeStorage = true;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _message = "";

    public ReportViewModel(AppState state, CheckViewModel check, StorageViewModel storage)
    {
        _state = state;
        _check = check;
        _storage = storage;
        check.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CheckViewModel.AllFindings)) Build(); };
        storage.Notes.CollectionChanged += (_, _) => Build();
    }

    partial void OnRedactChanged(bool value) => Build();
    partial void OnIncludeGoodChanged(bool value) => Build();
    partial void OnIncludeMythsChanged(bool value) => Build();
    partial void OnIncludeStorageChanged(bool value) => Build();

    private void Build()
    {
        if (_state.Snapshot is null) return;
        Text = ReportBuilder.Build(_state.Snapshot, _check.AllFindings, _state.GameId, _storage.Notes.ToList(),
            new ReportOptions(Redact, IncludeGood, IncludeMyths, IncludeStorage));
        Message = "";
    }

    [RelayCommand]
    private void Copy()
    {
        try { Clipboard.SetText(Text); Message = "Copied. Paste it into Discord, Reddit or a message."; }
        catch (System.Runtime.InteropServices.COMException) { Message = "Windows couldn't open the clipboard. Select the text and press Ctrl+C."; }
    }

    [RelayCommand]
    private void Save()
    {
        var dialog = new SaveFileDialog { FileName = "rigscope-report.md", Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, Text);
        Message = $"Saved to {dialog.FileName}";
    }

    /// <summary>Full snapshot for bug reports. Redacted the same way as the report.</summary>
    [RelayCommand]
    private void SaveSnapshot()
    {
        if (_state.Snapshot is null) return;
        var dialog = new SaveFileDialog { FileName = "rigscope-snapshot.json", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var json = SnapshotJson.Serialize(_state.Snapshot);
        File.WriteAllText(dialog.FileName, Redact ? ReportBuilder.Redact(json, _state.Snapshot) : json);
        Message = $"Saved to {dialog.FileName}";
    }
}
