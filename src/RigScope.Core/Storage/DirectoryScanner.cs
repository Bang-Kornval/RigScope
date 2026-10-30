using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace RigScope.Core.Storage;

/// <summary>A folder in a scan. Sizes include everything below it.</summary>
public sealed class FolderNode
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long Files { get; set; }
    /// <summary>Bytes in files directly inside this folder (not in subfolders).</summary>
    public long OwnFileBytes { get; set; }
    public List<FolderNode> Children { get; set; } = [];
    /// <summary>Folders that couldn't be read (permissions). Their size isn't counted.</summary>
    public bool Denied { get; set; }

    public FolderNode? Child(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a descendant by a path relative to this node, e.g. "Users\me\AppData".</summary>
    public FolderNode? Find(string relativePath)
    {
        var node = this;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Child(part);
            if (node is null) return null;
        }
        return node;
    }
}

public sealed record LargeFile(string Path, long Size, DateTime Modified);

public sealed class ScanResult
{
    public string RootPath { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public FolderNode Root { get; set; } = new();
    public List<LargeFile> LargestFiles { get; set; } = [];
    /// <summary>Extension (lower case, "(none)" if missing) → total bytes.</summary>
    public Dictionary<string, long> Extensions { get; set; } = [];
    /// <summary>Space the scan couldn't see: system files, other users, page file, restore points.</summary>
    public long Unaccounted => Math.Max(0, UsedBytes - Root.Size);
}

public sealed record ScanProgress(long Files, long Bytes, string CurrentFolder);

/// <summary>
/// Read-only folder size scanner. Top-level folders are scanned in parallel; links and junctions are skipped
/// so nothing is counted twice. Uses .NET's FileSystemEnumerable, which reads directory entries in large batches
/// without opening each file.
/// </summary>
public static class DirectoryScanner
{
    private const int TopFileCount = 300;

    public static Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Scan(rootPath, progress, ct), ct);

    public static ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = new ScanState(progress, ct);
        var root = new FolderNode { Name = rootPath };

        // Files at the root, then each top-level folder on its own thread.
        var topDirs = new List<string>();
        foreach (var e in Entries(rootPath, state))
        {
            if (e.IsDirectory) { if (!e.IsReparse) topDirs.Add(e.FullPath); }
            else state.AddFile(root, e);
        }
        var children = new ConcurrentBag<FolderNode>();
        Parallel.ForEach(topDirs, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = ct },
            dir => children.Add(ScanFolder(dir, state)));

        root.Children = children.OrderByDescending(c => c.Size).ToList();
        root.Size = root.OwnFileBytes + root.Children.Sum(c => c.Size);
        root.Files += root.Children.Sum(c => c.Files);

        var result = new ScanResult
        {
            RootPath = rootPath,
            ScannedAt = started,
            Duration = sw.Elapsed,
            FileCount = state.FileCount,
            Root = root,
            LargestFiles = state.TopFiles(),
            Extensions = state.Extensions.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(rootPath) ?? rootPath);
            if (drive.IsReady) { result.TotalBytes = drive.TotalSize; result.FreeBytes = drive.TotalFreeSpace; }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        return result;
    }

    private static FolderNode ScanFolder(string path, ScanState state)
    {
        state.Ct.ThrowIfCancellationRequested();
        var node = new FolderNode { Name = Path.GetFileName(path) };
        var subdirs = new List<string>();
        try
        {
            foreach (var e in Entries(path, state))
            {
                if (e.IsDirectory) { if (!e.IsReparse) subdirs.Add(e.FullPath); }
                else state.AddFile(node, e);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Keep whatever was read before access stopped; subfolders found so far are dropped.
            node.Denied = true;
            node.Size = node.OwnFileBytes;
            return node;
        }

        foreach (var sub in subdirs)
        {
            var child = ScanFolder(sub, state);
            node.Children.Add(child);
            node.Size += child.Size;
            node.Files += child.Files;
        }
        node.Size += node.OwnFileBytes;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        state.Report(path);
        return node;
    }

    private readonly record struct Entry(string FullPath, string Name, bool IsDirectory, bool IsReparse, long Length, DateTime LastWrite);

    private static FileSystemEnumerable<Entry> Entries(string path, ScanState state) =>
        new(path, (ref FileSystemEntry e) => new Entry(
                e.ToFullPath(), e.FileName.ToString(), e.IsDirectory,
                (e.Attributes & FileAttributes.ReparsePoint) != 0, e.Length, e.LastWriteTimeUtc.LocalDateTime),
            new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false });

    private sealed class ScanState(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        public CancellationToken Ct { get; } = ct;
        public readonly ConcurrentDictionary<string, long> Extensions = new(StringComparer.OrdinalIgnoreCase);
        private long _files, _bytes, _lastReport;
        private readonly object _topLock = new();
        private readonly PriorityQueue<LargeFile, long> _top = new();

        public long FileCount => Interlocked.Read(ref _files);

        public void AddFile(FolderNode folder, Entry e)
        {
            folder.OwnFileBytes += e.Length;
            folder.Files++;
            Interlocked.Increment(ref _files);
            Interlocked.Add(ref _bytes, e.Length);

            var ext = Path.GetExtension(e.Name);
            Extensions.AddOrUpdate(ext.Length == 0 ? "(none)" : ext.ToLowerInvariant(), e.Length, (_, v) => v + e.Length);

            if (e.Length < 32 * 1024 * 1024) return;
            lock (_topLock)
            {
                if (_top.Count < TopFileCount) _top.Enqueue(new LargeFile(e.FullPath, e.Length, e.LastWrite), e.Length);
                else if (_top.TryPeek(out _, out var smallest) && e.Length > smallest)
                    _top.EnqueueDequeue(new LargeFile(e.FullPath, e.Length, e.LastWrite), e.Length);
            }
        }

        public List<LargeFile> TopFiles()
        {
            lock (_topLock) return _top.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Size).ToList();
        }

        public void Report(string folder)
        {
            if (progress is null) return;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReport);
            if (now - last < 150 || Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            progress.Report(new ScanProgress(FileCount, Interlocked.Read(ref _bytes), folder));
        }
    }
}
