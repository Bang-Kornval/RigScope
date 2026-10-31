using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigScope.Core.Updates;

public sealed record ReleaseInfo(
    Version Version, string Tag, string Notes, string PageUrl, DateTimeOffset? PublishedAt,
    string? ExeUrl, long ExeSize, string? ChecksumUrl);

public enum UpdateState { UpToDate, Available, NoReleases, Failed }

public sealed record UpdateCheckResult(UpdateState State, ReleaseInfo? Latest, string Message);

public sealed record DownloadResult(bool Ok, bool Verified, string Message);

/// <summary>
/// Looks up the latest GitHub release and downloads its exe to a path the user picked. Only runs when the user asks:
/// RigScope makes no network calls on its own. It never replaces the running exe or installs anything.
/// </summary>
public static partial class UpdateChecker
{
    public const string Repository = "Kornvalles/RigScope";
    public const string ExeAssetName = "RigScope.exe";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The running version (set from Directory.Build.props, or from the tag by the release workflow).</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RigScope", CurrentVersion.ToString()));
        return http;
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateState.NoReleases, null, "No releases have been published yet.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(UpdateState.Failed, null, "GitHub is limiting requests right now. Try again in an hour, or open the releases page.");
            if (!response.IsSuccessStatusCode)
                return new(UpdateState.Failed, null, $"GitHub answered {(int)response.StatusCode}. Try again later, or open the releases page.");

            var latest = ParseRelease(await response.Content.ReadAsStringAsync(ct));
            if (latest is null)
                return new(UpdateState.Failed, null, "Couldn't read the version of the latest release. Open the releases page to check.");
            return latest.Version > CurrentVersion
                ? new(UpdateState.Available, latest, $"RigScope {latest.Version} is available. You have {CurrentVersion}.")
                : new(UpdateState.UpToDate, latest, $"You have the latest version ({CurrentVersion}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(UpdateState.Failed, null, "Couldn't reach GitHub. Check your connection and try again.");
        }
    }

    /// <summary>
    /// Downloads the release exe to <paramref name="targetPath"/> and checks it against the published SHA-256.
    /// A file that doesn't match is deleted. Progress is reported as 0..1 when the size is known.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(ReleaseInfo release, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.ExeUrl is null) return new(false, false, $"This release has no {ExeAssetName}. Open the release page instead.");
        var partPath = targetPath + ".part";
        try
        {
            string? expected = null;
            if (release.ChecksumUrl is not null)
                expected = ParseChecksum(await Http.GetStringAsync(release.ChecksumUrl, ct));

            using var response = await Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.ExeSize;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                return new(false, false, "The download didn't match the published SHA-256, so it was deleted. Try again, or download it from the release page.");
            }
            File.Move(partPath, targetPath, overwrite: true);
            return expected is null
                ? new(true, false, $"Saved. This release has no published checksum to compare against. SHA-256: {actual}")
                : new(true, true, "Saved and verified: the SHA-256 matches the one published with the release.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(partPath); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return new(false, false, e is OperationCanceledException ? "Download timed out or was cancelled. Try again." : $"Download failed: {e.Message}");
        }
    }

    /// <summary>Reads the fields RigScope needs from a GitHub "release" JSON object. Null if the tag isn't a version.</summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = Str(root, "tag_name") ?? "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? exeUrl = null, checksumUrl = null;
        long exeSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name") ?? "";
                if (name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = Str(a, "browser_download_url");
                    exeSize = a.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0;
                }
                else if (name.Equals(ExeAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    checksumUrl = Str(a, "browser_download_url");
            }

        DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var dt) ? dt : null;
        return new ReleaseInfo(version, tag, Str(root, "body") ?? "", Str(root, "html_url") ?? ReleasesPage, published,
            OnlyHttps(exeUrl), exeSize, OnlyHttps(checksumUrl));

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static string? OnlyHttps(string? url) => url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true ? url : null;
    }

    /// <summary>"v1.2", "1.2.3", "v1.2.3-beta+abc" → 1.2.0 / 1.2.3 / 1.2.3. Pre-release suffixes are ignored.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) s += ".0";
        if (Version.TryParse(s, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>The 64-hex-digit hash in a "HASH  RigScope.exe" checksum file, or null.</summary>
    internal static string? ParseChecksum(string text) => Sha256Hex().Match(text) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Hex();
}
