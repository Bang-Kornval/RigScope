using System.Text.Json.Nodes;
using RigScope.Core.Games;
using RigScope.Core.Rules;

namespace RigScope.Core.Tests;

public class SnapshotQueryTests
{
    private static readonly JsonNode Root = SnapshotQuery.Parse("""
        {
          "gpu": { "vendor": "nvidia", "driverAgeDays": 11, "rebar": false },
          "apps": [
            { "name": "G HUB", "category": "rgb", "running": true },
            { "name": "iCUE", "category": "rgb", "running": false },
            { "name": "Docker", "category": "vm", "running": true }
          ],
          "cs2": { "primary": { "video": { "msaa_samples": "4" }, "fpsMax": null } },
          "display": { "refreshHz": 120, "maxRefreshHz": 144 },
          "bytes": 13675053056
        }
        """);

    [Theory]
    [InlineData("""{ "path": "gpu.vendor", "eq": "nvidia" }""", true)]
    [InlineData("""{ "path": "gpu.vendor", "eq": "NVIDIA" }""", true)]
    [InlineData("""{ "path": "gpu.vendor", "in": ["amd", "intel"] }""", false)]
    [InlineData("""{ "path": "gpu.vendor", "notIn": ["amd", "intel"] }""", true)]
    [InlineData("""{ "path": "gpu.driverAgeDays", "gt": 10, "lt": 12 }""", true)]
    [InlineData("""{ "path": "gpu.driverAgeDays", "gte": 12 }""", false)]
    [InlineData("""{ "path": "gpu.rebar", "eq": false }""", true)]
    [InlineData("""{ "path": "cs2.primary.video.msaa_samples", "eq": 4 }""", true)]
    [InlineData("""{ "path": "cs2.primary.video.msaa_samples", "gte": "2" }""", true)]
    [InlineData("""{ "path": "cs2.primary.fpsMax", "exists": false }""", true)]
    [InlineData("""{ "path": "missing.thing", "ne": "x" }""", true)]
    [InlineData("""{ "path": "missing.thing", "eq": "x" }""", false)]
    [InlineData("""{ "path": "apps[category=rgb]", "countEq": 2 }""", true)]
    [InlineData("""{ "path": "apps[category=rgb,running=true]", "countGte": 2 }""", false)]
    [InlineData("""{ "path": "apps[*].name", "matches": "^doc" }""", true)]
    [InlineData("""{ "path": "apps[2].category", "eq": "vm" }""", true)]
    [InlineData("""{ "path": "apps[9].category", "exists": true }""", false)]
    [InlineData("""{ "path": "display.refreshHz", "lt": "{display.maxRefreshHz}" }""", true)]
    [InlineData("""{ "all": [ { "path": "gpu.vendor", "eq": "nvidia" }, { "not": { "path": "gpu.rebar", "eq": true } } ] }""", true)]
    [InlineData("""{ "any": [ { "path": "gpu.vendor", "eq": "amd" }, { "path": "apps[category=vm]", "countGte": 1 } ] }""", true)]
    public void Conditions(string condition, bool expected) =>
        Assert.Equal(expected, SnapshotQuery.Evaluate(SnapshotQuery.Parse(condition), Root));

    [Fact]
    public void Null_condition_is_true() => Assert.True(SnapshotQuery.Evaluate(null, Root));

    [Fact]
    public void Condition_without_path_is_rejected() =>
        Assert.Throws<FormatException>(() => SnapshotQuery.Evaluate(SnapshotQuery.Parse("""{ "eq": 1 }"""), Root));

    [Theory]
    [InlineData("{gpu.vendor|upper}", "NVIDIA")]
    [InlineData("{apps[category=rgb].name|join}", "G HUB, iCUE")]
    [InlineData("{apps[category=rgb]|count}", "2")]
    [InlineData("{bytes|gb}", "12.7 GB")]
    [InlineData("{gpu.driverAgeDays|days}", "11 days")]
    [InlineData("{gpu.rebar|bool:On/Off}", "Off")]
    [InlineData("{cs2.primary.video.msaa_samples|map:0=None;4=4x MSAA}", "4x MSAA")]
    [InlineData("{cs2.primary.fpsMax|map:0=Uncapped|default:Game default}", "Game default")]
    [InlineData("{nothing.here}", "?")]
    [InlineData("{missing|default:{gpu.vendor}}", "nvidia")]
    [InlineData("plain text", "plain text")]
    public void Templates(string template, string expected) => Assert.Equal(expected, SnapshotQuery.Render(template, Root));
}

