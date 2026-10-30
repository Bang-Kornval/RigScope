using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RigScope.Core.Snapshot;

public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(SystemSnapshot s) => JsonSerializer.Serialize(s, Options);

    public static SystemSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<SystemSnapshot>(json, Options) ?? throw new JsonException("Empty snapshot.");

    /// <summary>The snapshot as a JSON tree, including computed values (gpu, display, freePercent…). Rules evaluate against this.</summary>
    public static JsonNode ToNode(SystemSnapshot s) => JsonSerializer.SerializeToNode(s, Options)!;
}
