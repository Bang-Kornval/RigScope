using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RigScope.Core.Rules;

/// <summary>
/// Paths, conditions and templates over a snapshot JSON tree.
///
/// Paths:      gpu.vendor · displays[0].refreshHz · apps[*].name · apps[category=rgb,running=true]
///             cs2.primary.video.msaa_samples · game.id
///             A filter keeps array elements whose fields equal the given values (true/false/numbers compare as text).
///
/// Conditions: { "path": "gpu.vendor", "eq": "nvidia" }   — true if ANY value at the path matches every operator given.
///             operators: eq, ne, in, notIn, gt, gte, lt, lte, contains, matches (regex, ignore case),
///                        exists (true/false), countGte, countLte, countEq
///             { "all": [ … ] } · { "any": [ … ] } · { "not": { … } }
///
/// Templates:  "Driver {gpu.vendorDriverVersion|default:unknown}, {apps[category=rgb].name|join}"
///             filters: join, count, gb, mb, days, date, default:x, map:a=b;c=d, bool:Yes/No, upper, round
/// </summary>
public static partial class SnapshotQuery
{
    public static List<JsonNode?> Resolve(JsonNode? root, string path)
    {
        var current = new List<JsonNode?> { root };
        foreach (var seg in Segments(path))
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                if (node is null) continue;
                if (seg.Name.Length > 0)
                {
                    if (node is not JsonObject obj || !TryGetCaseInsensitive(obj, seg.Name, out var child)) continue;
                    if (seg.Selector is null) { next.Add(child); continue; }
                    ApplySelector(child, seg.Selector, next);
                }
                else if (seg.Selector is not null) ApplySelector(node, seg.Selector, next);
            }
            current = next;
        }
        return current;
    }

    public static JsonNode? First(JsonNode? root, string path) => Resolve(root, path).FirstOrDefault(n => n is not null);

    private static bool TryGetCaseInsensitive(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value)) return true;
        foreach (var (k, v) in obj)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = null;
        return false;
    }

    private static void ApplySelector(JsonNode? node, string selector, List<JsonNode?> output)
    {
        if (node is not JsonArray arr) return;
        if (selector == "*") { output.AddRange(arr); return; }
        if (int.TryParse(selector, out var index))
        {
            if (index >= 0 && index < arr.Count) output.Add(arr[index]);
            return;
        }
        var filters = selector.Split(',').Select(f => f.Split('=', 2)).Where(p => p.Length == 2).ToList();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            if (filters.All(f => TryGetCaseInsensitive(o, f[0].Trim(), out var v) && string.Equals(Text(v), f[1].Trim(), StringComparison.OrdinalIgnoreCase)))
                output.Add(item);
        }
    }

    private sealed record Segment(string Name, string? Selector);

    private static IEnumerable<Segment> Segments(string path)
    {
        foreach (var part in SplitPath(path))
        {
            var m = SegmentRegex().Match(part);
            if (!m.Success) { yield return new Segment(part, null); continue; }
            yield return new Segment(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null);
        }
    }

    /// <summary>Splits on dots that are not inside a [filter].</summary>
    private static List<string> SplitPath(string path)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var c in path)
        {
            if (c == '[') depth++;
            if (c == ']') depth--;
            if (c == '.' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    [GeneratedRegex(@"^([^\[]*)(?:\[(.*)\])?$")]
    private static partial Regex SegmentRegex();

    /// <summary>Text form of a JSON value: strings as-is, numbers invariant, booleans "true"/"false", null → null.</summary>
    public static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    public static double? Number(JsonNode? node)
    {
        var t = Text(node);
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // ---------------- Conditions ----------------

    public static bool Evaluate(JsonNode? condition, JsonNode root)
    {
        if (condition is null) return true;
        if (condition is not JsonObject c) throw new FormatException($"Condition must be an object: {condition.ToJsonString()}");

        if (c["all"] is JsonArray all) return all.All(x => Evaluate(x, root));
        if (c["any"] is JsonArray any) return any.Any(x => Evaluate(x, root));
        if (c["not"] is JsonNode not) return !Evaluate(not, root);

        var path = Text(c["path"]) ?? throw new FormatException($"Condition needs a path: {c.ToJsonString()}");
        var values = Resolve(root, path).Where(v => v is not null).ToList();

        if (c["exists"] is JsonNode ex) { if ((values.Count > 0) != (Text(ex) == "true")) return false; }
        if (c["countGte"] is JsonNode cg && !(values.Count >= (Number(cg) ?? 0))) return false;
        if (c["countLte"] is JsonNode cl && !(values.Count <= (Number(cl) ?? 0))) return false;
        if (c["countEq"] is JsonNode ce && values.Count != (int)(Number(ce) ?? -1)) return false;

        var valueOps = c.Where(p => p.Key is "eq" or "ne" or "in" or "notIn" or "gt" or "gte" or "lt" or "lte" or "contains" or "matches").ToList();
        if (valueOps.Count == 0) return true;
        // "ne"/"notIn" on a missing value count as true: a value that isn't there isn't equal to anything.
        if (values.Count == 0) return valueOps.All(o => o.Key is "ne" or "notIn");
        var resolved = valueOps.Select(o => (o.Key, Value: ResolveReference(o.Value, root))).ToList();
        return values.Any(v => resolved.All(o => Compare(v, o.Key, o.Value)));
    }

    /// <summary>A value written as "{path}" compares against another value in the snapshot.</summary>
    private static JsonNode? ResolveReference(JsonNode? expected, JsonNode root) =>
        Text(expected) is { Length: > 2 } t && t[0] == '{' && t[^1] == '}' && expected is JsonValue
            ? First(root, t[1..^1])?.DeepClone()
            : expected;

    private static bool Compare(JsonNode? value, string op, JsonNode? expected)
    {
        var text = Text(value) ?? "";
        switch (op)
        {
            case "eq": return Equal(value, expected);
            case "ne": return !Equal(value, expected);
            case "in": return expected is JsonArray a && a.Any(e => Equal(value, e));
            case "notIn": return expected is JsonArray b && !b.Any(e => Equal(value, e));
            case "contains":
                if (value is JsonArray arr) return arr.Any(e => Equal(e, expected));
                return text.Contains(Text(expected) ?? "", StringComparison.OrdinalIgnoreCase);
            case "matches": return Regex.IsMatch(text, Text(expected) ?? "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        var n = Number(value);
        var e2 = Number(expected);
        if (n is null || e2 is null) return false;
        return op switch { "gt" => n > e2, "gte" => n >= e2, "lt" => n < e2, "lte" => n <= e2, _ => false };
    }

    private static bool Equal(JsonNode? a, JsonNode? b)
    {
        var na = Number(a);
        var nb = Number(b);
        if (na is not null && nb is not null) return Math.Abs(na.Value - nb.Value) < 1e-9;
        return string.Equals(Text(a), Text(b), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Templates ----------------

    public static string Render(string? template, JsonNode root)
    {
        if (string.IsNullOrEmpty(template)) return "";
        // Innermost placeholders first, so "{a|default:{b}}" works.
        var text = template;
        for (int pass = 0; pass < 4 && TemplateRegex().IsMatch(text); pass++) text = RenderPass(text, root);
        return text;
    }

    private static string RenderPass(string template, JsonNode root)
    {
        return TemplateRegex().Replace(template, m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            var values = Resolve(root, parts[0].Trim()).Where(v => v is not null).ToList();
            string? result = values.Count switch { 0 => null, 1 => Text(values[0]), _ => string.Join(", ", values.Select(Text)) };
            foreach (var raw in parts.Skip(1))
            {
                var f = raw.Trim();
                var arg = f.Contains(':') ? f[(f.IndexOf(':') + 1)..] : "";
                var name = f.Contains(':') ? f[..f.IndexOf(':')] : f;
                result = name switch
                {
                    "count" => values.Count.ToString(CultureInfo.InvariantCulture),
                    "join" => values.Count == 0 ? null : string.Join(", ", values.Select(Text)),
                    "gb" => Number(result) is { } b ? FormatBytes(b) : result,
                    "mb" => Number(result) is { } mb ? $"{mb / 1024 / 1024:0} MB" : result,
                    "days" => Number(result) is { } d ? (d == 1 ? "1 day" : $"{d:0} days") : result,
                    "date" => DateTime.TryParse(result, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : result,
                    "round" => Number(result) is { } r ? Math.Round(r).ToString(CultureInfo.InvariantCulture) : result,
                    "upper" => result?.ToUpperInvariant(),
                    "bool" => result is null ? null : (result == "true" ? arg.Split('/')[0] : arg.Split('/').ElementAtOrDefault(1) ?? result),
                    "map" => MapValue(result, arg),
                    "default" => string.IsNullOrEmpty(result) ? arg : result,
                    _ => result,
                };
            }
            return result ?? "?";
        });
    }

    private static string? MapValue(string? value, string spec)
    {
        if (value is null) return null;
        foreach (var pair in spec.Split(';'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && (string.Equals(kv[0].Trim(), value, StringComparison.OrdinalIgnoreCase) || kv[0].Trim() == "*")) return kv[1];
        }
        return value;
    }

    public static string FormatBytes(double bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        if (Math.Abs(bytes) >= gb * 100) return $"{bytes / gb:0} GB";
        if (Math.Abs(bytes) >= gb) return string.Create(CultureInfo.InvariantCulture, $"{bytes / gb:0.0} GB");
        return $"{bytes / 1024 / 1024:0} MB";
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TemplateRegex();

    public static JsonNode Parse(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })!;
}
