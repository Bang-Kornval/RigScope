using RigScope.Core.Games;
using RigScope.Core.Parsing;
using RigScope.Core.Probes;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parses_nested_blocks_comments_escapes_and_conditionals()
    {
        var root = VdfParser.Parse("""
            // header comment
            "libraryfolders"
            {
                "0"
                {
                    "path"    "C:\\Program Files (x86)\\Steam"   // trailing comment
                    "label"   "say \"hi\""
                }
                "1" { "path" "D:\\SteamLibrary" }
                "flag" "1" [$WIN32]
            }
            """);

        var folders = root.Child("libraryfolders")!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.ChildPath("0")!.Value("path"));
        Assert.Equal("say \"hi\"", folders.Child("0")!.Value("label"));
        Assert.Equal(@"D:\SteamLibrary", folders.Child("1")!.Value("PATH")); // keys are case-insensitive
        Assert.Equal("1", folders.Value("flag"));
    }

    [Fact]
    public void Last_duplicate_key_wins_and_unquoted_tokens_work()
    {
        var root = VdfParser.Parse("a 1\na 2\nblock { inner yes }");
        Assert.Equal("2", root.Value("a"));
        Assert.Equal("yes", root.Child("block")!.Value("inner"));
    }

    [Fact]
    public void Unbalanced_input_does_not_throw()
    {
        var root = VdfParser.Parse("\"a\" { \"b\" \"c\" } } } \"dangling\"");
        Assert.Equal("c", root.Child("a")!.Value("b"));
    }

    [Fact]
    public void Steam_app_manifest_gives_name_size_and_last_played()
    {
        var acf = VdfParser.Parse("""
            "AppState"
            {
                "appid"       "730"
                "name"        "Counter-Strike 2"
                "installdir"  "Counter-Strike Global Offensive"
                "SizeOnDisk"  "73985335051"
                "LastPlayed"  "1793212800"
            }
            """);
        var app = SteamLocator.ParseManifest(acf, @"D:\SteamLibrary\steamapps")!;
        Assert.Equal(730, app.AppId);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", app.InstallPath);
        Assert.Equal(73985335051, app.SizeOnDisk);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793212800).LocalDateTime, app.LastPlayed);
    }

    [Fact]
    public void Manifest_without_last_played_has_null_date()
    {
        var acf = VdfParser.Parse("\"AppState\" { \"appid\" \"10\" \"name\" \"X\" \"installdir\" \"X\" \"LastPlayed\" \"0\" }");
        Assert.Null(SteamLocator.ParseManifest(acf, @"C:\s")!.LastPlayed);
    }
}

public class HardwareParsingTests
{
    [Theory]
    [InlineData("32.0.16.2101", "621.01")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("bad", "")]
    [InlineData(null, "")]
    public void Nvidia_driver_version_from_windows_version(string? windows, string expected) =>
        Assert.Equal(expected, HardwareParsing.NvidiaVersionFromWindows(windows));

    [Theory]
    [InlineData("CM4X16GC3000C16K4D", 3000)]
    [InlineData("CMK32GX5M2B6000C36", 6000)]
    [InlineData("F4-3600C16D-32GTZN", 3600)]
    [InlineData("F5-6000J3038F16G", 6000)]
    [InlineData("KF436C16BB/16", 3600)]
    [InlineData("KF560C36-16", 6000)]
    [InlineData("M378A1K43CB2-CTD", 0)]
    [InlineData("", 0)]
    public void Rated_speed_from_ram_part_number(string part, int expected) =>
        Assert.Equal(expected, HardwareParsing.RatedMtsFromPartNumber(part));

    [Fact]
    public void Resizable_bar_off_when_bar1_is_256_mib()
    {
        const string output = """
            FB Memory Usage
                Total                             : 8192 MiB
            BAR1 Memory Usage
                Total                             : 256 MiB
                Used                              : 227 MiB
            """;
        Assert.False(HardwareParsing.ResizableBarFromNvidiaSmiMemory(output));
    }

    [Fact]
    public void Resizable_bar_on_when_bar1_matches_vram() =>
        Assert.True(HardwareParsing.ResizableBarFromNvidiaSmiMemory("BAR1 Memory Usage\n    Total : 16384 MiB\n"));

    [Fact]
    public void Resizable_bar_unknown_without_bar1() =>
        Assert.Null(HardwareParsing.ResizableBarFromNvidiaSmiMemory("FB Memory Usage\n Total : 8192 MiB"));

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_15F3&SUBSYS_87D21043&REV_03", 3)]
    [InlineData(@"PCI\VEN_8086&DEV_15F3&REV_0A", 10)]
    [InlineData("USB\\VID_1234", null)]
    public void Pci_revision(string id, int? expected) => Assert.Equal(expected, HardwareParsing.PciRevision(id));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", HardwareVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7800 XT", HardwareVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", HardwareVendor.Intel)]
    [InlineData("GenuineIntel", HardwareVendor.Intel)]
    [InlineData("AuthenticAMD", HardwareVendor.Amd)]
    [InlineData("", HardwareVendor.Unknown)]
    public void Vendor_from_name(string name, HardwareVendor expected) => Assert.Equal(expected, HardwareParsing.VendorFromName(name));

    [Fact]
    public void Cim_date_parses_and_rejects_garbage()
    {
        Assert.Equal(new DateTime(2022, 12, 26), HardwareParsing.ParseCimDate("20221226000000.000000+000"));
        Assert.Null(HardwareParsing.ParseCimDate("2022"));
        Assert.Null(HardwareParsing.ParseCimDate(null));
    }

