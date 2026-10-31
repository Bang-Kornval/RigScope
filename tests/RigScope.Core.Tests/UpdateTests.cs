using RigScope.Core.Updates;

namespace RigScope.Core.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("v0.3.0-beta.1", "0.3.0")]
    [InlineData("V1.0.0+abc123", "1.0.0")]
    public void Parses_release_tags(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void Rejects_tags_that_are_not_versions(string tag) => Assert.False(UpdateChecker.TryParseVersion(tag, out _));

    [Fact]
    public void Newer_release_compares_greater_than_older()
    {
        UpdateChecker.TryParseVersion("v0.10.0", out var newer);
        UpdateChecker.TryParseVersion("v0.9.5", out var older);
        Assert.True(newer > older);
    }

    [Fact]
    public void Reads_release_json_and_picks_exe_and_checksum()
    {
        const string json = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/Kornvalles/RigScope/releases/tag/v0.2.0",
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
        Assert.Equal(new Version(0, 2, 0), r.Version);
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
            .Select(e => UpdateChecker.TryParseVersion(e.Version, out var v) ? v : null);
        Assert.Contains(UpdateChecker.CurrentVersion, versions);
    }
}
