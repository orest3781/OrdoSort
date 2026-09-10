using System.Text.Json;

namespace BoxLabelsApp.Services;

/// <summary>What a file the user picked actually looks like.</summary>
public enum LabelsFileKind
{
    /// <summary>Nothing there yet. Legitimate for the first station ever to
    /// set this up, and also exactly how someone accidentally starts a private
    /// box-number list — so it is confirmed, never assumed.</summary>
    Missing,

    /// <summary>"{}" — hand-created and not yet used. Usable.</summary>
    EmptyObject,

    /// <summary>Carries a client list. This is the shared store.</summary>
    Store,

    /// <summary>Valid JSON, an object, but no client list — most likely a
    /// different program's settings file.</summary>
    NotAStore,

    /// <summary>Not JSON, not an object, or empty. An empty file is included
    /// deliberately: BoxLabelStore treats a pre-existing zero-byte store as a
    /// save that was interrupted and refuses to issue numbers from it, so
    /// accepting one here would only defer the same error.</summary>
    Unreadable,
}

/// <summary>Decides whether a chosen file is really the shared box-labels
/// store, before anything is written to it.
///
/// This exists because the store is forgiving in a way that is dangerous here.
/// BoxLabelsDoc carries a [JsonExtensionData] bag, so pointing the app at some
/// other JSON file — OrdoSort's own config.json, say — parses fine, shows an
/// empty client list, and writes label_clients and date_style into that file
/// the moment anything is saved. Measured, not assumed: doing exactly that to
/// a copy of demo-full/config.json took it from 1688 to 1830 bytes.
///
/// BoxLabelStore.Read cannot answer this question — it deserialises into a
/// doc whose LabelClients defaults to an empty list, so an ABSENT client list
/// and an empty one are indistinguishable by the time it returns. Hence the
/// raw JSON inspection here.</summary>
public static class LabelsFileCheck
{
    /// <summary>Classify without modifying anything.</summary>
    public static LabelsFileKind Classify(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return LabelsFileKind.Missing;
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LabelsFileKind.Unreadable;
        }

        if (text.Trim().Length == 0) return LabelsFileKind.Unreadable;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return LabelsFileKind.Unreadable;
            if (doc.RootElement.TryGetProperty("label_clients", out _)) return LabelsFileKind.Store;
            return doc.RootElement.EnumerateObject().Any()
                ? LabelsFileKind.NotAStore
                : LabelsFileKind.EmptyObject;
        }
        catch (JsonException)
        {
            return LabelsFileKind.Unreadable;
        }
    }
}
