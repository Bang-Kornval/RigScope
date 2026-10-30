using System.Globalization;
using System.Text.RegularExpressions;
using RigScope.Core.Snapshot;

namespace RigScope.Core.Parsing;

/// <summary>Small pure helpers that turn raw vendor strings into facts. Kept separate so they can be unit-tested.</summary>
public static partial class HardwareParsing
{
    public static HardwareVendor VendorFromName(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx ")) return HardwareVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("ryzen") || n.Contains("authenticamd")) return HardwareVendor.Amd;
        if (n.Contains("intel") || n.Contains("genuineintel") || n.Contains("arc ")) return HardwareVendor.Intel;
        return string.IsNullOrWhiteSpace(n) ? HardwareVendor.Unknown : HardwareVendor.Other;
    }

    public static HardwareVendor VendorFromPciId(string? pnpId)
    {
        var id = (pnpId ?? "").ToUpperInvariant();
        if (id.Contains("VEN_10DE")) return HardwareVendor.Nvidia;
        if (id.Contains("VEN_1002") || id.Contains("VEN_1022")) return HardwareVendor.Amd;
        if (id.Contains("VEN_8086")) return HardwareVendor.Intel;
        return HardwareVendor.Unknown;
    }

    /// <summary>
    /// Windows reports NVIDIA drivers as e.g. 32.0.16.2101; NVIDIA calls that 621.01
    /// (last digit of the third part + the fourth part, split 3.2).
    /// </summary>
    public static string NvidiaVersionFromWindows(string? windowsVersion)
    {
        var parts = (windowsVersion ?? "").Split('.');
        if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length == 0) return "";
        var digits = parts[2][^1..] + parts[3].PadLeft(4, '0');
        return digits.Length == 5 ? $"{digits[..3]}.{digits[3..]}" : "";
    }

    /// <summary>Rated speed from a RAM part number, e.g. CM4X16GC3000C16 → 3000, F4-3600C16D-32GTZN → 3600, KF436C16BB/16 → 3600.</summary>
    public static int RatedMtsFromPartNumber(string? partNumber)
    {
        var p = (partNumber ?? "").Trim().ToUpperInvariant();
        if (p.Length == 0) return 0;
        var m = FourDigitBeforeCas().Match(p);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v is >= 1600 and <= 10000) return v;
        m = GSkill().Match(p);
        if (m.Success && int.TryParse(m.Groups[1].Value, out v) && v is >= 1600 and <= 10000) return v;
        m = KingstonFury().Match(p);
        if (m.Success && int.TryParse(m.Groups[1].Value, out v)) return v * 100;
        return 0;
    }

    [GeneratedRegex(@"(\d{4})C\d{2}")]
    private static partial Regex FourDigitBeforeCas();
    [GeneratedRegex(@"^F[45]-(\d{4})")]
    private static partial Regex GSkill();
    [GeneratedRegex(@"^KF(?:5|4)?(\d{2,3})C\d{2}")]
    private static partial Regex KingstonFury();

    /// <summary>164.999 Hz → 165. Windows sometimes reports 59 for 59.94 etc.; callers round the numerator/denominator first.</summary>
    public static int RoundRefresh(double hz) => (int)Math.Round(hz, MidpointRounding.AwayFromZero);

    /// <summary>Parses nvidia-smi --query-gpu CSV output with header (format=csv).</summary>
    public static List<Dictionary<string, string>> ParseNvidiaSmiCsv(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new List<Dictionary<string, string>>();
        if (lines.Length < 2) return rows;
        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length && i < cells.Length; i++) row[headers[i]] = cells[i];
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// From `nvidia-smi -q -d MEMORY`: BAR1 total of 256 MiB means Resizable BAR is off;
    /// a total close to the VRAM size means it is on. Returns null when BAR1 isn't in the output.
    /// </summary>
    public static bool? ResizableBarFromNvidiaSmiMemory(string output)
    {
        var idx = output.IndexOf("BAR1", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var m = Bar1Total().Match(output, idx);
        if (!m.Success) return null;
        var mib = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return mib > 512;
    }

    [GeneratedRegex(@"Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase)]
    private static partial Regex Bar1Total();

    /// <summary>WMI CIM_DATETIME (20221226000000.000000+000) → date.</summary>
    public static DateTime? ParseCimDate(string? cim)
    {
        if (string.IsNullOrEmpty(cim) || cim.Length < 8) return null;
        return DateTime.TryParseExact(cim[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public static bool IsX3D(string cpuName) => cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hardware ID revision, e.g. PCI\VEN_8086&amp;DEV_15F3&amp;SUBSYS_...&amp;REV_03 → 3.</summary>
    public static int? PciRevision(string? hardwareId)
    {
        var m = PciRev().Match(hardwareId ?? "");
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"REV_([0-9A-F]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex PciRev();
}
