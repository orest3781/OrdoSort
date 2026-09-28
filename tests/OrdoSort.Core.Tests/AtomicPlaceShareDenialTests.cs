using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>PR #6 test gap: on a share, File.Replace is refused and the save
/// falls back to a rename-over. A denial that is only momentary (the old file
/// still held for an instant) makes the rename fail too; that must be retried
/// like any other brief failure, not reported as "settings not saved".
/// Joins the AtomicPlace collection: it sets that class's static seams.</summary>
[Collection(AtomicPlaceTests.Name)]
public sealed class AtomicPlaceShareDenialTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        AtomicPlace.BeforeAttempt = null;
        AtomicPlace.Sleep = (_, delayMs) => Thread.Sleep(delayMs);
        AtomicPlace.ReplaceFile = (tmp, dest) => File.Replace(tmp, dest, null);
        _dir.Dispose();
    }

    [Fact]
    public void ABriefHoldOnTheOldFileDuringTheFallbackRenameIsRetried()
    {
        var dest = Path.Combine(_dir.Path, "config.json");
        File.WriteAllText(dest, "old");
        var realReplace = AtomicPlace.ReplaceFile;
        AtomicPlace.ReplaceFile = (tmp, target) =>
        {
            if (target != dest) { realReplace(tmp, target); return; }   // process-wide seam
            throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
        };
        AtomicPlace.Sleep = (_, _) => { };   // retries without real waiting
        FileStream? held = null;
        AtomicPlace.BeforeAttempt = (target, attempt) =>
        {
            if (target != dest) return;
            if (attempt == 0) held = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None);
            else { held?.Dispose(); held = null; }
        };
        try
        {
            Assert.True(AtomicPlace.TryReplace(dest, tmp => File.WriteAllText(tmp, "new"), out var error), error);
            Assert.Equal("new", File.ReadAllText(dest));
        }
        finally { held?.Dispose(); }
    }
}
