using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;
using RigScope.Core.Storage;

namespace RigScope.App.ViewModels;

public sealed record DriveOption(DriveInfoSnapshot Drive)
{
    public string Display => $"{Drive.Letter} {Drive.Label}".Trim() + $"  ·  {SnapshotQuery.FormatBytes(Drive.FreeBytes)} free of {SnapshotQuery.FormatBytes(Drive.TotalBytes)}";
    public string Root => Drive.Letter + "\\";
}

/// <summary>A row in the folder list. Pseudo-rows ("Files directly here", "smaller folders") have no node to open.</summary>
public sealed record FolderRow(string Name, long Size, double Percent, long Files, FolderNode? Node, string? FullPath, bool Denied = false)
{
    public bool CanOpen => Node is { Children.Count: > 0 };
    public string Meta => (Files > 0 ? $"{Files:N0} files · " : "") + $"{Percent:0.0}%" + (Denied ? " · couldn't read all of it" : "");
}

public sealed record ExtensionRow(string Extension, long Bytes, double Share);
public sealed record ChangeRow(string Path, long Delta)
{
    public string DeltaText => (Delta > 0 ? "+" : "−") + SnapshotQuery.FormatBytes(Math.Abs(Delta));
    public bool Freed => Delta < 0;
}

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly ScanHistory _history = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<DriveOption> Drives { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumb { get; } = [];
    public ObservableCollection<FolderRow> Rows { get; } = [];
    public ObservableCollection<LargeFile> Files { get; } = [];
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];
    public ObservableCollection<ChangeRow> Changes { get; } = [];
    public ObservableCollection<CleanupNote> Notes { get; } = [];

    [ObservableProperty] private DriveOption? _selectedDrive;
    [ObservableProperty] private ScanResult? _scan;
    [ObservableProperty] private FolderNode? _current;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private string _changesSummary = "";
    [ObservableProperty] private string _summary = "";

    public StorageViewModel(AppState state)
    {
        _state = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppState.Snapshot) || state.Snapshot is null) return;
            Drives.Clear();
            foreach (var d in state.Snapshot.Drives.OrderByDescending(d => d.IsSystem)) Drives.Add(new DriveOption(d));
            SelectedDrive ??= Drives.FirstOrDefault();
        };
    }

    public bool HasScan => Scan is not null;

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedDrive is null || IsScanning) return;
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var root = SelectedDrive.Root;
        try
        {
            var result = await DirectoryScanner.ScanAsync(root,
                new Progress<ScanProgress>(p => ProgressText = $"{p.Files:N0} files · {SnapshotQuery.FormatBytes(p.Bytes)}"), _cts.Token);
            var previous = await Task.Run(() => _history.Previous(root, result.ScannedAt));
            await Task.Run(() => _history.Save(result));
            Show(result, previous);
        }
        catch (OperationCanceledException) { ProgressText = "Scan cancelled."; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Show(ScanResult result, ScanResult? previous)
    {
        Scan = result;
        Summary = $"{SnapshotQuery.FormatBytes(result.Root.Size)} in {result.FileCount:N0} files, scanned in {result.Duration.TotalSeconds:0} s. "
                  + (result.Unaccounted > 1L << 30 ? $"{SnapshotQuery.FormatBytes(result.Unaccounted)} is in system or protected folders RigScope can't read without admin." : "");
        Breadcrumb.Clear();
        Breadcrumb.Add(result.Root);
        Current = result.Root;

        Extensions.Clear();
        var top = result.Extensions.OrderByDescending(kv => kv.Value).Take(25).ToList();
        foreach (var (ext, bytes) in top) Extensions.Add(new ExtensionRow(ext, bytes, top.Count == 0 ? 0 : bytes * 100d / top[0].Value));

        Notes.Clear();
        foreach (var n in CleanupAdvisor.Analyze(result, _state.Snapshot)) Notes.Add(n);

        Changes.Clear();
        if (previous is null) ChangesSummary = "This is the first scan of this drive. Scan again later to see what changed.";
        else
        {
            var diff = ScanDiff.Compare(previous, result);
            ChangesSummary = $"Since {previous.ScannedAt:d MMM HH:mm}: free space {(diff.FreeDelta >= 0 ? "+" : "−")}{SnapshotQuery.FormatBytes(Math.Abs(diff.FreeDelta))}.";
            foreach (var c in diff.Changes.Take(20)) Changes.Add(new ChangeRow(c.Path, c.Delta));
        }
        ApplyFileFilter();
        OnPropertyChanged(nameof(HasScan));
    }

    partial void OnCurrentChanged(FolderNode? value)
    {
        Rows.Clear();
        if (value is null) return;
        var path = CurrentPath();
        foreach (var r in RowsFor(value, path)) Rows.Add(r);
    }

    /// <summary>The 40 largest subfolders, plus "files directly here" and the remaining small folders rolled together.</summary>
    public static List<FolderRow> RowsFor(FolderNode node, string path)
    {
        var rows = new List<FolderRow>();
        double total = Math.Max(1, node.Size);
        foreach (var c in node.Children.Take(40))
            rows.Add(new FolderRow(c.Name, c.Size, c.Size * 100 / total, c.Files, c, Path.Combine(path, c.Name), c.Denied));
        var rest = node.Children.Skip(40).ToList();
        if (rest.Count > 0)
            rows.Add(new FolderRow($"{rest.Count} smaller folders", rest.Sum(r => r.Size), rest.Sum(r => r.Size) * 100 / total, rest.Sum(r => r.Files), null, null));
        if (node.OwnFileBytes > 0)
            rows.Add(new FolderRow("Files directly here", node.OwnFileBytes, node.OwnFileBytes * 100 / total, 0, null, path));
        return rows.OrderByDescending(r => r.Size).ToList();
    }

    private string CurrentPath()
    {
        if (Breadcrumb.Count == 0) return "";
        var root = Scan?.RootPath ?? Breadcrumb[0].Name;
        return Breadcrumb.Skip(1).Aggregate(root, (p, n) => Path.Combine(p, n.Name));
    }

    [RelayCommand]
    private void Open(FolderNode? node)
    {
        if (node is null || node.Children.Count == 0) return;
        Breadcrumb.Add(node);
        Current = node;
    }

    [RelayCommand]
    private void OpenRow(FolderRow? row)
    {
        if (row?.CanOpen == true) Open(row.Node);
    }

    [RelayCommand]
    private void GoTo(FolderNode? node)
    {
        if (node is null) return;
        var i = Breadcrumb.IndexOf(node);
        if (i < 0) return;
        while (Breadcrumb.Count > i + 1) Breadcrumb.RemoveAt(Breadcrumb.Count - 1);
        Current = node;
    }

    [RelayCommand]
    private void Up()
    {
        if (Breadcrumb.Count > 1) GoTo(Breadcrumb[^2]);
    }

    partial void OnFileFilterChanged(string value) => ApplyFileFilter();

    private void ApplyFileFilter()
    {
        Files.Clear();
        if (Scan is null) return;
        var q = FileFilter.Trim();
        foreach (var f in Scan.LargestFiles.Where(f => q.Length == 0 || f.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200))
            Files.Add(f);
    }

    /// <summary>Opens File Explorer with the item selected. Read-only: Explorer shows it, RigScope changes nothing.</summary>
    [RelayCommand]
    private static void ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    [RelayCommand]
    private static void CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Clipboard.SetText(path); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
