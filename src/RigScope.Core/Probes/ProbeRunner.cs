using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Probes;

/// <summary>A read-only fact collector. Probes must never change system state.</summary>
public interface IProbe
{
    string Name { get; }
    void Collect(SystemSnapshot snapshot);
}

public static class ProbeRunner
{
    public static IReadOnlyList<IProbe> DefaultProbes() =>
    [
        new OsProbe(), new CpuProbe(), new MemoryProbe(), new FirmwareProbe(), new SecurityProbe(),
        new GpuProbe(), new DisplayProbe(), new WindowsGamingProbe(), new KnownAppsProbe(),
        new NetworkProbe(), new DriveProbe(), new GamesProbe(), new Cs2Probe(),
    ];

    /// <summary>Runs every probe. Probes fill separate parts of the snapshot, so they run in parallel;
    /// the games and CS2 probes run last because they read what earlier probes found.</summary>
    public static async Task<SystemSnapshot> CaptureAsync(IReadOnlyList<IProbe>? probes = null, IProgress<string>? progress = null)
    {
        probes ??= DefaultProbes();
        var snapshot = new SystemSnapshot
        {
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
            IsElevated = IsElevated(),
        };
        var late = probes.Where(p => p is GamesProbe or Cs2Probe).ToList();
        var early = probes.Except(late).ToList();

        await Task.WhenAll(early.Select(p => Task.Run(() => RunOne(p, snapshot, progress))));
        foreach (var p in late) await Task.Run(() => RunOne(p, snapshot, progress));
        return snapshot;
    }

    private static void RunOne(IProbe probe, SystemSnapshot snapshot, IProgress<string>? progress)
    {
        progress?.Report(probe.Name);
        try { probe.Collect(snapshot); }
        catch (Exception ex)
        {
            lock (snapshot.ProbeErrors) snapshot.ProbeErrors[probe.Name] = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Shared read-only helpers for probes.</summary>
internal static class Sys
{
    public static List<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (ManagementException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (System.Runtime.InteropServices.COMException) { return []; }
    }

    public static string Str(this ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; } catch (ManagementException) { return ""; }
    }

    public static long Long(this ManagementBaseObject o, string prop)
    {
        try { return o[prop] is { } v ? Convert.ToInt64(v) : 0; } catch (Exception) { return 0; }
    }

    public static object? Reg(RegistryHive hive, string key, string value, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(key, writable: false);
            return k?.GetValue(value);
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    public static int? RegInt(RegistryHive hive, string key, string value) =>
        Reg(hive, key, value) switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var p) => p, _ => null };

    public static string RegStr(RegistryHive hive, string key, string value, RegistryView view = RegistryView.Registry64) =>
        Reg(hive, key, value, view)?.ToString() ?? "";

    public static string[] RegSubKeys(RegistryHive hive, string key, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(key, writable: false);
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    public static string[] RegValueNames(RegistryHive hive, string key, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(key, writable: false);
            return k?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    /// <summary>Runs a read-only command-line tool and returns stdout, or null if it isn't there or times out.</summary>
    public static string? Run(string exe, string args, int timeoutMs = 8000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return output.Result;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string? FindOnPath(string exe, params string[] extraDirs)
    {
        foreach (var dir in extraDirs.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), exe);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
