namespace OrdoSort.Core.Tests;

public class MergeTypesTests
{
    [Theory]
    [InlineData("pdf", "pdf")] [InlineData("docx", "word")] [InlineData("rtf", "word")]
    [InlineData("csv", "excel")] [InlineData("xlsx", "excel")] [InlineData("pptx", "powerpoint")]
    [InlineData("TIF", "images")] [InlineData("json", "text")]
    public void EveryHandledExtensionKnowsItsGroup(string extension, string group) =>
        Assert.Equal(group, MergeTypes.GroupOf(extension));

    [Theory]
    [InlineData("exe")] [InlineData("mp4")] [InlineData("")]
    public void AForeignTypeHasNoGroup(string extension) =>
        Assert.Null(MergeTypes.GroupOf(extension));

    [Fact]
    public void NoExtensionBelongsToTwoGroups()
    {
        var all = MergeTypes.AllGroups.SelectMany(MergeTypes.ExtensionsOf).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // NothingStoredMeansEverythingIsOn and EverythingOffIsDistinguishableFrom
    // NothingStored together pin down the sentinel decision: null/blank
    // (nothing ever saved) and MergeTypes.NoneStored (the user turned every
    // group off on purpose) must load as two different things, or unticking
    // every box would come back fully re-enabled on the next launch.

    [Fact]
    public void NothingStoredMeansEverythingIsOn() =>
        Assert.Equal(MergeTypes.AllGroups.OrderBy(g => g),
                     MergeTypes.Load(null).OrderBy(g => g));

    [Fact]
    public void TheEnabledSetSurvivesARoundTrip()
    {
        var chosen = new[] { MergeTypes.Pdf, MergeTypes.Images };
        Assert.Equal(chosen.OrderBy(g => g),
                     MergeTypes.Load(MergeTypes.Save(chosen)).OrderBy(g => g));
    }

    [Fact]
    public void AGroupNameFromALaterVersionIsIgnoredRatherThanBreakingTheLoad() =>
        Assert.Equal(new[] { "pdf" }, MergeTypes.Load("pdf,hologram"));

    [Fact]
    public void EverythingOffIsDistinguishableFromNothingStored()
    {
        // Save of an empty set writes the "none" sentinel, never "" — and
        // Load reads that sentinel back as empty, distinct from null/blank
        // (Load(null), covered above), which loads as everything on.
        var none = MergeTypes.Save(Array.Empty<string>());
        Assert.Equal(MergeTypes.NoneStored, none);
        Assert.Empty(MergeTypes.Load(none));
        Assert.NotEmpty(MergeTypes.Load(null));
    }
}
