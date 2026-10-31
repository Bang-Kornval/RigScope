using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RigScope.Core.Settings;
using RigScope.Core.Updates;

namespace RigScope.App.ViewModels;

public sealed record ChangelogItem(ChangelogEntry Entry, bool IsCurrent)
{
    public string Title => $"RigScope {Entry.Version}";
    public string DateText => Entry.Date is { } d ? d.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : "";
}

/// <summary>Settings: check the PC again, check for updates (only when asked) and what's new.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public MainViewModel Main { get; }
    public string CurrentVersion { get; } = UpdateChecker.CurrentVersion.ToString();
    public IReadOnlyList<ChangelogItem> WhatsNew { get; }

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _updateMessage = "";
    [ObservableProperty] private ReleaseInfo? _available;
    [ObservableProperty] private IReadOnlyList<ChangelogSection> _availableNotes = [];

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadMessage = "";
    [ObservableProperty] private bool _downloadOk;
    [ObservableProperty] private string? _downloadedPath;

    [ObservableProperty] private bool _checkOnStartup;
    [ObservableProperty] private string _settingsMessage = "";

    private readonly SettingsStore _store = new();
    private UserSettings _settings;

    public SettingsViewModel(MainViewModel main)
    {
        Main = main;
        WhatsNew = Changelog.LoadBuiltIn()
            .Where(e => !e.IsUnreleased)
            .Select(e => new ChangelogItem(e, AppVersion.TryParse(e.Version, out var v) && v == UpdateChecker.CurrentVersion))
            .ToList();
        _settings = _store.Load();
        CheckOnStartup = _settings.CheckForUpdatesOnStartup;
        EarlyVersions = _settings.WantsEarlyVersions(UpdateChecker.CurrentVersion.IsPrerelease);
    }

    /// <summary>Beta testing opt-in: also offer alpha/beta releases.</summary>
    [ObservableProperty] private bool _earlyVersions;

    partial void OnCheckOnStartupChanged(bool value)
    {
        if (_settings.CheckForUpdatesOnStartup == value) return;
        Save(_settings with { CheckForUpdatesOnStartup = value });
    }

    partial void OnEarlyVersionsChanged(bool value)
    {
        if (_settings.WantsEarlyVersions(UpdateChecker.CurrentVersion.IsPrerelease) == value) return;
        Save(_settings with { EarlyVersions = value });
        // What counts as "newer" just changed, so an offer from the last check may no longer apply.
        Available = null;
        UpdateMessage = "";
    }

    private void Save(UserSettings settings)
    {
        _settings = settings;
        SettingsMessage = _store.Save(settings) ? "" : $"Couldn't save this choice to {_store.Path}. It applies until you close RigScope.";
    }

    /// <summary>Called once at startup. Quiet: only says something when there's a newer version.</summary>
    public async Task CheckAtStartupAsync()
    {
        if (CheckOnStartup && Available is null && !IsChecking) await RunCheckAsync(quiet: true);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunCheckAsync(quiet: false);

    private async Task RunCheckAsync(bool quiet)
    {
        IsChecking = true;
        if (!quiet) UpdateMessage = "Asking GitHub for the latest release…";
        Available = null;
        DownloadMessage = "";
        DownloadedPath = null;
        var result = await UpdateChecker.CheckAsync(EarlyVersions);
        if (!quiet || result.State == UpdateState.Available) UpdateMessage = result.Message;
        if (result.State == UpdateState.Available && result.Latest is { } latest)
        {
            // The release notes are the CHANGELOG section for that version, so the same parser reads them.
            AvailableNotes = Changelog.Parse($"## {latest.Version}\n{latest.Notes}").FirstOrDefault()?.Sections ?? [];
            Available = latest;
        }
        IsChecking = false;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Available is not { } release) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save the new RigScope",
            FileName = $"RigScope-{release.Version}.exe",
            Filter = "Program (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() != true) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            DownloadOk = false;
            DownloadMessage = "That's the RigScope you're running now. Pick another name or folder, then close this one and start the new file.";
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadMessage = "Downloading…";
        DownloadedPath = null;
        var result = await UpdateChecker.DownloadAsync(release, dialog.FileName, new Progress<double>(p => DownloadProgress = p));
        DownloadOk = result.Ok;
        DownloadMessage = result.Ok ? result.Message + " Close RigScope and start the new file." : result.Message;
        DownloadedPath = result.Ok ? dialog.FileName : null;
        IsDownloading = false;
    }

    [RelayCommand]
    private void ShowDownload()
    {
        if (DownloadedPath is not null && File.Exists(DownloadedPath))
            Process.Start("explorer.exe", $"/select,\"{DownloadedPath}\"");
    }

    [RelayCommand]
    private void OpenReleasePage() => OpenUrl(Available?.PageUrl ?? UpdateChecker.ReleasesPage);

    [RelayCommand]
    private static void OpenRepository() => OpenUrl($"https://github.com/{UpdateChecker.Repository}");

    private static void OpenUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
