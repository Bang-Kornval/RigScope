namespace RigScope.Core.Storage;

public sealed record FolderChange(string Path, long Delta);

public sealed record ScanComparison(DateTime From, DateTime To, long FreeDelta, IReadOnlyList<FolderChange> Changes);

public static class ScanDiff
{
    public const long MinChange = 300L * 1024 * 1024;

    /// <summary>
    /// Folders that grew or shrank by at least <see cref="MinChange"/>. When a subfolder explains most of its
    /// parent's change, only the subfolder is listed, so you see "MarvelRivals −88 GB" rather than every parent of it.
    /// </summary>
    public static ScanComparison Compare(ScanResult before, ScanResult after, int maxDepth = 7)
    {
        var a = Flatten(before.Root, maxDepth);
        var b = Flatten(after.Root, maxDepth);
        var diffs = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(p => new FolderChange(p, b.GetValueOrDefault(p) - a.GetValueOrDefault(p)))
            .Where(c => Math.Abs(c.Delta) >= MinChange && c.Path.Length > 0)
            .ToList();

        var kept = diffs.Where(x => !diffs.Any(y =>
                y.Path.StartsWith(x.Path + "\\", StringComparison.OrdinalIgnoreCase) && Math.Sign(y.Delta) == Math.Sign(x.Delta)
                && Math.Abs(y.Delta) >= Math.Abs(x.Delta) * 0.85))
            .OrderByDescending(c => Math.Abs(c.Delta))
            .ToList();

        return new ScanComparison(before.ScannedAt, after.ScannedAt, after.FreeBytes - before.FreeBytes, kept);
    }

    private static Dictionary<string, long> Flatten(FolderNode root, int maxDepth)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Walk(FolderNode n, string path, int depth)
        {
            map[path] = n.Size;
            // The Recycle Bin's per-user subfolders aren't meaningful; report the bin as a whole.
            if (depth >= maxDepth || n.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var c in n.Children) Walk(c, path.Length == 0 ? c.Name : path + "\\" + c.Name, depth + 1);
        }
        Walk(root, "", 0);
        return map;
    }
}
