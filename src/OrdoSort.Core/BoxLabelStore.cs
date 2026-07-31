using System.Text.Json;

namespace OrdoSort.Core;

/// <summary>All mutations of box-labels.json go through here. The file can
/// live on a share with several stations printing: an exclusive open with
/// retries (the busy_timeout philosophy) makes counter advances atomic.</summary>
public static class BoxLabelStore
{
    private const int RetryDelayMs = 150;
    private const int DefaultMaxWaitMs = 5000;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Non-exclusive read for display with retries. Missing file = no clients yet.
    /// If another station holds the file, retries at 150ms intervals within maxWaitMs.</summary>
    public static BoxLabelsDoc Read(string fullPath, int maxWaitMs = DefaultMaxWaitMs)
    {
        if (!File.Exists(fullPath)) return new BoxLabelsDoc();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var fs = new FileStream(fullPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                try
                {
                    return JsonSerializer.Deserialize<BoxLabelsDoc>(text, Opts)
                           ?? new BoxLabelsDoc();
                }
                catch (JsonException ex)
                {
                    throw new ConfigException($"Config file {fullPath} is not valid JSON: {ex.Message}");
                }
            }
            catch (IOException) when (sw.ElapsedMilliseconds + RetryDelayMs <= maxWaitMs)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                throw new ConfigException(
                    $"another station is using the box-labels file — try again ({fullPath})");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ConfigException(
                    $"box-labels file not accessible: {ex.Message} ({fullPath})");
            }
        }
    }

    /// <summary>Exclusive read-modify-write. The callback sees the FRESH
    /// on-disk doc (never a stale in-memory copy), mutates it, and its
    /// return value is handed back after the write lands. Callback exceptions
    /// propagate raw (not mislabeled as file errors); file is unchanged if
    /// callback throws before the write completes.</summary>
    public static T Mutate<T>(string fullPath, Func<BoxLabelsDoc, T> mutate,
        int maxWaitMs = DefaultMaxWaitMs)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var fs = new FileStream(fullPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);

                // Read and deserialize: fail fast on corruption (no retries for JSON errors)
                try
                {
                    using var reader = new StreamReader(fs, leaveOpen: true);
                    var text = reader.ReadToEnd();
                    var doc = text.Trim().Length == 0
                        ? new BoxLabelsDoc()
                        : JsonSerializer.Deserialize<BoxLabelsDoc>(text, Opts) ?? new BoxLabelsDoc();
                    doc.LabelClients ??= new();
                    doc.Extras ??= new();

                    // Callback executes OUTSIDE catch: exceptions propagate raw, file untouched
                    var result = mutate(doc);

                    // Write only if callback succeeded
                    fs.Seek(0, SeekOrigin.Begin);
                    fs.SetLength(0);
                    using var writer = new StreamWriter(fs);
                    writer.Write(JsonSerializer.Serialize(doc, Opts) + "\n");
                    writer.Flush();
                    return result;
                }
                catch (JsonException ex)
                {
                    throw new ConfigException($"Config file {fullPath} is not valid JSON: {ex.Message}");
                }
            }
            catch (IOException) when (sw.ElapsedMilliseconds + RetryDelayMs <= maxWaitMs)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                throw new ConfigException(
                    $"another station is using the box-labels file — try again ({fullPath})");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ConfigException(
                    $"box-labels file not accessible: {ex.Message} ({fullPath})");
            }
        }
    }
}
