using System.Windows;

namespace OrdoSort.Smoke.E2E;

/// <summary>One recorded check and its outcome. Assertions are data, not
/// exceptions, so a scenario reports everything it checked — the failing
/// ones alongside the passing ones — which is what makes the evidence
/// report readable rather than a stack trace.</summary>
public sealed record Assertion(string Description, bool Passed, string? Detail = null);

/// <summary>One end-to-end scenario. Kind is "clean" (proves the surface
/// works) or "awkward" (proves it behaves under an input that breaks naive
/// code); the report asserts every surface has at least one of each.</summary>
public sealed record Scenario(string Surface, string Name, string Kind, Action<ScenarioContext> Run);

/// <summary>What a scenario is handed: its isolated fixture, its dialog
/// answers, and the recorder it reports through.</summary>
public sealed class ScenarioContext
{
    private readonly List<Assertion> _assertions = new();

    public ScenarioContext(Fixture fx, ScriptedDialogs dialogs)
    {
        Fx = fx;
        Dialogs = dialogs;
    }

    public Fixture Fx { get; }
    public ScriptedDialogs Dialogs { get; }
    public IReadOnlyList<Assertion> Assertions => _assertions;
    public Window? Captured { get; private set; }

    /// <summary>Record a check. Never throws: one false assertion must not
    /// stop the rest of the scenario from reporting.</summary>
    public void Check(string description, bool condition, string? detail = null) =>
        _assertions.Add(new Assertion(description, condition, condition ? null : detail));

    public void FileExists(string path) =>
        Check($"file exists: {Rel(path)}", File.Exists(path), "not found");

    public void FileMissing(string path) =>
        Check($"file absent: {Rel(path)}", !File.Exists(path), "unexpectedly present");

    /// <summary>The never-overwrite guarantee: content identical, not merely
    /// present.</summary>
    public void BytesUnchanged(string path, byte[] before, string description)
    {
        if (!File.Exists(path)) { Check(description, false, "file is gone"); return; }
        var now = File.ReadAllBytes(path);
        Check(description, now.SequenceEqual(before),
            $"content changed ({before.Length} → {now.Length} bytes)");
    }

    /// <summary>Every path currently under the fixture (and one level above
    /// it, so an escape out of the root is visible). Take this before the
    /// act, hand it to NothingNewOutside after.</summary>
    public string[] Snapshot() => ScanBase() is { } b
        ? Directory.GetFileSystemEntries(b, "*", SearchOption.AllDirectories)
        : Array.Empty<string>();

    /// <summary>Nothing new appeared anywhere except inside
    /// <paramref name="allowedDir"/>. This is the zip-slip assertion, and it
    /// is deliberately not "the file I predicted is absent" — an entry that
    /// escapes to somewhere unanticipated is caught just the same.</summary>
    public void NothingNewOutside(string allowedDir, string[] before, string description)
    {
        var allowed = Path.GetFullPath(allowedDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var added = Snapshot()
            .Except(before, StringComparer.OrdinalIgnoreCase)
            .Where(p => !IsUnder(p, allowed))
            .ToList();
        Check(description, added.Count == 0,
            added.Count == 0 ? null : "appeared: " + string.Join(", ", added.Select(Rel)));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="allowed"/>
    /// itself or something under it. A plain StartsWith on the string would
    /// let a sibling with a shared name prefix — allowed "...\evil" against a
    /// stray "...\evil-extra.txt" — pass as "under allowed" when it plainly
    /// is not, which would hide a real escape. Requiring an exact match or a
    /// separator right after the prefix closes that gap.</summary>
    private static bool IsUnder(string path, string allowed)
    {
        var full = Path.GetFullPath(path);
        return full.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string? ScanBase()
    {
        var parent = Path.GetDirectoryName(Fx.Root);
        return Directory.Exists(parent) ? parent : (Directory.Exists(Fx.Root) ? Fx.Root : null);
    }

    /// <summary>Nominate the window this scenario is evidenced by. The
    /// runner rasterizes it after Run returns, while it is still open.</summary>
    public void Capture(Window win) => Captured = win;

    private string Rel(string path) =>
        path.StartsWith(Fx.Root, StringComparison.OrdinalIgnoreCase)
            ? path[(Fx.Root.Length + 1)..]
            : path;
}
