namespace OrdoSort.Core.Tests;

/// <summary>The candidates-then-ask loop every locked operation shares.
/// Nothing here touches a zip or a PDF: <c>tryWith</c> is scripted, so each
/// fact is about the ORDER things are tried in and WHEN the person is asked,
/// which is the whole contract.</summary>
public class PasswordsTests
{
    private static Func<string, PasswordTry> Opens(string right) =>
        pw => pw == right ? PasswordTry.Opened : PasswordTry.WrongPassword;

    [Fact]
    public void CandidatesAreTriedInOrderAndTheAskIsNeverReachedWhenOneWorks()
    {
        var tried = new List<string>();
        var asked = 0;

        var r = Passwords.Resolve(new[] { "a", "b", "c" }, _ => { asked++; return "typed"; },
            "doc.pdf", null, pw => { tried.Add(pw); return pw == "b" ? PasswordTry.Opened : PasswordTry.WrongPassword; });

        Assert.Equal("opened", r.Status);
        Assert.Equal("b", r.Password);
        Assert.Equal(1, r.MatchedIndex);
        Assert.Equal(new[] { "a", "b" }, tried);   // c was never needed
        Assert.Equal(0, asked);
    }

    [Fact]
    public void WhenNoCandidateWorksThePersonIsAskedAndATypedAnswerHasNoIndex()
    {
        var requests = new List<PasswordRequest>();

        var r = Passwords.Resolve(new[] { "a" }, req => { requests.Add(req); return "typed"; },
            "report.pdf", "Batch 12.zip", Opens("typed"));

        Assert.Equal("opened", r.Status);
        Assert.Equal("typed", r.Password);
        Assert.Null(r.MatchedIndex);
        var req = Assert.Single(requests);
        Assert.Equal("report.pdf", req.Item);
        Assert.Equal("Batch 12.zip", req.Inside);
        Assert.False(req.PreviousAttemptFailed);
    }

    [Fact]
    public void AWrongTypedAnswerIsAskedAgainWithTheFailedFlagUntilOneWorks()
    {
        var answers = new Queue<string?>(new[] { "bad", "worse", "right" });
        var flags = new List<bool>();

        var r = Passwords.Resolve(Array.Empty<string>(), req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); },
            "doc.pdf", null, Opens("right"));

        Assert.Equal("opened", r.Status);
        Assert.Equal("right", r.Password);
        Assert.Equal(new[] { false, true, true }, flags);
    }

    [Fact]
    public void SkippingThePromptIsNeedsPassword()
    {
        var r = Passwords.Resolve(new[] { "a" }, _ => null, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.Password);
    }

    [Fact]
    public void AnEmptyAnswerCountsAsASkip()
    {
        var asked = 0;
        var r = Passwords.Resolve(Array.Empty<string>(), _ => { asked++; return ""; }, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
        Assert.Equal(1, asked);   // not re-asked forever
    }

    [Fact]
    public void WithNoAskAtAllAnUnopenedItemIsNeedsPassword()
    {
        var r = Passwords.Resolve(new[] { "a", "b" }, ask: null, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
    }

    /// <summary>A damaged file is not a password problem. The first
    /// Unreadable stops everything — later candidates are not tried and the
    /// person is not asked — because asking for a password that cannot help
    /// would be a lie.</summary>
    [Fact]
    public void UnreadableStopsTheLoopWithoutAsking()
    {
        var tried = new List<string>();
        var asked = 0;

        var r = Passwords.Resolve(new[] { "a", "b" }, _ => { asked++; return "typed"; },
            "doc.pdf", null, pw => { tried.Add(pw); return PasswordTry.Unreadable; });

        Assert.Equal("unreadable", r.Status);
        Assert.Equal(new[] { "a" }, tried);
        Assert.Equal(0, asked);
    }
}
