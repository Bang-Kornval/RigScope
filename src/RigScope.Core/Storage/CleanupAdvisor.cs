using RigScope.Core.Snapshot;

namespace RigScope.Core.Storage;

public enum CleanupTag { Safe, Review, Keep }

/// <summary>A place worth looking at to free space, with how to remove it properly. RigScope never deletes anything itself.</summary>
public sealed record CleanupNote(CleanupTag Tag, string Title, long Bytes, string Body, string? Path = null);

/// <summary>Turns a scan (plus the snapshot's game list) into cleanup notes. Works for any drive and any user.</summary>
public static class CleanupAdvisor
{
    public const long MinNoteBytes = 500L * 1024 * 1024;
    public const int UnplayedDays = 30;

    public static List<CleanupNote> Analyze(ScanResult scan, SystemSnapshot? snapshot = null, Func<Environment.SpecialFolder, string>? folders = null)
    {
        folders ??= Environment.GetFolderPath;
        var notes = new List<CleanupNote>();
        var local = folders(Environment.SpecialFolder.LocalApplicationData);
        var profile = folders(Environment.SpecialFolder.UserProfile);
        var windows = folders(Environment.SpecialFolder.Windows);

        long Size(string fullPath) => Node(scan, fullPath)?.Size ?? 0;
        void Add(CleanupTag tag, string title, string path, string body)
        {
            var bytes = Size(path);
            if (bytes >= MinNoteBytes) notes.Add(new CleanupNote(tag, title, bytes, body, path));
        }

        // Recycle Bin
        Add(CleanupTag.Safe, "Recycle Bin", Path.Combine(scan.RootPath, "$Recycle.Bin"),
            "Files you deleted are still here and still use space. Empty the Recycle Bin to get it back, but restore anything you want to keep first.");

        // Shader caches (every GPU vendor)
        var shaderCaches = new[]
        {
            Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"), Path.Combine(local, "AMD", "VkCache"),
            Path.Combine(local, "Intel", "ShaderCache"), Path.Combine(local, "D3DSCache"),
        };
        var shaderBytes = shaderCaches.Sum(Size);
        if (shaderBytes >= MinNoteBytes)
            notes.Add(new CleanupNote(CleanupTag.Safe, "GPU shader cache", shaderBytes,
                "Precompiled shaders. The driver rebuilds them automatically, so clearing only costs a little stutter the first time each game loads. Clear it with Disk Cleanup (DirectX Shader Cache) or by emptying the folder; to stop it growing, cap the size in your GPU control panel.",
                shaderCaches.Where(p => Size(p) > 0).OrderByDescending(Size).FirstOrDefault()));

        // Temp and crash dumps
        Add(CleanupTag.Safe, "Temporary files", Path.Combine(local, "Temp"),
            "Settings › System › Storage › Temporary files removes these safely. Files in use are skipped automatically.");
        Add(CleanupTag.Safe, "Windows temporary files", Path.Combine(windows, "Temp"),
            "Cleared by Settings › System › Storage › Temporary files.");
        Add(CleanupTag.Safe, "Crash dumps", Path.Combine(local, "CrashDumps"),
            "Memory dumps from programs that crashed. Only useful when reporting a bug.");
        Add(CleanupTag.Safe, "Windows crash dumps", Path.Combine(windows, "Minidump"),
            "Small dumps from blue-screen crashes. Remove with Disk Cleanup › System error memory dump files.");
        Add(CleanupTag.Safe, "Delivery Optimization cache", Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"),
            "Windows Update downloads kept to share with other PCs. Settings › System › Storage › Temporary files › Delivery Optimization Files.");
        Add(CleanupTag.Safe, "Previous Windows installation", Path.Combine(scan.RootPath, "Windows.old"),
            "Left after a Windows upgrade, for rolling back. Settings › System › Storage › Temporary files › Previous Windows installation(s).");

        // Personal folders worth reviewing
        Add(CleanupTag.Review, "Downloads", Path.Combine(profile, "Downloads"),
            "Installers and files you downloaded. Sort by size in File Explorer and remove what you don't need.");
        foreach (var (title, dir) in new[]
        {
            ("SteelSeries Moments clips", Path.Combine(profile, "Videos", "SteelSeries Moments")),
            ("Xbox Game Bar captures", Path.Combine(profile, "Videos", "Captures")),
            ("Medal clips", Path.Combine(profile, "Videos", "Medal")),
            ("NVIDIA recordings", Path.Combine(profile, "Videos", "NVIDIA")),
            ("OBS recordings", Path.Combine(profile, "Videos", "OBS")),
        })
            Add(CleanupTag.Review, title, dir, "Recorded game clips. Keep the ones you want, delete the rest, or move them to another drive or cloud storage.");

        // Steam extras
        foreach (var lib in SteamLibrariesOnDrive(scan, snapshot))
        {
            Add(CleanupTag.Review, "Steam Workshop content", Path.Combine(lib, "steamapps", "workshop"),
                "Mods and maps you subscribed to. Unsubscribe from the ones you don't use and Steam removes them.");
            Add(CleanupTag.Safe, "Steam shader cache", Path.Combine(lib, "steamapps", "shadercache"),
                "Precompiled shaders Steam downloads for Vulkan and OpenGL games. Steam rebuilds them when needed.");
        }

        // Docker / WSL disks
        Add(CleanupTag.Review, "Docker and WSL disk images", Path.Combine(local, "Docker"),
            "Virtual disks for Docker Desktop. Run \"docker system prune\" in Docker, or remove unused WSL distros with \"wsl --unregister\". Don't delete the files by hand.");

        // Hibernation file
        if (snapshot is { Windows.HiberfilBytes: > 0 } s && OnThisDrive(scan, Path.Combine(scan.RootPath, "hiberfil.sys")) && s.Windows.HiberfilBytes >= MinNoteBytes)
            notes.Add(new CleanupNote(s.Firmware.IsLaptop ? CleanupTag.Keep : CleanupTag.Review, "Hibernation file", s.Windows.HiberfilBytes,
                s.Firmware.IsLaptop
                    ? "Used when the laptop hibernates. Keep it unless you never hibernate."
                    : "Used for Hibernate and Fast Startup. If you never hibernate this PC, run \"powercfg /h off\" in an admin terminal and Windows removes it. Never delete it by hand.",
                Path.Combine(scan.RootPath, "hiberfil.sys")));

        // Games not played recently
        if (snapshot is not null)
        {
            foreach (var g in snapshot.Games.Where(g => OnThisDrive(scan, g.InstallPath) && g.SizeBytes >= MinNoteBytes * 4))
            {
                var days = g.DaysSincePlayed;
                if (days is null || days < UnplayedDays) continue;
                notes.Add(new CleanupNote(CleanupTag.Review, g.Name, g.SizeBytes,
                    $"Last played {days} days ago. Uninstall it through {g.Launcher} if you're taking a break; you can reinstall later and online progress is kept.",
                    g.InstallPath));
            }
        }

        // Things to leave alone
        Add(CleanupTag.Keep, "Windows component store (WinSxS)", Path.Combine(windows, "WinSxS"),
            "Looks bigger than it is, because many files are shared with System32. Don't touch it by hand; Settings › Storage › Temporary files › Windows Update Cleanup trims it safely.");

        return notes
            .GroupBy(n => n.Path ?? n.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(n => n.Tag == CleanupTag.Keep).ThenByDescending(n => n.Bytes).ToList();
    }

    /// <summary>The scan's node for a full path on the scanned drive, or null when it's elsewhere or wasn't kept.</summary>
    public static FolderNode? Node(ScanResult scan, string fullPath)
    {
        if (!OnThisDrive(scan, fullPath)) return null;
        var relative = fullPath[scan.RootPath.TrimEnd('\\').Length..].TrimStart('\\');
        return relative.Length == 0 ? scan.Root : scan.Root.Find(relative);
    }

    private static bool OnThisDrive(ScanResult scan, string fullPath) =>
        fullPath.StartsWith(scan.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
        || fullPath.Equals(scan.RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SteamLibrariesOnDrive(ScanResult scan, SystemSnapshot? snapshot)
    {
        var fromGames = snapshot?.Games.Where(g => g.Launcher == "Steam")
            .Select(g => g.InstallPath)
            .Select(p => p.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) is var i and > 0 ? p[..i] : null)
            .OfType<string>() ?? [];
        return fromGames.Append(Path.Combine(scan.RootPath, "Program Files (x86)", "Steam"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => OnThisDrive(scan, p));
    }
}
