namespace OrdoSort.Core.Tests;

public class NaturalSortTests
{
    private static int Cmp(string? a, string? b) => NaturalSort.Instance.Compare(a, b);

    [Fact]
    public void ShortDigitRunSortsBeforeLongerOne() =>
        Assert.True(Cmp("file2", "file10") < 0);

    [Fact]
    public void ReverseComparisonIsConsistent() =>
        Assert.True(Cmp("file10", "file2") > 0);

    [Fact]
    public void PlainOrdinalWouldGetThisWrongRegressionGuard()
    {
        // Ordinal string comparison puts "file10" before "file2" because
        // '1' < '2' character-by-character — the exact bug NaturalSort
        // exists to avoid.
        Assert.True(string.CompareOrdinal("file10", "file2") < 0);
        Assert.True(Cmp("file10", "file2") > 0);
    }

    [Fact]
    public void CaseInsensitiveLettersCompareEqual()
    {
        Assert.Equal(0, Cmp("A", "a"));
        Assert.Equal(0, Cmp("FILE", "file"));
    }

    [Fact]
    public void MixedDigitAndLetterRunsCompareRunByRun()
    {
        Assert.True(Cmp("a1b2", "a1b10") < 0);
        Assert.True(Cmp("item9x", "item10x") < 0);
        Assert.True(Cmp("v2.0", "v10.0") < 0);
    }

    [Fact]
    public void EqualStringsCompareToZero()
    {
        Assert.Equal(0, Cmp("same.pdf", "same.pdf"));
        Assert.Equal(0, Cmp("", ""));
    }

    [Fact]
    public void NullSortsBeforeNonNull()
    {
        Assert.True(Cmp(null, "a") < 0);
        Assert.True(Cmp("a", null) > 0);
        Assert.Equal(0, Cmp(null, null));
    }

    [Fact]
    public void VeryLongDigitRunsCompareByLengthThenLexicallyWithoutOverflow()
    {
        // long.MaxValue is 9223372036854775807 — 19 digits. A 24-25 digit
        // run would overflow long.Parse outright, so NaturalSort must never
        // attempt to parse a run into a number at all.
        var digits24 = new string('1', 24);
        var sameLengthSmaller = "doc" + digits24 + "5.pdf";
        var sameLengthBigger = "doc" + digits24 + "9.pdf";
        Assert.True(Cmp(sameLengthSmaller, sameLengthBigger) < 0);

        // 25 nines is the biggest possible 25-digit number; a 26-digit
        // number (even one starting with 1 and trailing zeros) is bigger —
        // length must win over a naive lexical compare here.
        var run25Nines = "doc" + new string('9', 25) + ".pdf";
        var run26OneAndZeros = "doc1" + new string('0', 25) + ".pdf";
        Assert.True(Cmp(run25Nines, run26OneAndZeros) < 0);
    }

    [Fact]
    public void LeadingZerosDoNotChangeNumericOrdering() =>
        Assert.Equal(0, Cmp("img007.png", "img7.png"));
}
