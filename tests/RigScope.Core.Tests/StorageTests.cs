using RigScope.Core.Snapshot;
using RigScope.Core.Storage;

namespace RigScope.Core.Tests;

/// <summary>Scanner tests run against a temporary folder tree the test creates and deletes itself.</summary>
public sealed class ScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigscope-test-" + Guid.NewGuid().ToString("N"));

    public ScannerTests()
    {
        Directory.CreateDirectory(_root);
        Write("root.txt", 100);
        Write(@"a\one.bin", 1000);
        Write(@"a\deep\two.bin", 2000);
        Write(@"b\three.log", 300);
        Write(@"b\big.pak", 40 * 1024 * 1024);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var f = File.Create(path);
        f.SetLength(bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Sizes_add_up_through_the_tree()
    {
        var scan = DirectoryScanner.Scan(_root);
        Assert.Equal(100 + 1000 + 2000 + 300 + 40 * 1024 * 1024, scan.Root.Size);
        Assert.Equal(5, scan.FileCount);
        Assert.Equal(3000, scan.Root.Find("a")!.Size);
        Assert.Equal(2000, scan.Root.Find(@"a\deep")!.Size);
        Assert.Equal(1000, scan.Root.Find("a")!.OwnFileBytes);
        Assert.Equal(0, scan.Root.Find("empty")!.Size);
        Assert.Equal("b", scan.Root.Children[0].Name); // largest first
    }

    [Fact]
    public void Largest_files_and_extensions_are_tracked()
    {
        var scan = DirectoryScanner.Scan(_root);
        Assert.EndsWith("big.pak", scan.LargestFiles.Single().Path);
        Assert.Equal(40 * 1024 * 1024, scan.Extensions[".pak"]);
        Assert.Equal(1000 + 2000, scan.Extensions[".bin"]);
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        var link = Path.Combine(_root, "link-to-a");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_root, "a")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // needs Developer Mode or admin; skip quietly
        var scan = DirectoryScanner.Scan(_root);
        Assert.Null(scan.Root.Find("link-to-a"));
        Assert.Equal(3000, scan.Root.Find("a")!.Size);
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DirectoryScanner.Scan(_root, ct: cts.Token));
    }

    [Fact]
    public void History_saves_trims_loads_and_prunes()
    {
        var history = new ScanHistory(Path.Combine(_root, "history"));
        var scan = DirectoryScanner.Scan(_root);
        var saved = history.Save(scan);
        var loaded = history.Load(saved)!;
        Assert.Equal(scan.Root.Size, loaded.Root.Size);
        Assert.Empty(loaded.Root.Children); // every folder here is under the 50 MB threshold
        Assert.Single(history.List(_root));

        for (int i = 0; i < ScanHistory.KeepPerDrive + 3; i++)
        {
            scan.ScannedAt = scan.ScannedAt.AddMinutes(1);
            history.Save(scan);
        }
        Assert.Equal(ScanHistory.KeepPerDrive, history.List(_root).Count);
    }
}

public class ScanDiffTests
{
    private const long GB = 1L << 30;

    private static ScanResult Scan(DateTime at, long free, FolderNode root) => new() { ScannedAt = at, FreeBytes = free, Root = root };

    private static FolderNode Dir(string name, long size, params FolderNode[] children) => new() { Name = name, Size = size, Children = [.. children] };

    [Fact]
    public void Reports_the_deepest_folder_that_explains_a_change()
    {
        var before = Scan(new DateTime(2026, 10, 30), 214 * GB, Dir(@"C:\", 700 * GB,
            Dir("Program Files (x86)", 517 * GB, Dir("Steam", 437 * GB, Dir("steamapps", 436 * GB, Dir("common", 430 * GB, Dir("MarvelRivals", 89 * GB), Dir("Apex", 78 * GB))))),
            Dir("$Recycle.Bin", 0)));
        var after = Scan(new DateTime(2026, 10, 31), 303 * GB, Dir(@"C:\", 626 * GB,
            Dir("Program Files (x86)", 428 * GB, Dir("Steam", 348 * GB, Dir("steamapps", 347 * GB, Dir("common", 341 * GB, Dir("Apex", 78 * GB))))),
            Dir("$Recycle.Bin", 14 * GB, Dir("S-1-5-21-123", 14 * GB))));

        var diff = ScanDiff.Compare(before, after);

        Assert.Equal(89 * GB, diff.FreeDelta);
        Assert.Equal(2, diff.Changes.Count);
        Assert.Equal(@"Program Files (x86)\Steam\steamapps\common\MarvelRivals", diff.Changes[0].Path);
        Assert.Equal(-89 * GB, diff.Changes[0].Delta);
        Assert.Equal("$Recycle.Bin", diff.Changes[1].Path); // not its per-user subfolder
        Assert.Equal(14 * GB, diff.Changes[1].Delta);
    }

    [Fact]
    public void Small_changes_are_ignored()
    {
        var a = Scan(DateTime.Today, 0, Dir("C:", 10 * GB, Dir("x", 1 * GB)));
        var b = Scan(DateTime.Today.AddDays(1), 0, Dir("C:", 10 * GB + 100_000_000, Dir("x", 1 * GB + 100_000_000)));
        Assert.Empty(ScanDiff.Compare(a, b).Changes);
    }

