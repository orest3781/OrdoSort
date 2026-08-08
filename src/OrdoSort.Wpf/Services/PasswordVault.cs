using System.Security.Cryptography;
using System.Text;

namespace OrdoSort.Wpf.Services;

/// <summary>Saved Unlock passwords are stored in PLAIN TEXT in the shared
/// config.json — the owner's deliberate choice (see
/// docs/superpowers/plans/2026-08-08-portable-saved-passwords.md: "The
/// decision, and why the obvious alternative was rejected"). DPAPI cannot
/// serve a config.json that lives on an SMB share and is read by several
/// stations: <c>CurrentUser</c> scope binds a blob to one Windows account,
/// <c>LocalMachine</c> scope binds it to one PC (and exposes it to every
/// account on that PC) — neither travels. The share's own permissions are
/// the security boundary here; anyone who can read config.json can read the
/// saved passwords, and that is understood and accepted, not a bug.
///
/// A DPAPI-protected value from before this change (or a hand-rolled one)
/// still decrypts fine on the machine and account that created it — that is
/// the only route by which a password saved under the old scheme survives
/// the switch — and <see cref="UnlockViewModel"/> converts it to plaintext
/// the next time the saved-password list is touched (add, remove, or the
/// window's own load-time migration; see
/// UnlockViewModel.MigrateProtectedToPlaintext). An entry protected on a
/// DIFFERENT machine or account cannot be decrypted at all; that migration
/// leaves it exactly as it is rather than silently blanking it.</summary>
public static class PasswordVault
{
    private const string Prefix = "dpapi:";

    public static bool IsProtected(string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Still here for legacy values and for tests that need to
    /// construct one — nothing in the app calls this to store a NEW saved
    /// password anymore. Saved passwords are plaintext by design now (Task
    /// 1 of the plan above); this exists only so a pre-existing DPAPI blob
    /// can still be created (and, via <see cref="Reveal"/>/
    /// <see cref="TryReveal"/>, read back) at all.</summary>
    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    /// <summary>Plaintext passes through unchanged; a DPAPI-protected value
    /// decrypts if this is the machine+account that protected it, else "".
    /// "" is ambiguous between "genuinely empty" and "could not decrypt" —
    /// a caller that must not confuse the two (the portable-passwords
    /// migration) uses <see cref="TryReveal"/> instead, which never
    /// collapses that distinction away.</summary>
    public static string Reveal(string stored) => TryReveal(stored, out var plain) ? plain : "";

    /// <summary>Like <see cref="Reveal"/>, but reports success/failure
    /// instead of collapsing a decrypt failure into "". The migration that
    /// converts a protected entry to plaintext depends on this: an entry it
    /// cannot decrypt (protected on a different machine or account — the
    /// normal, expected case once passwords are meant to travel) must be
    /// left completely untouched, never quietly overwritten with an empty
    /// password.</summary>
    public static bool TryReveal(string stored, out string plain)
    {
        if (!IsProtected(stored)) { plain = stored; return true; }
        try
        {
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]), null,
                DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            plain = "";
            return false;
        }
    }
}
