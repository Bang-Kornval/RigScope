using RigScope.Core.Report;
using RigScope.Core.Rules;
using RigScope.Core.Storage;

namespace RigScope.Core.Tests;

public class ReportTests
{
    private static readonly RuleEngine Engine = new(AdvicePack.LoadBuiltIn());

    [Fact]
    public void Report_lists_hardware_and_problems_first()
    {
        var pc = FakePcs.AmdX3dRadeon();
        var md = ReportBuilder.Build(pc, Engine.Evaluate(pc, "cs2"), "cs2");
        Assert.Contains("RigScope report — Counter-Strike 2", md);
        Assert.Contains("AMD Ryzen 9 7950X3D", md);
        Assert.True(md.IndexOf("Worth doing", StringComparison.Ordinal) < md.IndexOf("Already good", StringComparison.Ordinal));
        Assert.Contains("⚠ Main monitor refresh rate", md);
        Assert.DoesNotContain("Skip these", md); // myths are off by default
    }

    [Fact]
    public void Options_add_myths_and_drop_good()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        var md = ReportBuilder.Build(pc, Engine.Evaluate(pc, "cs2"), "cs2", options: new ReportOptions(IncludeGood: false, IncludeMyths: true));
        Assert.Contains("Skip these (myths)", md);
        Assert.DoesNotContain("Already good", md);
    }

    [Fact]
    public void Storage_section_skips_leave_alone_items()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        var notes = new List<CleanupNote>
        {
            new(CleanupTag.Safe, "Recycle Bin", 14L << 30, ""),
            new(CleanupTag.Keep, "WinSxS", 20L << 30, ""),
        };
        var md = ReportBuilder.Build(pc, [], null, notes);
        Assert.Contains("Recycle Bin (safe to clear)", md);
        Assert.DoesNotContain("WinSxS", md);
    }

    [Fact]
    public void Redaction_removes_user_paths_ids_and_emails()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        pc.Cs2!.Primary!.SteamId3 = "110657164";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = $"""
            path {profile}\Videos and C:\Users\someoneelse\AppData
            steam 110657164, nic PCI\VEN_8086&DEV_15F3&SUBSYS_87D21043&REV_03
            contact gamer@example.com on {Environment.MachineName}
            C:\Users\Public stays
            """;
        var red = ReportBuilder.Redact(raw, pc);
        Assert.DoesNotContain(profile, red, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("someoneelse", red);
        Assert.DoesNotContain("110657164", red);
        Assert.DoesNotContain("87D21043", red);
        Assert.DoesNotContain("gamer@example.com", red);
        Assert.Contains("REV_03", red);          // revision is useful and not personal
        Assert.Contains(@"C:\Users\Public", red);
        Assert.Equal(red, ReportBuilder.Redact(red, pc)); // idempotent
    }
}
