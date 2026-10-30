using RigScope.Core.Probes;
using RigScope.Core.Rules;
using RigScope.Core.Snapshot;

// rigscope snapshot [file]            Capture a read-only snapshot of this PC as JSON (stdout or file).
// rigscope check [game] [--from file] Run the competitive checks (default game: cs2 if installed).
var command = args.FirstOrDefault() ?? "help";
var progress = new Progress<string>(p => Console.Error.WriteLine($"  reading {p}…"));

switch (command)
{
    case "snapshot":
    {
        var snapshot = await ProbeRunner.CaptureAsync(progress: progress);
        var json = SnapshotJson.Serialize(snapshot);
        if (args.Length > 1) { File.WriteAllText(args[1], json); Console.Error.WriteLine($"Saved to {args[1]}"); }
        else Console.WriteLine(json);
        return snapshot.ProbeErrors.Count == 0 ? 0 : 2;
    }
    case "check":
    {
        var fromIndex = Array.IndexOf(args, "--from");
        var snapshot = fromIndex > 0 && fromIndex + 1 < args.Length
            ? SnapshotJson.Deserialize(File.ReadAllText(args[fromIndex + 1]))
            : await ProbeRunner.CaptureAsync(progress: progress);
        var game = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && (fromIndex < 0 || a != args[fromIndex + 1]))
                   ?? (snapshot.Games.Any(g => g.Id == "cs2") ? "cs2" : null);

        var findings = new RuleEngine(AdvicePack.LoadBuiltIn()).Evaluate(snapshot, game);
        Console.WriteLine($"RigScope check for {game ?? "general PC"} — {findings.Count} findings\n");
        foreach (var group in findings.GroupBy(f => f.Status))
        {
            Console.WriteLine($"== {group.Key} ({group.Count()})");
            foreach (var f in group)
            {
                Console.WriteLine($"  [{f.Rule.Category}] {f.Title}: {f.Summary}");
                if (f.Current.Length > 0) Console.WriteLine($"      now: {f.Current}");
            }
            Console.WriteLine();
        }
        foreach (var (probe, error) in snapshot.ProbeErrors) Console.WriteLine($"  probe failed: {probe}: {error}");
        return 0;
    }
    case "scan":
    {
        var root = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? Path.GetPathRoot(Environment.SystemDirectory)!;
        var scan = await RigScope.Core.Storage.DirectoryScanner.ScanAsync(root,
            new Progress<RigScope.Core.Storage.ScanProgress>(p => Console.Error.Write($"\r  {p.Files:N0} files, {SnapshotFormat.Bytes(p.Bytes)}…   ")));
        Console.Error.WriteLine();
        Console.WriteLine($"{root}: {SnapshotFormat.Bytes(scan.Root.Size)} in {scan.FileCount:N0} files, scanned in {scan.Duration.TotalSeconds:0} s");
        Console.WriteLine($"Drive: {SnapshotFormat.Bytes(scan.UsedBytes)} used, {SnapshotFormat.Bytes(scan.FreeBytes)} free, {SnapshotFormat.Bytes(scan.Unaccounted)} not readable\n");
        foreach (var c in scan.Root.Children.Take(8)) Console.WriteLine($"  {SnapshotFormat.Bytes(c.Size),10}  {c.Name}");

        RigScope.Core.Snapshot.SystemSnapshot? snapshot = args.Contains("--notes") ? await ProbeRunner.CaptureAsync() : null;
        if (snapshot is not null)
        {
            Console.WriteLine("\nWhere to look first:");
            foreach (var n in RigScope.Core.Storage.CleanupAdvisor.Analyze(scan, snapshot))
                Console.WriteLine($"  {SnapshotFormat.Bytes(n.Bytes),10}  [{n.Tag}] {n.Title}");
        }
        if (args.Contains("--save"))
        {
            var history = new RigScope.Core.Storage.ScanHistory();
            var previous = history.Previous(root, scan.ScannedAt);
            Console.WriteLine($"\nSaved to {history.Save(scan)}");
            if (previous is not null)
                foreach (var c in RigScope.Core.Storage.ScanDiff.Compare(previous, scan).Changes)
                    Console.WriteLine($"  {(c.Delta > 0 ? "+" : "")}{SnapshotFormat.Bytes(c.Delta),10}  {c.Path}");
        }
        return 0;
    }
    default:
        Console.WriteLine("""
            RigScope — read-only competitive FPS check for Windows

              rigscope snapshot [file]              Save what RigScope can see about this PC as JSON
              rigscope check [game] [--from file]   Run the checks (games: cs2, valorant, apex, overwatch2,
                                                    fortnite, r6siege, marvelrivals)
              rigscope scan [path] [--notes] [--save]
                                                    Measure folder sizes (read-only). --notes adds cleanup
                                                    suggestions; --save keeps the scan to compare next time
            """);
        return 0;
}

static class SnapshotFormat
{
    public static string Bytes(long b) => RigScope.Core.Rules.SnapshotQuery.FormatBytes(b);
}
