using System.Reflection;
using System.Text.RegularExpressions;

namespace RigScope.Core.Updates;

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

public sealed record ChangelogEntry(string Version, DateOnly? Date, IReadOnlyList<ChangelogSection> Sections)
{
    public bool IsUnreleased => Version.Equals("Unreleased", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads CHANGELOG.md (embedded). Understands <c>## [1.2.0] - 2026-10-31</c> version headings, <c>### Group</c> headings
/// and <c>- </c> / <c>* </c> bullets; indented lines continue the previous bullet. Everything else is ignored.
/// </summary>
public static partial class Changelog
{
    public static IReadOnlyList<ChangelogEntry> LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RigScope.Core.CHANGELOG.md")
            ?? throw new InvalidOperationException("CHANGELOG.md is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? version = null;
        DateOnly? date = null;
        var sections = new List<(string Title, List<string> Items)>();

        void Flush()
        {
            if (version is null) return;
            entries.Add(new ChangelogEntry(version, date,
                sections.Where(s => s.Items.Count > 0).Select(s => new ChangelogSection(s.Title, s.Items)).ToList()));
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (VersionHeading().Match(line) is { Success: true } vh)
            {
                Flush();
                version = vh.Groups["v"].Value;
                date = DateOnly.TryParse(vh.Groups["d"].Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                sections = [];
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal)) { Flush(); version = null; }
            else if (version is null) { }
            else if (line.StartsWith("### ", StringComparison.Ordinal)) sections.Add((Plain(line[4..]), []));
            else if (line.TrimStart() is var t && (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)))
            {
                if (sections.Count == 0) sections.Add(("", []));
                sections[^1].Items.Add(Plain(t[2..]));
            }
            else if (raw.StartsWith("  ", StringComparison.Ordinal) && line.Length > 0 && sections.Count > 0 && sections[^1].Items.Count > 0)
                sections[^1].Items[^1] += " " + Plain(line.Trim());
        }
        Flush();
        return entries;
    }

    /// <summary>Drops the Markdown that reads badly as plain text: bold, code ticks and link targets.</summary>
    private static string Plain(string s) => MarkdownLink().Replace(s, "$1").Replace("**", "").Replace("`", "").Trim();

    [GeneratedRegex(@"^##\s+\[?(?<v>[^\]\s]+)\]?(?:\s+[-–]\s+(?<d>\d{4}-\d{2}-\d{2}))?")]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
}
