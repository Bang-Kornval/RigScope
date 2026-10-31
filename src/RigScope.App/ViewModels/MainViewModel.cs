using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigScope.Core.Probes;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;

namespace RigScope.App.ViewModels;

/// <summary>Shared state: the snapshot, the advice engine and the latest storage scan. Pages read from it.</summary>
public sealed partial class AppState : ObservableObject
{
    public RuleEngine Engine { get; } = new(AdvicePack.LoadBuiltIn());

    [ObservableProperty] private SystemSnapshot? _snapshot;
    [ObservableProperty] private string? _gameId;

    /// <summary>The game the checks default to: CS2 if installed, else the competitive game played most recently.</summary>
    public string? DefaultGameId(SystemSnapshot s) =>
        s.Games.Any(g => g.Id == "cs2") ? "cs2"
        : s.Games.Where(g => g.IsCompetitive).OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).FirstOrDefault()?.Id;
}

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);

public sealed partial class MainViewModel : ObservableObject
{
    public AppState State { get; } = new();
    public ObservableCollection<NavItem> Pages { get; } = [];

    [ObservableProperty] private NavItem? _selectedPage;
    /// <summary>The page on screen: the selected nav item's page, or Settings (which isn't in the nav list).</summary>
    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _loadingText = "Starting…";
    [ObservableProperty] private string _statusText = "";

    public OverviewViewModel Overview { get; }
    public CheckViewModel Check { get; }
    public StorageViewModel Storage { get; }
    public ReportViewModel Report { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel()
    {
        Overview = new OverviewViewModel(State, this);
        Check = new CheckViewModel(State);
        Storage = new StorageViewModel(State);
        Report = new ReportViewModel(State, Check, Storage);
        Settings = new SettingsViewModel(this);
        // Segoe Fluent Icons glyphs.
        Pages.Add(new NavItem("Overview", "", Overview));
        Pages.Add(new NavItem("Competitive check", "", Check));
        Pages.Add(new NavItem("Storage", "", Storage));
        Pages.Add(new NavItem("Measure", "", new MeasureViewModel(State)));
        Pages.Add(new NavItem("Share report", "", Report));
        SelectedPage = Pages[0];
    }

    public void Navigate(ObservableObject page) => SelectedPage = Pages.FirstOrDefault(p => p.Page == page) ?? SelectedPage;

    partial void OnSelectedPageChanged(NavItem? value)
    {
        if (value is not null) CurrentPage = value.Page;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SelectedPage = null;
        CurrentPage = Settings;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadingText = "Reading your PC (nothing is changed)…";
        var snapshot = await ProbeRunner.CaptureAsync(progress: new Progress<string>(p => LoadingText = $"Reading {p.ToLowerInvariant()}…"));
        State.GameId ??= State.DefaultGameId(snapshot);
        State.Snapshot = snapshot;
        StatusText = snapshot.ProbeErrors.Count == 0
            ? $"Checked {snapshot.CapturedAt:HH:mm}"
            : $"Checked {snapshot.CapturedAt:HH:mm} · {snapshot.ProbeErrors.Count} part(s) couldn't be read";
        IsLoading = false;
    }
}
