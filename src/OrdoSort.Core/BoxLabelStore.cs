using System.Text;
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
            // 0 bytes means two very different things once the FileStream
            // opens below. A file THIS call just created (OpenOrCreate) is
            // first run. A file that already existed and is now empty is a
            // crash mid-write. Must be captured before the FileStream open,
            // since OpenOrCreate itself would otherwise bring the file into
            // existence and erase the distinction.
            var existedBefore = File.Exists(fullPath);
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
                    // 0 bytes means two very different things. A file this
                    // call just created is first run. A file that already
                    // existed and is now empty is a crash mid-write — and
                    // treating that as "no clients yet" is how the counters
                    // got wiped (2026-08-04 audit 2.2). Read has always
                    // thrown here; Mutate must agree, because Mutate is the
                    // one that writes back.
                    doc = text.Trim().Length == 0
                        ? (existedBefore
                            ? throw new ConfigException(
                                $"the box-labels file is empty, which means a previous save was " +
                                $"interrupted — restore it from a backup before claiming more " +
                                $"box numbers ({fullPath})")
                            : new BoxLabelsDoc())
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

                // Write first, truncate after. The old order (SetLength(0)
                // then write) put the file through a 0-byte state on every
                // save; a crash there is what the guard above now has to
                // refuse. This ordering removes the state instead of
                // detecting it. Not a substitute for the guard — an
                // already-damaged file can still arrive from an older build.
                var bytes = new UTF8Encoding(false).GetBytes(
                    JsonSerializer.Serialize(doc, Opts) + "\n");
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
                fs.SetLength(bytes.Length);
                return result;
            }
        }
    }
}
