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
    default:
        Console.WriteLine("""
            RigScope — read-only competitive FPS check for Windows

              rigscope snapshot [file]              Save what RigScope can see about this PC as JSON
              rigscope check [game] [--from file]   Run the checks (games: cs2, valorant, apex, overwatch2,
                                                    fortnite, r6siege, marvelrivals)
            """);
        return 0;
}
