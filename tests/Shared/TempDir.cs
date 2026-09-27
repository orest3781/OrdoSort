namespace OrdoSort.TestSupport;

/// <summary>A private temp folder for one test's files, deleted when the test
/// ends. The delete retries briefly: a file a background probe or the
/// indexer still holds for a moment must not fail a test that already
/// passed (docs/testing.md).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ordotest_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>Creates <paramref name="name"/> (folders in it included) with
    /// <paramref name="content"/> and returns its full path.</summary>
    public string File(string name, string content = "x")
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates the folder <paramref name="name"/> and returns its path.</summary>
    public string Dir(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => DeleteWithRetry(Path);

    /// <summary>Deletes <paramref name="path"/> and everything in it, retrying
    /// for about half a second. A folder that still cannot be deleted is left
    /// for the per-run temp sweep rather than failing the test.</summary>
    public static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
