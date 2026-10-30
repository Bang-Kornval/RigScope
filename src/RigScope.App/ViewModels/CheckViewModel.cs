using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigScope.Core.Games;
using RigScope.Core.Rules;

namespace RigScope.App.ViewModels;

public sealed record GameOption(string? Id, string Name, bool Installed)
{
    public string Display => Id is null ? Name : Installed ? Name : $"{Name} (not installed)";
}

public enum FindingFilter { WorthDoing, All, Good, Preferences, Myths }

public sealed record NumberedStep(string Number, string Text);

/// <summary>One finding as the UI shows it.</summary>
public sealed class FindingItem(Finding f)
{
    public Finding Finding => f;
    public string Title => f.Title;
    public string Summary => f.Summary;
    public string Current => f.Current;
    public FindingStatus Status => f.Status;
    public string StatusText => f.Status switch
    {
        FindingStatus.Warn => "Fix this",
        FindingStatus.Suggest => "Worth doing",
        FindingStatus.Good => "Good",
        FindingStatus.Info => f.Rule.Kind == AdviceKind.Preference ? "Preference" : "Check",
        FindingStatus.Myth => "Myth",
        _ => "Unknown",
    };
    public string Category => f.Rule.Category switch
    {
        AdviceCategory.InGame => "In-game settings",
        AdviceCategory.Gpu => "Graphics driver",
        AdviceCategory.Display => "Display",
        AdviceCategory.Windows => "Windows",
        AdviceCategory.Apps => "Background apps",
        AdviceCategory.Network => "Network",
        AdviceCategory.Firmware => "Hardware and BIOS",
        AdviceCategory.Storage => "Storage",
        _ => "Other",
    };
    public string Recommendation => f.Recommendation;
    public string Why => f.Rule.Why;
    public IReadOnlyList<NumberedStep> Steps => f.Steps.Select((s, i) => new NumberedStep($"{i + 1}.", s)).ToList();
    public string Undo => f.Rule.Undo;
    public string Risk => f.Rule.Risk;
    public string Impact => $"Impact: {f.Rule.Impact.ToString().ToLowerInvariant()}";
    public string Evidence => $"Evidence: {f.Rule.Evidence.ToString().ToLowerInvariant()}";
    public IReadOnlyList<AdviceSource> Sources => f.Rule.Sources;
    public bool HasSteps => f.Steps.Count > 0;
    public bool HasSources => f.Rule.Sources.Count > 0;
}

public sealed partial class CheckViewModel : ObservableObject
{
    private readonly AppState _state;
    private List<Finding> _all = [];

    public ObservableCollection<GameOption> Games { get; } = [];
    public ObservableCollection<FindingItem> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private GameOption? _selectedGame;
    [ObservableProperty] private FindingFilter _filter = FindingFilter.WorthDoing;
    [ObservableProperty] private FindingItem? _selected;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _emptyText = "";

    public CheckViewModel(AppState state)
    {
        _state = state;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FindingItem.Category)));
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Rebuild(); };
    }

    public List<Finding> AllFindings => _all;

    private void Rebuild()
    {
        var s = _state.Snapshot;
        if (s is null) return;
        var installed = s.Games.Select(g => g.Id).ToHashSet();
        Games.Clear();
        foreach (var g in GameCatalogue.Competitive.OrderByDescending(g => installed.Contains(g.Id)).ThenBy(g => g.Id == "cs2" ? 0 : 1))
            Games.Add(new GameOption(g.Id, g.Name, installed.Contains(g.Id)));
        Games.Add(new GameOption(null, "PC only (no game)", true));
        SelectedGame = Games.FirstOrDefault(g => g.Id == _state.GameId) ?? Games[^1];
    }

    partial void OnSelectedGameChanged(GameOption? value)
    {
        _state.GameId = value?.Id;
        Evaluate();
    }

    partial void OnFilterChanged(FindingFilter value) => ApplyFilter();

    private void Evaluate()
    {
        if (_state.Snapshot is null) return;
        _all = _state.Engine.Evaluate(_state.Snapshot, SelectedGame?.Id);
        int worth = _all.Count(f => f.Status is FindingStatus.Warn or FindingStatus.Suggest);
        int good = _all.Count(f => f.Status == FindingStatus.Good);
        int myths = _all.Count(f => f.Status == FindingStatus.Myth);
        Counts = $"{worth} worth doing · {good} already good · {myths} myths to skip";
        ApplyFilter();
        OnPropertyChanged(nameof(AllFindings));
    }

    private void ApplyFilter()
    {
        var keep = _all.Where(f => Filter switch
        {
            FindingFilter.WorthDoing => f.Status is FindingStatus.Warn or FindingStatus.Suggest or FindingStatus.Unknown
                                        || (f.Status == FindingStatus.Info && f.Rule.Kind == AdviceKind.Setting),
            FindingFilter.Good => f.Status == FindingStatus.Good,
            FindingFilter.Preferences => f.Rule.Kind == AdviceKind.Preference,
            FindingFilter.Myths => f.Status == FindingStatus.Myth,
            _ => true,
        });
        Items.Clear();
        foreach (var f in keep) Items.Add(new FindingItem(f));
        Selected = Items.FirstOrDefault();
        EmptyText = Items.Count == 0
            ? Filter == FindingFilter.WorthDoing ? "Nothing to change here. Your setup already follows the advice RigScope has for this game." : "Nothing in this list."
            : "";
    }

    [RelayCommand]
    private static void OpenSource(AdviceSource? source)
    {
        if (source is null || !source.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
    }
}
