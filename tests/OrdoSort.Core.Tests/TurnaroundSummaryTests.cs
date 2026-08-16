using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The business-day counter is the workbook's TAT column: the
/// number of weekdays in [docDate, uploadDate) — numpy busday_count
/// semantics, verified against 23,565 of the 23,672 live rows before this
/// was built (spec decision 3). All fixture dates are synthetic; 2026-07-06
/// is a Monday.</summary>
public class TurnaroundSummaryTests
{
    private static readonly DateOnly Wed = new(2026, 7, 1);
    private static readonly DateOnly Thu = new(2026, 7, 2);
    private static readonly DateOnly Fri = new(2026, 7, 3);
    private static readonly DateOnly Sat = new(2026, 7, 4);
    private static readonly DateOnly Sun = new(2026, 7, 5);
    private static readonly DateOnly Mon = new(2026, 7, 6);

    [Fact]
    public void SameDayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Mon, Mon));

    [Fact]
    public void NextWeekdayIsOne() =>
        Assert.Equal(1, TurnaroundSummary.BusinessDaysBetween(Wed, Thu));

    [Fact]
    public void FridayToMondaySkipsTheWeekend() =>
        Assert.Equal(1, TurnaroundSummary.BusinessDaysBetween(Fri, Mon));

    [Fact]
    public void SaturdayToMondayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Sat, Mon));

    [Fact]
    public void SundayToMondayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Sun, Mon));

    [Fact]
    public void AFullWeekIsFive() =>
        Assert.Equal(5, TurnaroundSummary.BusinessDaysBetween(Mon, Mon.AddDays(7)));

    [Fact]
    public void ReversedDatesCountNegative() =>
        Assert.Equal(-1, TurnaroundSummary.BusinessDaysBetween(Mon, Fri));

    [Theory]
    [InlineData(0, TurnaroundSummary.Bucket.SameDay)]
    [InlineData(1, TurnaroundSummary.Bucket.OneDay)]
    [InlineData(2, TurnaroundSummary.Bucket.TwoDays)]
    [InlineData(3, TurnaroundSummary.Bucket.ThreePlus)]
    [InlineData(9, TurnaroundSummary.Bucket.ThreePlus)]
    public void BucketsMatchTheWorkbookColumns(int days, TurnaroundSummary.Bucket expected) =>
        Assert.Equal(expected, TurnaroundSummary.Classify(days));

    [Fact]
    public void BucketCountsComputeRollupAndPercentages()
    {
        var counts = new TurnaroundSummary.BucketCounts(SameDay: 3, OneDay: 2, TwoDays: 2, ThreePlus: 2);
        Assert.Equal(9, counts.Total);
        Assert.Equal(5, counts.ZeroToOne);
        Assert.Equal(55.56, counts.ZeroToOnePercent, 2);
        Assert.Equal(22.22, counts.TwoPercent, 2);
        Assert.Equal(22.22, counts.ThreePlusPercent, 2);
    }

    [Fact]
    public void EmptyBucketCountsHaveZeroPercentagesNotNaN()
    {
        var counts = new TurnaroundSummary.BucketCounts(0, 0, 0, 0);
        Assert.Equal(0, counts.Total);
        Assert.Equal(0.0, counts.ZeroToOnePercent);
    }
}
