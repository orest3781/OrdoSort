using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

/// <summary>The four per-section config files. Each is a JSON object with a
/// single list key; unknown top-level keys round-trip (same contract as
/// config.json itself).</summary>
public sealed class DestinationsDoc
{
    [JsonPropertyName("routes")] public List<Route> Routes { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class MonitoredFoldersDoc
{
    [JsonPropertyName("watch_folders")] public List<WatchFolder> WatchFolders { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class AlertsDoc
{
    [JsonPropertyName("alert_texts")] public List<string> AlertTexts { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class BoxLabelsDoc
{
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonPropertyName("date_style")] public string DateStyle { get; set; } = BoxLabels.DateStyleBars;
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}
