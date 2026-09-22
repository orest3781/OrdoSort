using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

/// <summary>box-labels.json: the one section kept in its own file. Unknown
/// top-level keys round-trip (same contract as config.json itself).</summary>
public sealed class BoxLabelsDoc
{
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonPropertyName("date_style")] public string DateStyle { get; set; } = BoxLabels.DateStyleBars;
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}
