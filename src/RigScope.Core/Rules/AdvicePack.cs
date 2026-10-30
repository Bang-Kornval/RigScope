using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RigScope.Core.Rules;

/// <summary>The advice data: every recommendation RigScope can make. Loaded from advice-pack.json.</summary>
public sealed class AdvicePack
{
    public string Version { get; set; } = "";
    public string Updated { get; set; } = "";
    public List<AdviceRule> Rules { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AdvicePack Parse(string json) =>
        JsonSerializer.Deserialize<AdvicePack>(json, JsonOptions) ?? throw new JsonException("Empty advice pack.");

    /// <summary>The pack built into RigScope.Core.</summary>
    public static AdvicePack LoadBuiltIn()
    {
        using var stream = typeof(AdvicePack).Assembly.GetManifestResourceStream("RigScope.Core.advice-pack.json")
            ?? throw new InvalidOperationException("Built-in advice pack is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

public enum AdviceCategory { InGame, Gpu, Display, Windows, Apps, Network, Firmware, Storage }
public enum AdviceKind { Setting, Preference, Myth }
public enum Level { None, Small, Medium, Large }
public enum Evidence { Low, Medium, High }

/// <summary>What RigScope concludes for one rule on one PC.</summary>
public enum FindingStatus
{
    /// <summary>Worth changing.</summary>
    Suggest,
    /// <summary>A real problem (e.g. RAM below rated speed, display below max refresh).</summary>
    Warn,
    /// <summary>Already set well.</summary>
    Good,
    /// <summary>Preference or context only; no verdict.</summary>
    Info,
    /// <summary>Common advice that doesn't help; shown so people can skip it.</summary>
    Myth,
    /// <summary>RigScope couldn't read the current state.</summary>
    Unknown,
}

public sealed class AdviceRule
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public AdviceCategory Category { get; set; }
    public AdviceKind Kind { get; set; } = AdviceKind.Setting;
    /// <summary>Game ids this rule is about. Empty = applies to every competitive game.</summary>
    public List<string> Games { get; set; } = [];
    /// <summary>Condition for the rule to appear at all (e.g. only on NVIDIA). Missing = always.</summary>
    public JsonNode? AppliesWhen { get; set; }
    /// <summary>Template for the current value, e.g. "{gpu.vendorDriverVersion}".</summary>
    public string? Current { get; set; }
    /// <summary>Checked in order; the first match wins. An outcome without "when" is the fallback.</summary>
    public List<AdviceOutcome> Outcomes { get; set; } = [];
    public string Recommendation { get; set; } = "";
    public string Why { get; set; } = "";
    public Level Impact { get; set; }
    public Evidence Evidence { get; set; }
    public List<string> Steps { get; set; } = [];
    public string Undo { get; set; } = "";
    public string Risk { get; set; } = "";
    public List<AdviceSource> Sources { get; set; } = [];
}

public sealed class AdviceOutcome
{
    public JsonNode? When { get; set; }
    public FindingStatus Status { get; set; }
    /// <summary>One line shown in the list; may contain templates.</summary>
    public string Summary { get; set; } = "";
}

public sealed class AdviceSource
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Date { get; set; } = "";
}
