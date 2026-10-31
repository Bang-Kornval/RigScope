using System.Reflection;

namespace RigScope.Core.Updates;

/// <summary>
/// A release version like 1.2.0 or 0.1.0-alpha.1, ordered the SemVer way: 0.1.0-alpha.1 &lt; 0.1.0-alpha.2 &lt; 0.1.0-beta &lt; 0.1.0.
/// Build metadata after '+' is ignored.
/// </summary>
public sealed record AppVersion(Version Number, string Label) : IComparable<AppVersion>
{
    public bool IsPrerelease => Label.Length > 0;

    public override string ToString() => IsPrerelease ? $"{Number}-{Label}" : Number.ToString();

    /// <summary>The running version: the informational version from Directory.Build.props or the release tag.</summary>
    public static AppVersion Current { get; } = FromAssembly(typeof(AppVersion).Assembly);

    private static AppVersion FromAssembly(Assembly a) =>
        TryParse(a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "", out var v) ? v
        : new AppVersion(Normalize(a.GetName().Version ?? new Version(0, 0)), "");

    /// <summary>"v1.2", "1.2.3", "v0.1.0-alpha.1+abc" → 1.2.0 / 1.2.3 / 0.1.0-alpha.1.</summary>
    public static bool TryParse(string text, out AppVersion version)
    {
        version = new AppVersion(new Version(0, 0, 0), "");
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var label = dash >= 0 ? s[(dash + 1)..] : "";
        var number = dash >= 0 ? s[..dash] : s;
        if (!number.Contains('.')) number += ".0";
        if (!Version.TryParse(number, out var n) || label.Contains(' ')) return false;
        version = new AppVersion(Normalize(n), label);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Number.CompareTo(other.Number);
        if (c != 0) return c;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;

        // Dot-separated identifiers: numbers compare numerically and sort before words; a shorter list sorts first.
        var a = Label.Split('.');
        var b = other.Label.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = long.TryParse(a[i], out var ai);
            var bn = long.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