    [Fact]
    public void Opposite_changes_in_a_parent_are_both_kept()
    {
        // Parent grew overall even though one child shrank: both facts matter.
        var a = Scan(DateTime.Today, 0, Dir("C:", 20 * GB, Dir("p", 10 * GB, Dir("shrank", 5 * GB), Dir("grew", 1 * GB))));
        var b = Scan(DateTime.Today.AddDays(1), 0, Dir("C:", 24 * GB, Dir("p", 14 * GB, Dir("shrank", 1 * GB), Dir("grew", 9 * GB))));
        var paths = ScanDiff.Compare(a, b).Changes.Select(c => c.Path).ToList();
        Assert.Contains(@"p\shrank", paths);
        Assert.Contains(@"p\grew", paths);
    }
}

public class CleanupAdvisorTests
{
    private const long GB = 1L << 30;

    private static string Folder(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.LocalApplicationData => @"D:\Users\pat\AppData\Local",
        Environment.SpecialFolder.UserProfile => @"D:\Users\pat",
        Environment.SpecialFolder.Windows => @"D:\Windows",
        _ => "",
    };

    private static FolderNode Dir(string name, long size, params FolderNode[] children) => new() { Name = name, Size = size, Children = [.. children] };

    /// <summary>Windows installed on D: for a user called "pat" — nothing about the developer's own PC.</summary>
    private static ScanResult Scan() => new()
    {
        RootPath = @"D:\",
        Root = Dir(@"D:\", 600 * GB,
            Dir("$Recycle.Bin", 14 * GB),
            Dir("Users", 80 * GB, Dir("pat", 80 * GB,
                Dir("AppData", 50 * GB, Dir("Local", 40 * GB, Dir("NVIDIA", 13 * GB, Dir("DXCache", 13 * GB)), Dir("Temp", 2 * GB), Dir("Docker", 6 * GB))),
                Dir("Downloads", 100L * 1024 * 1024),
                Dir("Videos", 14 * GB, Dir("SteelSeries Moments", 14 * GB)))),
            Dir("Windows", 38 * GB, Dir("WinSxS", 20 * GB)),
            Dir("Games", 300 * GB)),
    };

    private static SystemSnapshot Snapshot() => new()
    {
        Windows = new WindowsGamingInfo { HiberfilBytes = 12 * GB },
        Games =
        [
            new InstalledGame { Name = "Old Game", Launcher = "Steam", InstallPath = @"D:\Games\steamapps\common\Old", SizeBytes = 80 * GB, LastPlayed = DateTime.Now.AddDays(-45) },
            new InstalledGame { Name = "Current Game", Launcher = "Steam", InstallPath = @"D:\Games\steamapps\common\Now", SizeBytes = 70 * GB, LastPlayed = DateTime.Now.AddDays(-1) },
            new InstalledGame { Name = "Other Drive", Launcher = "Epic", InstallPath = @"E:\Epic\Big", SizeBytes = 90 * GB, LastPlayed = DateTime.Now.AddDays(-400) },
        ],
    };

    [Fact]
    public void Finds_known_places_on_any_drive_and_user()
    {
        var notes = CleanupAdvisor.Analyze(Scan(), Snapshot(), Folder);
        var byTitle = notes.ToDictionary(n => n.Title);

        Assert.Equal(CleanupTag.Safe, byTitle["Recycle Bin"].Tag);
        Assert.Equal(14 * GB, byTitle["Recycle Bin"].Bytes);
        Assert.Equal(13 * GB, byTitle["GPU shader cache"].Bytes);
        Assert.Equal(@"D:\Users\pat\AppData\Local\NVIDIA\DXCache", byTitle["GPU shader cache"].Path);
        Assert.Equal(CleanupTag.Review, byTitle["SteelSeries Moments clips"].Tag);
        Assert.Equal(CleanupTag.Review, byTitle["Hibernation file"].Tag);
        Assert.Equal(CleanupTag.Keep, byTitle["Windows component store (WinSxS)"].Tag);
        Assert.Contains("45 days", byTitle["Old Game"].Body);
        Assert.False(byTitle.ContainsKey("Current Game"));
        Assert.False(byTitle.ContainsKey("Other Drive")); // on E:, not this scan
        Assert.False(byTitle.ContainsKey("Downloads"));   // 100 MB is below the threshold
    }

    [Fact]
    public void Leave_alone_notes_come_last()
    {
        var notes = CleanupAdvisor.Analyze(Scan(), Snapshot(), Folder);
        Assert.Equal(CleanupTag.Keep, notes[^1].Tag);
        Assert.All(notes.SkipLast(1), n => Assert.NotEqual(CleanupTag.Keep, n.Tag));
    }

    [Fact]
    public void Laptop_keeps_its_hibernation_file()
    {
        var s = Snapshot();
        s.Firmware.IsLaptop = true;
        Assert.Equal(CleanupTag.Keep, CleanupAdvisor.Analyze(Scan(), s, Folder).Single(n => n.Title == "Hibernation file").Tag);
    }

    [Fact]
    public void Works_without_a_snapshot() =>
        Assert.Contains(CleanupAdvisor.Analyze(Scan(), null, Folder), n => n.Title == "Recycle Bin");
}
