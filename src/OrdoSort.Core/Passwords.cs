namespace OrdoSort.Core;

/// <summary>What a locked item wants from the person: which item, where it
/// lives (null for a loose file or an archive itself; the archive's name for
/// an entry inside one), and whether the previous answer was tried and
/// failed — the prompt shows "That password didn't open it" on exactly that
/// flag.</summary>
public sealed record PasswordRequest(string Item, string? Inside, bool PreviousAttemptFailed);

/// <summary>What one attempt with one password came back as. WrongPassword
/// moves the loop on to the next candidate, or to the prompt; Unreadable
/// stops it — a damaged file is not a password problem, and asking again
/// would be a lie.</summary>
public enum PasswordTry { Opened, WrongPassword, Unreadable }

/// <summary>Status "opened": <see cref="Password"/> is the one that worked,
/// and <see cref="MatchedIndex"/> its position among the candidates — null
/// when it was typed at the prompt instead. "needs_password": nothing worked
/// and the prompt was skipped, or there was no prompt to ask. "unreadable":
/// an attempt failed for a reason no password can fix.</summary>
public sealed record PasswordResolution(string Status, string? Password = null, int? MatchedIndex = null);

/// <summary>The candidates-then-ask loop, written once for every locked
/// thing the app opens — a zip, a loose PDF, a PDF inside a zip. Core
/// remembers nothing: the caller owns the candidate list and the order it
/// comes in (the view models put what was typed in this window first, then
/// the Unlock tool's saved list), and this only walks it.</summary>
public static class Passwords
{
    /// <summary>Try every candidate in order, silently; only when none opens
    /// the item call <paramref name="ask"/>, and keep asking — with
    /// <see cref="PasswordRequest.PreviousAttemptFailed"/> set from the
    /// second time on — until an answer works or the answer is null or
    /// empty (a skip). <paramref name="tryWith"/> is the only thing that
    /// touches the item.</summary>
    public static PasswordResolution Resolve(
        IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask,
        string item, string? inside,
        Func<string, PasswordTry> tryWith)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            switch (tryWith(candidates[i]))
            {
                case PasswordTry.Opened: return new("opened", candidates[i], i);
                case PasswordTry.Unreadable: return new("unreadable");
            }
        }

        if (ask is null) return new("needs_password");

        var previousFailed = false;
        while (true)
        {
            var answer = ask(new PasswordRequest(item, inside, previousFailed));
            if (string.IsNullOrEmpty(answer)) return new("needs_password");
            switch (tryWith(answer))
            {
                case PasswordTry.Opened: return new("opened", answer, null);
                case PasswordTry.Unreadable: return new("unreadable");
            }
            previousFailed = true;
        }
    }
}