public class AdvicePackTests
{
    private static readonly AdvicePack Pack = AdvicePack.LoadBuiltIn();

    [Fact]
    public void Built_in_pack_loads_with_rules() => Assert.True(Pack.Rules.Count >= 40, $"only {Pack.Rules.Count} rules");

    [Fact]
    public void Rule_ids_are_unique() =>
        Assert.Empty(Pack.Rules.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key));

    [Fact]
    public void Every_rule_is_complete()
    {
        foreach (var r in Pack.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title), $"{r.Id}: title");
            Assert.False(string.IsNullOrWhiteSpace(r.Recommendation), $"{r.Id}: recommendation");
            Assert.False(string.IsNullOrWhiteSpace(r.Why), $"{r.Id}: why");
            Assert.NotEmpty(r.Outcomes);
            Assert.All(r.Outcomes, o => Assert.False(string.IsNullOrWhiteSpace(o.Summary), $"{r.Id}: outcome summary"));
            Assert.All(r.Sources, s => Assert.StartsWith("https://", s.Url));
            Assert.All(r.Games, g => Assert.NotNull(GameCatalogue.ById(g)));
        }
    }

    [Fact]
    public void Only_the_last_outcome_may_be_a_fallback()
    {
        foreach (var r in Pack.Rules)
            Assert.All(r.Outcomes.SkipLast(1), o => Assert.True(o.When is not null, $"{r.Id}: a fallback outcome hides the ones after it"));
    }

    [Fact]
    public void Myths_use_myth_status_and_settings_dont()
    {
        foreach (var r in Pack.Rules)
        {
            if (r.Kind == AdviceKind.Myth) Assert.All(r.Outcomes, o => Assert.Equal(FindingStatus.Myth, o.Status));
            else Assert.All(r.Outcomes, o => Assert.NotEqual(FindingStatus.Myth, o.Status));
        }
    }

    [Fact]
    public void Steps_dont_tell_people_to_run_tweak_scripts()
    {
        foreach (var r in Pack.Rules.Where(r => r.Kind != AdviceKind.Myth))
            Assert.All(r.Steps, s => Assert.DoesNotContain("regedit", s, StringComparison.OrdinalIgnoreCase));
    }
}

public class RuleScenarioTests
{
    private static readonly RuleEngine Engine = new(AdvicePack.LoadBuiltIn());

    private static Finding? Find(List<Finding> findings, string id) => findings.FirstOrDefault(f => f.Rule.Id == id);
    private static FindingStatus? Status(List<Finding> findings, string id) => Find(findings, id)?.Status;

    public static IEnumerable<object?[]> PcsAndGames()
    {
        foreach (var (name, _) in FakePcs.Named())
        {
            yield return [name, null];
            foreach (var g in GameCatalogue.Competitive) yield return [name, g.Id];
        }
    }

    [Theory]
    [MemberData(nameof(PcsAndGames))]
    public void Every_rule_evaluates_cleanly_on_every_pc_and_game(string pc, string? game)
    {
        var findings = Engine.Evaluate(FakePcs.ByName(pc), game);
        Assert.DoesNotContain(findings, f => f.Status == FindingStatus.Unknown);
        foreach (var f in findings)
        {
            Assert.DoesNotContain("{", f.Summary + f.Current + f.Title + f.Recommendation);
            Assert.DoesNotContain("?", f.Summary.Replace("?)", ""));
        }
    }

