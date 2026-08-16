namespace OrdoSort.Core.Tests;

/// <summary>No temp directory, no IDisposable, no cleanup retry loop —
/// PathIdentity is string work over Path.GetFullPath, which doesn't touch
/// the filesystem. That's the property worth protecting: these tests stay
/// fast and can't flake on a locked handle or a virus scanner.</summary>
public class PathIdentityTests
{
    [Fact]
    public void CanonicalResolvesDotAndDotDotSegments()
    {
        Assert.Equal(@"C:\jobs\a.pdf", PathIdentity.Canonical(@"C:\jobs\.\a.pdf"));
        Assert.Equal(@"C:\jobs\a.pdf", PathIdentity.Canonical(@"C:\jobs\2026\..\a.pdf"));
    }

    [Fact]
    public void CanonicalNormalisesForwardSlashesAndDoubledSeparators()
    {
        Assert.Equal(@"C:\jobs\a.pdf", PathIdentity.Canonical("C:/jobs/a.pdf"));
        Assert.Equal(@"C:\jobs\a.pdf", PathIdentity.Canonical(@"C:\jobs\\a.pdf"));
    }

    /// <summary>The one GetFullPath won't do on its own, and the reason a
    /// source-root list can otherwise hold one folder twice: a source root
    /// added as "C:\jobs" and again as "C:\jobs\" compares unequal without
    /// this.</summary>
    [Fact]
    public void CanonicalDropsATrailingSeparatorButLeavesARootAlone()
    {
        Assert.Equal(@"C:\jobs", PathIdentity.Canonical(@"C:\jobs\"));
        Assert.Equal(@"C:\", PathIdentity.Canonical(@"C:\"));
    }

    [Fact]
    public void CanonicalIsNullForAPathThatCannotBeOne()
    {
        Assert.Null(PathIdentity.Canonical("C:\\jobs\\bad\0name.pdf"));
        Assert.Null(PathIdentity.Canonical(""));
        Assert.Null(PathIdentity.Canonical("   "));
        Assert.Null(PathIdentity.Canonical(null));
    }

    [Theory]
    [InlineData(@"C:\jobs\scan.pdf", @"C:\jobs\SCAN.PDF")]      // the defect this exists for
    [InlineData(@"C:\jobs", @"C:\jobs\")]                        // trailing separator
    [InlineData(@"C:\jobs\scan.pdf", "C:/jobs/scan.pdf")]        // separator style
    [InlineData(@"C:\jobs\scan.pdf", @"C:\jobs\2026\..\scan.pdf")]
    public void SameSeesThroughSpellingDifferences(string a, string b) =>
        Assert.True(PathIdentity.Same(a, b));

    [Fact]
    public void SameSaysNoToGenuinelyDifferentFiles()
    {
        Assert.False(PathIdentity.Same(@"C:\jobs\a.pdf", @"C:\jobs\b.pdf"));
        Assert.False(PathIdentity.Same(@"C:\jobs\a.pdf", @"D:\jobs\a.pdf"));
    }

    /// <summary>Falls back to the raw compare BulkRename.SameFile used to do
    /// for every path, so an unusable path stays equal to itself rather than
    /// becoming equal to nothing — the safer answer where it guards a move.</summary>
    [Fact]
    public void SameFallsBackToARawCompareWhenNeitherSideCanBeCanonicalised()
    {
        Assert.True(PathIdentity.Same("C:\\bad\0name.pdf", "C:\\bad\0name.pdf"));
        Assert.True(PathIdentity.Same("C:\\BAD\0name.pdf", "C:\\bad\0name.pdf"));
        Assert.False(PathIdentity.Same("C:\\bad\0one.pdf", "C:\\bad\0two.pdf"));
    }

    [Fact]
    public void NeitherMemberEverThrows()
    {
        var nasty = new[] { null, "", "   ", "\0", "C:\\a\0b", "::", "\\\\", "|", new string('x', 400) };
        foreach (var p in nasty)
        {
            var ex = Record.Exception(() => PathIdentity.Canonical(p));
            Assert.Null(ex);
            Assert.Null(Record.Exception(() => PathIdentity.Same(p, "C:\\jobs\\a.pdf")));
        }
    }
}
