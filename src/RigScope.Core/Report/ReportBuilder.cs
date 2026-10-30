using System.Text;
using System.Text.RegularExpressions;
using RigScope.Core.Games;
using RigScope.Core.Peripherals;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;
using RigScope.Core.Storage;

namespace RigScope.Core.Report;

public sealed record ReportOptions(bool Redact = true, bool IncludeGood = true, bool IncludeMyths = false, bool IncludeStorage = true);

/// <summary>
/// Builds a Markdown report to paste into Discord, Reddit or a message. With redaction on (the default) it removes
/// the Windows user name, PC name, Steam account ids, serial-like hardware ids and full paths.
/// </summary>
public static partial class ReportBuilder
{
    public static string Build(SystemSnapshot s, IReadOnlyList<Finding> findings, string? gameId, IReadOnlyList<CleanupNote>? cleanup = null, ReportOptions? options = null)
    {
        options ??= new ReportOptions();
        var game = gameId is null ? null : GameCatalogue.ById(gameId);
        var md = new StringBuilder();

        md.AppendLine($"## RigScope report{(game is null ? "" : $" — {game.Name}")}");
        md.AppendLine($"*{s.CapturedAt:d MMM yyyy} · RigScope {s.AppVersion} · read-only check*");
        md.AppendLine();
        md.AppendLine("**PC**");
        md.AppendLine($"- CPU: {s.Cpu.Name} ({s.Cpu.Cores}C/{s.Cpu.Threads}T)");
        foreach (var g in s.Gpus) md.AppendLine($"- GPU: {g.Name}{(g.VendorDriverVersion.Length > 0 ? $", driver {g.VendorDriverVersion}" : "")}");
        if (s.Memory.TotalBytes > 0) md.AppendLine($"- RAM: {s.Memory.TotalGb} GB {s.Memory.Generation} at {s.Memory.ConfiguredMts} MT/s ({s.Memory.Modules.Count} sticks)");
        foreach (var d in s.Displays) md.AppendLine($"- Display: {d.MonitorName} {d.Width}x{d.Height} @ {d.RefreshHz} Hz{(d.IsPrimary ? " (main)" : "")}");
        md.AppendLine($"- Windows: {s.Os.ProductName} {s.Os.DisplayVersion} (build {s.Os.Build})");
        if (s.Firmware.BoardProduct.Length > 0) md.AppendLine($"- Board: {s.Firmware.BoardProduct}, BIOS {s.Firmware.BiosVersion}");
        md.AppendLine();

        Section(md, "Worth doing", findings.Where(f => f.Status is FindingStatus.Warn or FindingStatus.Suggest), detailed: true);
        Section(md, "Check yourself", findings.Where(f => f.Status is FindingStatus.Info or FindingStatus.Unknown && f.Rule.Kind != AdviceKind.Preference), detailed: false);
        if (options.IncludeGood) Section(md, "Already good", findings.Where(f => f.Status == FindingStatus.Good), detailed: false);
        if (options.IncludeMyths) Section(md, "Skip these (myths)", findings.Where(f => f.Status == FindingStatus.Myth), detailed: false);

        PeripheralSection(md, s);

        if (options.IncludeStorage && cleanup is { Count: > 0 })
        {
            md.AppendLine("**Storage: where to look first**");
            foreach (var n in cleanup.Where(n => n.Tag != CleanupTag.Keep).Take(8))
                md.AppendLine($"- {SnapshotQuery.FormatBytes(n.Bytes)} · {n.Title} ({(n.Tag == CleanupTag.Safe ? "safe to clear" : "review first")})");
            md.AppendLine();
        }

        md.AppendLine("*Made with RigScope. It only reads settings; it never changes anything.*");
        var text = md.ToString();
        return options.Redact ? Redact(text, s) : text;
    }

    /// <summary>Devices and suites, one line each. Names and vendor/product ids only: never serial numbers or instance ids.</summary>
    private static void PeripheralSection(StringBuilder md, SystemSnapshot s)
    {
        if (s.Peripherals.Count == 0 && s.Suites.Count == 0) return;
        md.AppendLine("**Peripherals**");
        foreach (var p in s.Peripherals)
        {
            var onboard = p.OnboardMemory switch { OnboardMemory.Yes => "yes", OnboardMemory.No => "no", _ => "check" };
            var via = p.ViaReceiver ? ", via receiver" : p.Bluetooth ? ", Bluetooth" : "";
            md.AppendLine($"- {p.DisplayName} ({PeripheralCatalogue.KindText(p.Kind)}{via}) · {(p.SuiteName.Length > 0 ? p.SuiteName : "no suite")} · settings on the device: {onboard}");
        }
        foreach (var x in s.Suites)
        {
            var state = (x.Running, x.StartsWithWindows) switch
            {
                (true, true) => "running, starts with Windows",
                (true, false) => "running",
                (false, true) => "starts with Windows",
                _ => "not running",
            };
            var verdict = x.NeededForAlwaysOn switch { true => "keep it running", false => "not needed all the time", null => "check" };
            md.AppendLine($"- {x.Name}: {state} → {verdict}{(x.Reasons.Count > 0 ? $" ({x.Reason})" : "")}");
        }
        md.AppendLine();
    }

    private static void Section(StringBuilder md, string heading, IEnumerable<Finding> items, bool detailed)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        md.AppendLine($"**{heading}**");
        foreach (var f in list)
        {
            md.AppendLine($"- {(f.Status == FindingStatus.Warn ? "⚠ " : "")}{f.Title}: {f.Summary}");
            if (detailed && f.Recommendation.Length > 0) md.AppendLine($"  → {f.Recommendation}");
        }
        md.AppendLine();
    }

    /// <summary>Removes personal details. Safe to call more than once.</summary>
    public static string Redact(string text, SystemSnapshot? s = null)
    {
        var user = Environment.UserName;
        var machine = Environment.MachineName;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (profile.Length > 3)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace(@"\", @"\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase); // JSON-escaped
        }
        text = UsersPath().Replace(text, @"C:\Users\<you>");
        text = UsersPathJson().Replace(text, @"C:\\Users\\<you>");
        if (user.Length >= 3) text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<you>", RegexOptions.IgnoreCase);
        if (machine.Length >= 3) text = Regex.Replace(text, $@"\b{Regex.Escape(machine)}\b", "<pc>", RegexOptions.IgnoreCase);
        // Real SteamID3s are 5+ digits; matching whole numbers only keeps other values intact.
        foreach (var a in s?.Cs2?.Accounts ?? [])
            if (a.SteamId3.Length >= 5) text = Regex.Replace(text, $@"(?<!\d){Regex.Escape(a.SteamId3)}(?!\d)", "<steam-id>");
        text = SubsysId().Replace(text, "SUBSYS_<id>");
        text = Email().Replace(text, "<email>");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z]:\\Users\\(?!<you>|Public\b|Default\b)[^\\\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPath();

    [GeneratedRegex(@"[A-Za-z]:\\\\Users\\\\(?!<you>|Public\b|Default\b)[^\\\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPathJson();

    [GeneratedRegex(@"SUBSYS_[0-9A-F]{8}", RegexOptions.IgnoreCase)]
    private static partial Regex SubsysId();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