    [Fact]
    public void Intel_nvidia_desktop()
    {
        var f = Engine.Evaluate(FakePcs.IntelNvidiaDesktop(), "cs2");
        Assert.Equal(FindingStatus.Suggest, Status(f, "display.refresh-secondary"));
        Assert.Equal(FindingStatus.Good, Status(f, "display.refresh-primary"));
        // G HUB isn't needed (the mouse keeps its settings onboard); GG is, because Sonar is set up.
        Assert.Equal(FindingStatus.Suggest, Status(f, "peripherals.suites-not-needed"));
        Assert.Contains("Logitech G HUB", Find(f, "peripherals.suites-not-needed")!.Summary);
        Assert.DoesNotContain("SteelSeries", Find(f, "peripherals.suites-not-needed")!.Summary);
        Assert.Equal(FindingStatus.Good, Status(f, "peripherals.suites-needed"));
        Assert.Contains("SteelSeries GG", Find(f, "peripherals.suites-needed")!.Summary);
        Assert.Contains("Sonar", Find(f, "peripherals.suites-needed")!.Current);
        Assert.Null(Find(f, "apps.peripheral-suites")); // the device-aware rules replace the general one
        Assert.Equal(FindingStatus.Suggest, Status(f, "apps.virtual-machines"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "firmware.bios-age"));
        Assert.Equal(FindingStatus.Good, Status(f, "firmware.xmp"));
        Assert.Equal(FindingStatus.Good, Status(f, "network.intel-i225"));
        Assert.Equal(FindingStatus.Good, Status(f, "cs2.reflex"));
        Assert.Equal(FindingStatus.Good, Status(f, "cs2.refresh-rate"));
        Assert.Equal(FindingStatus.Info, Status(f, "cs2.launch-options"));
        Assert.Equal(FindingStatus.Info, Status(f, "firmware.intel-gear-mode"));
        Assert.Contains("Uncapped", Find(f, "cs2.fps-cap")!.Current);
        Assert.Contains("launch options", Find(f, "cs2.fps-cap")!.Current);
        // AMD/laptop/hybrid-only rules stay hidden.
        Assert.Null(Find(f, "cs2.anti-lag-2"));
        Assert.Null(Find(f, "firmware.amd-x3d-dual-ccd"));
        Assert.Null(Find(f, "firmware.intel-hybrid"));
        Assert.Null(Find(f, "firmware.laptop-power"));
        Assert.Null(Find(f, "gpu.pcie-width"));
        // Rules for other games don't appear in a CS2 check.
        Assert.Null(Find(f, "game.reflex"));
    }

