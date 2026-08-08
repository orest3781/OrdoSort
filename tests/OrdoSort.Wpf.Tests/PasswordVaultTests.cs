using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Portable-saved-passwords (2026-08-08): Protect/Reveal's own
/// round-trip and pass-through behaviour did NOT reverse — the app simply
/// stopped calling Protect on save (see UnlockViewModelTests) and reversed
/// which direction the migration sweep converts (see the load-time-sweep
/// tests there too). Protect/Reveal themselves still have to do exactly
/// what they always did, because Reveal is the ONLY route by which a
/// password saved under the old DPAPI scheme is still usable at all. These
/// three tests are therefore unchanged from before this plan — kept here,
/// not deleted, specifically so a future reader doesn't mistake their
/// silence for "nobody checked."</summary>
public class PasswordVaultTests
{
    [Fact]
    public void ProtectRoundTrips()
    {
        var stored = PasswordVault.Protect("hunter2");
        Assert.True(PasswordVault.IsProtected(stored));
        Assert.StartsWith("dpapi:", stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.Equal("hunter2", PasswordVault.Reveal(stored));
    }

    [Fact]
    public void LegacyPlaintextPassesThrough()
    {
        Assert.False(PasswordVault.IsProtected("plain-old-password"));
        Assert.Equal("plain-old-password", PasswordVault.Reveal("plain-old-password"));
    }

    [Fact]
    public void CorruptProtectedValueRevealsEmptyNotCrash()
    {
        Assert.Equal("", PasswordVault.Reveal("dpapi:not-base64!!"));
        Assert.Equal("", PasswordVault.Reveal("dpapi:AAAA"));
    }

    // ------------------------------------------------------------ TryReveal
    // New for the migration (Task 1): Reveal alone can't tell "genuinely
    // empty" apart from "couldn't decrypt" — both come back as "". The
    // portable-passwords migration must never treat the second case as the
    // first and silently overwrite an undecryptable entry with nothing, so
    // it uses TryReveal, which reports the two cases distinctly.

    [Fact]
    public void TryRevealRoundTripsAProtectedValueAndReportsSuccess()
    {
        var stored = PasswordVault.Protect("hunter2");
        Assert.True(PasswordVault.TryReveal(stored, out var plain));
        Assert.Equal("hunter2", plain);
    }

    [Fact]
    public void TryRevealPassesPlaintextThroughAndReportsSuccess()
    {
        Assert.True(PasswordVault.TryReveal("plain-old-password", out var plain));
        Assert.Equal("plain-old-password", plain);
    }

    [Fact]
    public void TryRevealReportsFailureForAnUndecryptableValueInsteadOfReturningEmpty()
    {
        // This is the exact distinction Reveal cannot make: both of these
        // return "" from Reveal, but neither is a genuinely empty password —
        // TryReveal must say so via its return value, not its out parameter.
        Assert.False(PasswordVault.TryReveal("dpapi:not-base64!!", out var plain1));
        Assert.Equal("", plain1);
        Assert.False(PasswordVault.TryReveal("dpapi:AAAA", out var plain2));
        Assert.Equal("", plain2);
    }
}