    [Fact]
    public void Nvidia_smi_csv_maps_headers_to_cells()
    {
        var rows = HardwareParsing.ParseNvidiaSmiCsv("name, driver_version\nNVIDIA GeForce RTX 4070, 621.01\n");
        Assert.Equal("621.01", rows.Single()["driver_version"]);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", HardwareVendor.Intel, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", HardwareVendor.Intel, false)]
    [InlineData("AMD Radeon(TM) Graphics", HardwareVendor.Amd, true)]
    [InlineData("AMD Radeon RX 6700 XT", HardwareVendor.Amd, false)]
    [InlineData("NVIDIA GeForce RTX 4090", HardwareVendor.Nvidia, false)]
    public void Integrated_gpu_detection(string name, HardwareVendor vendor, bool integrated) =>
        Assert.Equal(integrated, GpuProbe.IsIntegrated(new GpuInfo { Name = name, Vendor = vendor }));

    [Fact]
    public void Monitor_pnp_code_from_device_id() =>
        Assert.Equal("AUS2701", DisplayProbe.PnpCode(@"MONITOR\AUS2701\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001"));
}

public class Cs2ConfigTests
{
    [Fact]
    public void Video_config_drops_setting_prefix()
    {
        var video = Cs2Config.ParseVideo(VdfParser.Parse("""
            "video.cfg" { "Version" "16" "setting.msaa_samples" "4" "setting.r_low_latency" "1" }
            """));
        Assert.Equal("4", video["msaa_samples"]);
        Assert.Equal("1", video["r_low_latency"]);
        Assert.Equal("16", video["Version"]);
    }

    [Fact]
    public void Convars_are_found_at_any_depth()
    {
        var convars = Cs2Config.ParseConvars(VdfParser.Parse("""
            "config" { "convars" { "fps_max" "0" "cl_net_buffer_ticks" "1" } }
            """));
        Assert.Equal("0", convars["fps_max"]);
        Assert.Equal("1", convars["cl_net_buffer_ticks"]);
    }

    [Fact]
    public void Launch_options_from_localconfig()
    {
        var lc = VdfParser.Parse("""
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "730" { "LaunchOptions" "+fps_max 0 -nojoy" } } } } } }
            """);
        Assert.Equal("+fps_max 0 -nojoy", Cs2Config.LaunchOptions(lc));
        Assert.Equal("", Cs2Config.LaunchOptions(null));
    }

    [Fact]
    public void Autoexec_keeps_convars_and_skips_binds()
    {
        var cvars = Cs2Config.ParseAutoexec("""
            echo "hi"
            bind mwheelup +jump
            fps_max "0" // comment
            cl_radar_scale 0.5; cl_hud_color 6
            +jumpthrow
            """);
        Assert.Equal("0", cvars["fps_max"]);
        Assert.Equal("0.5", cvars["cl_radar_scale"]);
        Assert.Equal("6", cvars["cl_hud_color"]);
        Assert.False(cvars.ContainsKey("bind"));
        Assert.False(cvars.ContainsKey("echo"));
    }

    [Fact]
    public void Effective_fps_max_prefers_launch_options_then_autoexec_then_convars()
    {
        var a = new Cs2Account { Convars = new() { ["fps_max"] = "400" }, Autoexec = new() { ["fps_max"] = "300" }, LaunchOptions = "-novid +fps_max \"240\"" };
        Assert.Equal("240", a.FpsMax);
        Assert.Equal("launch options", a.FpsMaxSource);
        a.LaunchOptions = "";
        Assert.Equal("300", a.FpsMax);
        Assert.Equal("autoexec.cfg", a.FpsMaxSource);
        a.Autoexec.Clear();
        Assert.Equal("400", a.FpsMax);
        a.Convars.Clear();
        Assert.Null(a.FpsMax);
        Assert.Equal("game default", a.FpsMaxSource);
    }

    [Fact]
    public void Configured_refresh_rate_rounds_fraction()
    {
        var a = new Cs2Account { Video = new() { ["refreshrate_numerator"] = "164999", ["refreshrate_denominator"] = "1000" } };
        Assert.Equal(165, a.ConfiguredRefreshHz);
        Assert.Null(new Cs2Account().ConfiguredRefreshHz);
    }
}

public class SnapshotJsonTests
{
    [Fact]
    public void Round_trips_and_includes_computed_values()
    {
        var pc = FakePcs.IntelNvidiaDesktop();
        var json = SnapshotJson.Serialize(pc);
        var back = SnapshotJson.Deserialize(json);
        Assert.Equal(pc.Cpu.Name, back.Cpu.Name);
        Assert.Equal(pc.Games.Count, back.Games.Count);
        Assert.Equal("4", back.Cs2!.Primary!.Video["msaa_samples"]);
        Assert.Contains("\"vendor\": \"nvidia\"", json);
        Assert.Contains("\"freePercent\"", json);
        Assert.Contains("\"fpsMax\": \"0\"", json);
    }

    [Fact]
    public void Path_helpers_handle_nesting_and_slashes()
    {
        Assert.True(GamesProbe.SameOrNested(@"C:\Riot Games\Teamfight Tactics\Live", "C:/Riot Games/Teamfight Tactics/"));
        Assert.False(GamesProbe.SameOrNested(@"C:\Games\A", @"C:\Games\AB"));
    }
}
