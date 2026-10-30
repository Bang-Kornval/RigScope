using System.IO.Compression;
using System.Text.Json;

namespace RigScope.Core.Storage;

/// <summary>
/// Saves scans so the next one can show what changed. Stored in %LOCALAPPDATA%\RigScope\scans as gzip JSON,
/// trimmed to folders of meaningful size so each file stays small. This is the only place RigScope writes to disk.
/// </summary>
public sealed class ScanHistory(string? folder = null)
{
    public const long KeepFolderBytes = 50L * 1024 * 1024;
    public const int KeepDepth = 10;
    public const int KeepPerDrive = 10;

    public string Folder { get; } = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigScope", "scans");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Save(ScanResult scan)
    {
        Directory.CreateDirectory(Folder);
        var trimmed = new ScanResult
        {
            RootPath = scan.RootPath, ScannedAt = scan.ScannedAt, Duration = scan.Duration, FileCount = scan.FileCount,
            TotalBytes = scan.TotalBytes, FreeBytes = scan.FreeBytes, Root = Trim(scan.Root, 0),
            LargestFiles = scan.LargestFiles.Take(150).ToList(),
            Extensions = scan.Extensions.OrderByDescending(kv => kv.Value).Take(60).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        var path = Path.Combine(Folder, $"{DriveKey(scan.RootPath)}_{scan.ScannedAt:yyyyMMdd-HHmmss}.json.gz");
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, trimmed, Json);
        Prune(scan.RootPath);
        return path;
    }

    public IReadOnlyList<string> List(string rootPath)
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, $"{DriveKey(rootPath)}_*.json.gz").OrderByDescending(f => f).ToList();
    }

    public ScanResult? Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<ScanResult>(gz, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The most recent saved scan of this drive taken before <paramref name="before"/>.</summary>
    public ScanResult? Previous(string rootPath, DateTime before) =>
        List(rootPath).Select(Load).FirstOrDefault(s => s is not null && s.ScannedAt < before.AddSeconds(-1));

    private void Prune(string rootPath)
    {
        foreach (var old in List(rootPath).Skip(KeepPerDrive))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static string DriveKey(string rootPath)
    {
        var key = new string(rootPath.Where(char.IsLetterOrDigit).ToArray());
        return key.Length == 0 ? "root" : key.ToUpperInvariant();
    }

    /// <summary>Keeps folders above the size threshold; the rest are rolled into their parent's size.</summary>
    internal static FolderNode Trim(FolderNode node, int depth) => new()
    {
        Name = node.Name, Size = node.Size, Files = node.Files, OwnFileBytes = node.OwnFileBytes, Denied = node.Denied,
        Children = depth >= KeepDepth ? [] : node.Children.Where(c => c.Size >= KeepFolderBytes).Select(c => Trim(c, depth + 1)).ToList(),
    };
}
