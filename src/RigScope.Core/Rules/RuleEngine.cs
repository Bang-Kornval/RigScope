using System.Text.Json.Nodes;
using RigScope.Core.Games;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Rules;

/// <summary>One rule's result on one PC. Text fields are already rendered for this PC and game.</summary>
public sealed record Finding(AdviceRule Rule, FindingStatus Status, string Summary, string Current)
{
    public string Title { get; init; } = Rule.Title;
    public string Recommendation { get; init; } = Rule.Recommendation;
    public IReadOnlyList<string> Steps { get; init; } = Rule.Steps;

    /// <summary>Sort key: problems first, then by impact, then category.</summary>
    public int Rank => Status switch
    {
        FindingStatus.Warn => 0,
        FindingStatus.Suggest => 1,
        FindingStatus.Unknown => 2,
        FindingStatus.Info => 3,
        FindingStatus.Good => 4,
        FindingStatus.Myth => 5,
        _ => 6,
    } * 10 + (3 - (int)Rule.Impact);
}

public sealed class RuleEngine(AdvicePack pack)
{
    public AdvicePack Pack { get; } = pack;

    /// <summary>
    /// Evaluates every rule that applies to this PC and the chosen game.
    /// gameId null = only the general PC rules. A rule whose condition throws is reported as Unknown instead of crashing.
    /// </summary>
    public List<Finding> Evaluate(SystemSnapshot snapshot, string? gameId)
    {
        var root = BuildRoot(snapshot, gameId);
        var findings = new List<Finding>();
        foreach (var rule in Pack.Rules)
        {
            if (rule.Games.Count > 0 && (gameId is null || !rule.Games.Contains(gameId))) continue;
            try
            {
                if (!SnapshotQuery.Evaluate(rule.AppliesWhen, root)) continue;
                var outcome = rule.Outcomes.FirstOrDefault(o => SnapshotQuery.Evaluate(o.When, root));
                if (outcome is null) continue;
                var status = rule.Kind == AdviceKind.Myth ? FindingStatus.Myth : outcome.Status;
                findings.Add(new Finding(rule, status, SnapshotQuery.Render(outcome.Summary, root), SnapshotQuery.Render(rule.Current, root))
                {
                    Title = SnapshotQuery.Render(rule.Title, root),
                    Recommendation = SnapshotQuery.Render(rule.Recommendation, root),
                    Steps = rule.Steps.Select(s => SnapshotQuery.Render(s, root)).ToList(),
                });
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            {
                findings.Add(new Finding(rule, FindingStatus.Unknown, $"RigScope couldn't evaluate this check ({ex.Message}).", ""));
            }
        }
        return findings.OrderBy(f => f.Rank).ThenBy(f => f.Rule.Category).ToList();
    }

    /// <summary>The snapshot as JSON plus a "game" object describing the selected game (id, name, installed, latencyTech…).</summary>
    public static JsonNode BuildRoot(SystemSnapshot snapshot, string? gameId)
    {
        var root = SnapshotJson.ToNode(snapshot);
        if (gameId is not null && GameCatalogue.ById(gameId) is { } def)
        {
            var installed = snapshot.Games.FirstOrDefault(g => g.Id == gameId);
            var drive = installed is null ? null : snapshot.Drives.FirstOrDefault(d => d.Letter.Equals(installed.DriveLetter, StringComparison.OrdinalIgnoreCase));
            root["game"] = new JsonObject
            {
                ["id"] = def.Id,
                ["name"] = def.Name,
                ["installed"] = installed is not null,
                ["deepProfile"] = def.DeepProfile,
                ["latencyTech"] = def.LatencyTech,
                ["configHint"] = def.ConfigHint,
                ["drive"] = installed?.DriveLetter ?? "",
                ["launcher"] = installed?.Launcher ?? "",
                ["driveMediaType"] = drive?.MediaType ?? "",
                ["driveFreeBytes"] = drive?.FreeBytes,
            };
        }
        return root;
    }
}
