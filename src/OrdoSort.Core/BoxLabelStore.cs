using System.Text.Json;

namespace OrdoSort.Core;

/// <summary>All mutations of box-labels.json go through here. The file can
/// live on a share with several stations printing: an exclusive open with
/// retries (the busy_timeout philosophy) makes counter advances atomic.</summary>
public static class BoxLabelStore
{
    private const int RetryDelayMs = 150;
    private const int DefaultMaxWaitMs = 5000;

    // Win32 HRESULTs for "another process has this open" — the only cases
    // worth burning the retry budget on. Everything else (disk full, bad
    // network path, permission changes mid-run, ...) will not resolve itself
    // in 150ms and should fail fast with its own message instead.
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    internal static bool IsContention(IOException ex) =>
        ex.HResult is ErrorSharingViolation or ErrorLockViolation;

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
            catch (FileNotFoundException)
            {
                // deletion race: the Exists check above passed, but the file
                // is gone by the time the FileStream opened — same meaning as
                // never having existed, not a busy-file retry case.
                return new BoxLabelsDoc();
            }
            catch (IOException ex) when (IsContention(ex) && sw.ElapsedMilliseconds + RetryDelayMs <= maxWaitMs)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException ex)
            {
                throw new ConfigException(IsContention(ex)
                    ? $"another station is using the box-labels file — try again ({fullPath})"
                    : $"box-labels file error: {ex.Message} ({fullPath})");
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
    /// callback throws before the write completes. Must not be called
    /// reentrantly on the same path from within its own callback — the
    /// exclusive handle is still open, so a nested call would just spin
    /// until its own outer call's retry budget expires.</summary>
    public static T Mutate<T>(string fullPath, Func<BoxLabelsDoc, T> mutate,
        int maxWaitMs = DefaultMaxWaitMs)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(fullPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsContention(ex) && sw.ElapsedMilliseconds + RetryDelayMs <= maxWaitMs)
            {
                Thread.Sleep(RetryDelayMs);
                continue;
            }
            catch (IOException ex)
            {
                throw new ConfigException(IsContention(ex)
                    ? $"another station is using the box-labels file — try again ({fullPath})"
                    : $"box-labels file error: {ex.Message} ({fullPath})");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ConfigException($"box-labels file not accessible: {ex.Message} ({fullPath})");
            }

            using (fs)
            {
                string text;
                using (var reader = new StreamReader(fs, leaveOpen: true))
                    text = reader.ReadToEnd();

                BoxLabelsDoc doc;
                try
                {
                    doc = text.Trim().Length == 0
                        ? new BoxLabelsDoc()
                        : JsonSerializer.Deserialize<BoxLabelsDoc>(text, Opts) ?? new BoxLabelsDoc();
                }
                catch (JsonException ex)
                {
                    throw new ConfigException($"Config file {fullPath} is not valid JSON: {ex.Message}");
                }
                doc.LabelClients ??= new();
                doc.Extras ??= new();
                doc.DateStyle = BoxLabels.NormalizeDateStyle(doc.DateStyle);

                var result = mutate(doc);   // outside every classification catch

                fs.Seek(0, SeekOrigin.Begin);
                fs.SetLength(0);
                using (var writer = new StreamWriter(fs, leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(doc, Opts) + "\n");
                    writer.Flush();
                }
                return result;
            }
        }
    }
}
