using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

/// <summary>box-labels.json: the one section kept in its own file. Unknown
/// top-level keys round-trip (same contract as config.json itself).</summary>
public sealed class BoxLabelsDoc
{
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonPropertyName("date_style")] public string DateStyle { get; set; } = BoxLabels.DateStyleBars;
    [JsonPropertyName("label_layout")] public string LabelLayout { get; set; } = BoxLabels.LayoutStandard;
    [JsonPropertyName("leading_zeros")] public bool LeadingZeros { get; set; } = true;

    /// <summary>The three label-style keys as one value, normalised: every
    /// station reads the same style from here, and writes it back the same way.</summary>
    [JsonIgnore]
    public BoxLabels.LabelStyle Style
    {
        get => new BoxLabels.LabelStyle(LabelLayout, LeadingZeros, DateStyle).Normalized();
        set
        {
            var s = value.Normalized();
            LabelLayout = s.Layout;
            LeadingZeros = s.LeadingZeros;
            DateStyle = s.DateStyle;
        }
    }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}
