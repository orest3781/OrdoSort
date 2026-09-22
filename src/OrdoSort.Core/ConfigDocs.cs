using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

// The per-section config files. Each is a JSON object with a single list
// key; unknown top-level keys round-trip (same contract as config.json
// itself). Only box-labels.json is still written. The other three are the
// legacy split-file layout (2026-07 to 2026-09), read once by Config.Load to
// fold their sections back into config.json.

/// <summary>Legacy destinations.json: read-only, for migration.</summary>
public sealed class DestinationsDoc
{
    [JsonPropertyName("routes")] public List<Route> Routes { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>Legacy monitored-folders.json: read-only, for migration.</summary>
public sealed class MonitoredFoldersDoc
{
    [JsonPropertyName("watch_folders")] public List<WatchFolder> WatchFolders { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>Legacy alerts.json: read-only, for migration.</summary>
public sealed class AlertsDoc
{
    [JsonPropertyName("alert_texts")] public List<string> AlertTexts { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>box-labels.json: the label clients and their counters, written
/// only through BoxLabelStore's exclusive lock.</summary>
public sealed class BoxLabelsDoc
{
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonPropertyName("date_style")] public string DateStyle { get; set; } = BoxLabels.DateStyleBars;
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}