    [Fact]
    public void Amd_x3d_radeon_with_problems()
    {
        var f = Engine.Evaluate(FakePcs.AmdX3dRadeon(), "cs2");
        Assert.Equal(FindingStatus.Warn, Status(f, "display.refresh-primary"));
        Assert.Equal(FindingStatus.Warn, Status(f, "firmware.xmp"));
        Assert.Equal(FindingStatus.Info, Status(f, "firmware.amd-x3d-dual-ccd"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "windows.game-mode"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "windows.background-recording"));
        Assert.Equal(FindingStatus.Warn, Status(f, "windows.power-plan"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "apps.recorders"));
        Assert.Equal(FindingStatus.Info, Status(f, "apps.overlays"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "network.eee"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "gpu.driver-age"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "storage.game-on-hdd"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "storage.game-drive-free"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.vsync"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.display-mode"));
        Assert.Equal(FindingStatus.Warn, Status(f, "cs2.refresh-rate"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.dynamic-shadows"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.anti-aliasing"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.fsr"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.boost-player-contrast"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.net-buffer"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "cs2.launch-options"));
        Assert.Equal(FindingStatus.Info, Status(f, "cs2.anti-lag-2"));
        Assert.Equal(FindingStatus.Info, Status(f, "windows.memory-integrity"));
        // NVIDIA-only rules stay hidden on a Radeon.
        Assert.Null(Find(f, "cs2.reflex"));
        Assert.Null(Find(f, "gpu.nvidia-power-mode"));
        Assert.Null(Find(f, "gpu.nvidia-low-latency-mode"));
    }

    [Fact]
    public void Hybrid_laptop_on_battery_with_valorant()
    {
        var f = Engine.Evaluate(FakePcs.IntelHybridLaptopOnBattery(), "valorant");
        Assert.Equal(FindingStatus.Warn, Status(f, "firmware.laptop-power"));
        Assert.Equal(FindingStatus.Warn, Status(f, "firmware.single-channel"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "firmware.intel-hybrid"));
        Assert.Equal(FindingStatus.Suggest, Status(f, "network.wired"));
        Assert.Equal(FindingStatus.Warn, Status(f, "gpu.pcie-width"));
        Assert.Equal(FindingStatus.Info, Status(f, "game.reflex"));
        Assert.Contains("VALORANT", Find(f, "game.reflex")!.Title);
        Assert.Contains("VALORANT", Find(f, "game.reflex")!.Steps[0]);
        // The laptop rules beat desktop-only ones.
        Assert.Null(Find(f, "storage.hiberfil"));
        Assert.Null(Find(f, "cs2.reflex"));
    }

    [Fact]
    public void Unknown_corsair_keyboard_makes_icue_a_check_not_a_verdict()
    {
        var f = Engine.Evaluate(FakePcs.AmdX3dRadeon(), null);
        Assert.Equal(FindingStatus.Info, Status(f, "peripherals.suites-not-needed"));
        Assert.Contains("Corsair iCUE", Find(f, "peripherals.suites-not-needed")!.Summary);
        Assert.Null(Find(f, "peripherals.suites-needed"));
        Assert.Null(Find(f, "apps.peripheral-suites"));
    }

    [Fact]
    public void No_suites_no_peripheral_findings()
    {
        foreach (var pc in new[] { FakePcs.IntelHybridLaptopOnBattery(), FakePcs.WifiNoGames(), new Core.Snapshot.SystemSnapshot() })
        {
            var f = Engine.Evaluate(pc, null);
            Assert.Null(Find(f, "peripherals.suites-not-needed"));
            Assert.Null(Find(f, "peripherals.suites-needed"));
            Assert.Null(Find(f, "apps.peripheral-suites"));
        }
    }

    [Fact]
    public void Without_a_device_list_the_general_suite_rule_still_applies()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        pc.Peripherals.Clear();
        pc.Suites.Clear(); // what the suites probe leaves when the device list couldn't be read
        var f = Engine.Evaluate(pc, null);
        Assert.Equal(FindingStatus.Suggest, Status(f, "apps.peripheral-suites"));
        Assert.Null(Find(f, "peripherals.suites-not-needed"));
    }

    [Fact]
    public void Every_suite_needed_is_good_not_suggest()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        pc.Suites.RemoveAll(s => s.Id == "ghub");
        var f = Engine.Evaluate(pc, null);
        Assert.Equal(FindingStatus.Good, Status(f, "peripherals.suites-not-needed"));
        Assert.Equal(FindingStatus.Good, Status(f, "peripherals.suites-needed"));
    }

    [Fact]
    public void Main_gpu_is_the_discrete_one_on_a_laptop() =>
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", FakePcs.IntelHybridLaptopOnBattery().Gpu!.Name);

    [Fact]
    public void Sparse_pc_still_gets_useful_general_findings()
    {
        var f = Engine.Evaluate(FakePcs.WifiNoGames(), null);
        Assert.Equal(FindingStatus.Suggest, Status(f, "network.wired"));
        Assert.Null(Find(f, "gpu.driver-age"));       // driver date unknown → rule skipped, not guessed
        Assert.Null(Find(f, "display.refresh-primary"));
        Assert.Contains(f, x => x.Status == FindingStatus.Myth);
    }

    [Fact]
    public void Findings_are_ranked_problems_first()
    {
        var f = Engine.Evaluate(FakePcs.AmdX3dRadeon(), "cs2");
        Assert.Equal(FindingStatus.Warn, f[0].Status);
        Assert.Equal(FindingStatus.Myth, f[^1].Status);
    }

    [Fact]
    public void A_broken_rule_is_reported_not_thrown()
    {
        var pack = new AdvicePack
        {
            Rules = [new AdviceRule { Id = "bad", Title = "Bad", AppliesWhen = SnapshotQuery.Parse("""{ "eq": 1 }"""), Outcomes = [new AdviceOutcome { Summary = "x" }] }],
        };
        var f = new RuleEngine(pack).Evaluate(FakePcs.WifiNoGames(), null);
        Assert.Equal(FindingStatus.Unknown, f.Single().Status);
    }
}
