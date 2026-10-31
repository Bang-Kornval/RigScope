using RigScope.Core.Updates;

namespace RigScope.Core.Tests;

public class UpdateTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("v0.3.0-beta.1", "0.3.0-beta.1")]
    [InlineData("V1.0.0+abc123", "1.0.0")]
    [InlineData("0.1.0-alpha.1+5f3e2a", "0.1.0-alpha.1")]
    public void Parses_release_tags(string tag, string expected) => Assert.Equal(expected, V(tag).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void Rejects_tags_that_are_not_versions(string tag) => Assert.False(AppVersion.TryParse(tag, out _));

    [Theory]
    [InlineData("v0.10.0", "v0.9.5")]
    [InlineData("v0.1.0", "v0.1.0-beta")]
    [InlineData("v0.1.0-beta", "v0.1.0-alpha.2")]
    [InlineData("v0.1.0-alpha.10", "v0.1.0-alpha.2")]
    [InlineData("v0.1.0-alpha.1", "v0.1.0-alpha")]
    [InlineData("v0.2.0-alpha.1", "v0.1.0")]
    public void Newer_release_compares_greater_than_older(string newer, string older)
    {
        Assert.True(V(newer) > V(older));
        Assert.True(V(older) < V(newer));
    }

    [Fact]
    public void Same_version_with_and_without_v_is_equal() => Assert.Equal(V("v0.1.0-alpha.1"), V("0.1.0-alpha.1"));

    private const string ReleaseList = """
    [
      { "tag_name": "v0.3.0", "draft": true, "prerelease": false },
      { "tag_name": "v0.2.0-beta.1", "prerelease": true },
      { "tag_name": "v0.1.1", "prerelease": false },
      { "tag_name": "v0.1.0", "prerelease": false },
      { "tag_name": "nightly", "prerelease": true }
    ]
    """;

    [Fact]
    public void Stable_users_get_the_newest_stable_release_and_never_drafts() =>
        Assert.Equal("0.1.1", UpdateChecker.PickLatest(ReleaseList, includePrereleases: false)?.Version.ToString());

    [Fact]
    public void Beta_testers_also_get_prereleases() =>
        Assert.Equal("0.2.0-beta.1", UpdateChecker.PickLatest(ReleaseList, includePrereleases: true)?.Version.ToString());

    [Fact]
    public void Release_flagged_prerelease_on_github_counts_as_prerelease_even_without_a_suffix()
    {
        var r = UpdateChecker.PickLatest("""[ { "tag_name": "v0.4.0", "prerelease": true } ]""", includePrereleases: false);
        Assert.Null(r);
    }

    [Fact]
    public void Empty_release_list_has_no_latest() => Assert.Null(UpdateChecker.PickLatest("[]", includePrereleases: true));

    [Fact]
    public void Reads_release_json_and_picks_exe_and_checksum()
    {
        const string json = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/Bang-Kornval/RigScope/releases/tag/v0.2.0",
          "published_at": "2026-11-05T12:00:00Z",
          "body": "### New\n- Faster storage scan",
          "assets": [
            { "name": "RigScope.exe.sha256", "browser_download_url": "https://github.com/x/RigScope.exe.sha256", "size": 80 },
            { "name": "RigScope.exe", "browser_download_url": "https://github.com/x/RigScope.exe", "size": 65000000 },
            { "name": "other.zip", "browser_download_url": "https://github.com/x/other.zip", "size": 1 }
          ]
        }
        """;
        var r = UpdateChecker.ParseRelease(json);
        Assert.NotNull(r);
        Assert.Equal(V("0.2.0"), r.Version);
        Assert.False(r.IsPrerelease);
        Assert.Equal("https://github.com/x/RigScope.exe", r.ExeUrl);
        Assert.Equal(65000000, r.ExeSize);
        Assert.Equal("https://github.com/x/RigScope.exe.sha256", r.ChecksumUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), r.PublishedAt);
    }

    [Fact]
    public void Release_without_assets_or_with_non_https_links_has_no_download()
    {
        var r = UpdateChecker.ParseRelease("""{ "tag_name": "v1.0.0", "assets": [ { "name": "RigScope.exe", "browser_download_url": "http://insecure/RigScope.exe" } ] }""");
        Assert.NotNull(r);
        Assert.Null(r.ExeUrl);
        Assert.Null(r.ChecksumUrl);
        Assert.Null(UpdateChecker.ParseRelease("""{ "tag_name": "nightly" }"""));
    }

    [Fact]
    public void Reads_checksum_file()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash.ToUpperInvariant(), UpdateChecker.ParseChecksum($"{hash}  RigScope.exe\r\n"));
        Assert.Null(UpdateChecker.ParseChecksum("not a hash"));
    }

    [Fact]
    public void Parses_changelog_versions_groups_and_bullets()
    {
        const string md = """
        # Changelog
        Intro text that isn't a release.
        - not a release bullet

        ## [Unreleased]
        - Work in progress

        ## [0.2.0] - 2026-11-05
        ### New
        - **Faster** storage scan with `--notes`
          that continues here
        - See [the docs](https://example.com)
        ### Fixed
        - Crash on empty drives

        ## 0.1.0
        - First release
        """;
        var entries = Changelog.Parse(md);
        Assert.Equal(["Unreleased", "0.2.0", "0.1.0"], entries.Select(e => e.Version));
        Assert.True(entries[0].IsUnreleased);

        var v2 = entries[1];
        Assert.Equal(new DateOnly(2026, 11, 5), v2.Date);
        Assert.Equal(["New", "Fixed"], v2.Sections.Select(s => s.Title));
        Assert.Equal("Faster storage scan with --notes that continues here", v2.Sections[0].Items[0]);
        Assert.Equal("See the docs", v2.Sections[0].Items[1]);

        Assert.Null(entries[2].Date);
        Assert.Equal("", entries[2].Sections.Single().Title);
        Assert.Equal("First release", entries[2].Sections.Single().Items.Single());
    }

    [Fact]
    public void Built_in_changelog_has_an_entry_for_this_version()
    {
        // The release workflow copies this section into the GitHub release notes, and Settings marks it "This version".
        var versions = Changelog.LoadBuiltIn()
            .Select(e => AppVersion.TryParse(e.Version, out var v) ? v : null);
        Assert.Contains(UpdateChecker.CurrentVersion, versions);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigscope-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults_with_startup_check_on() =>
        Assert.True(new RigScope.Core.Settings.SettingsStore(FilePath).Load().CheckForUpdatesOnStartup);

    [Fact]
    public void Saved_choice_is_loaded_back()
    {
        var store = new RigScope.Core.Settings.SettingsStore(FilePath);
        Assert.True(store.Save(new() { CheckForUpdatesOnStartup = false }));
        Assert.False(new RigScope.Core.Settings.SettingsStore(FilePath).Load().CheckForUpdatesOnStartup);
    }

    [Fact]
    public void Broken_file_gives_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        Assert.True(new RigScope.Core.Settings.SettingsStore(FilePath).Load().CheckForUpdatesOnStartup);
    }
}

public class EarlyVersionsTests
{
    [Theory]
    [InlineData(null, false, false)] // stable build, never chose: stable only
    [InlineData(null, true, true)]   // running an alpha/beta, never chose: keep getting them
    [InlineData(true, false, true)]  // opted in
    [InlineData(false, true, false)] // opted out while on a beta
    public void Early_versions_follow_the_users_choice_then_the_running_build(bool? choice, bool runningPrerelease, bool expected) =>
        Assert.Equal(expected, new RigScope.Core.Settings.UserSettings { EarlyVersions = choice }.WantsEarlyVersions(runningPrerelease));
}
