using System.Globalization;
using System.Text;

namespace OrdoSort.Core.Tests;

/// <summary>ProductionReport is pure computation on top of SweptTable rows —
/// these tests build tables directly rather than round-tripping through
/// Csv/SweptTable.Load, since that plumbing is already pinned by CsvTests and
/// SweptTableTests.</summary>
public class ProductionReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prodrpttest_" + Guid.NewGuid());
    public ProductionReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SweptTable.Row Row(string sourceFile, params (string Key, string Value)[] cells) =>
        new(cells.ToDictionary(c => c.Key, c => c.Value), sourceFile);

    // ---- Group ----

    [Fact]
    public void GroupBySingleColumnSumsSingleColumnPerGroup()
    {
        var table = new SweptTable.Table(
            new[] { "SOURCE-FOLDER", "PDF-PAGE-COUNT" },
            new[]
            {
                Row("a.csv", ("SOURCE-FOLDER", "EMAILS_APPEAL"), ("PDF-PAGE-COUNT", "5")),
                Row("a.csv", ("SOURCE-FOLDER", "EMAILS_APPEAL"), ("PDF-PAGE-COUNT", "16")),
                Row("a.csv", ("SOURCE-FOLDER", "FAX_APPEAL"), ("PDF-PAGE-COUNT", "3")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, new[] { "SOURCE-FOLDER" }, new[] { "PDF-PAGE-COUNT" });

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "EMAILS_APPEAL" }, groups[0].Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(21.0, groups[0].Sums["PDF-PAGE-COUNT"]);
        Assert.Equal(new[] { "FAX_APPEAL" }, groups[1].Key);
        Assert.Equal(1, groups[1].Count);
        Assert.Equal(3.0, groups[1].Sums["PDF-PAGE-COUNT"]);
    }

    [Fact]
    public void GroupByCompositeKeySortsCategoryThenEmployee()
    {
        var table = new SweptTable.Table(
            new[] { "SOURCE-FOLDER", "Employee" },
            new[]
            {
                Row("a.csv", ("SOURCE-FOLDER", "B"), ("Employee", "Y")),
                Row("a.csv", ("SOURCE-FOLDER", "A"), ("Employee", "Z")),
                Row("a.csv", ("SOURCE-FOLDER", "A"), ("Employee", "X")),
                Row("a.csv", ("SOURCE-FOLDER", "B"), ("Employee", "X")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, new[] { "SOURCE-FOLDER", "Employee" }, Array.Empty<string>());

        Assert.Equal(4, groups.Count);
        Assert.Equal(new[] { "A", "X" }, groups[0].Key);
        Assert.Equal(new[] { "A", "Z" }, groups[1].Key);
        Assert.Equal(new[] { "B", "X" }, groups[2].Key);
        Assert.Equal(new[] { "B", "Y" }, groups[3].Key);
    }

    [Fact]
    public void GroupSumIgnoresNonNumericAndBlankCellsAndParsesThousandsSeparator()
    {
        var table = new SweptTable.Table(
            new[] { "Cat", "Count" },
            new[]
            {
                Row("a.csv", ("Cat", "X"), ("Count", "abc")),
                Row("a.csv", ("Cat", "X"), ("Count", "")),
                Row("a.csv", ("Cat", "X"), ("Count", "1,234.5")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, new[] { "Cat" }, new[] { "Count" });

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(1234.5, groups[0].Sums["Count"]);
    }

    [Fact]
    public void GroupKeysAreTrimmedButCaseDifferenceDoesNotMerge()
    {
        var table = new SweptTable.Table(
            new[] { "Cat" },
            new[]
            {
                Row("a.csv", ("Cat", " EMAILS_APPEAL ")),
                Row("a.csv", ("Cat", "EMAILS_APPEAL")),
                Row("a.csv", ("Cat", "emails_appeal")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, new[] { "Cat" }, Array.Empty<string>());

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "EMAILS_APPEAL" }, groups[0].Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(new[] { "emails_appeal" }, groups[1].Key);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void GroupWithEmptyGroupByColumnsGivesOneTotalsGroup()
    {
        var table = new SweptTable.Table(
            new[] { "Cat", "Count" },
            new[]
            {
                Row("a.csv", ("Cat", "X"), ("Count", "1")),
                Row("a.csv", ("Cat", "Y"), ("Count", "2")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, Array.Empty<string>(), new[] { "Count" });

        Assert.Single(groups);
        Assert.Empty(groups[0].Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(3.0, groups[0].Sums["Count"]);
    }

    [Fact]
    public void GroupByAStaleColumnNameNotInHeadersGivesASingleEmptyKeyPart()
    {
        var table = new SweptTable.Table(
            new[] { "Cat" },
            new[]
            {
                Row("a.csv", ("Cat", "X")),
                Row("a.csv", ("Cat", "Y")),
            },
            1, Array.Empty<string>());

        var groups = ProductionReport.Group(table, new[] { "NOPE" }, Array.Empty<string>());

        Assert.Single(groups);
        Assert.Equal(new[] { "" }, groups[0].Key);
        Assert.Equal(2, groups[0].Count);
    }

    // ---- WithDerived ----

    [Fact]
    public void WithDerivedEmployeeStripsDomainPrefix()
    {
        var table = new SweptTable.Table(
            new[] { "FILE-OWNER" },
            new[] { Row("a.csv", ("FILE-OWNER", @"ACME\user3")) },
            1, Array.Empty<string>());

        var derived = ProductionReport.WithDerived(table, "FILE-OWNER", "");

        Assert.Equal("user3", derived.Rows[0].Cells["Employee"]);
    }

    [Fact]
    public void WithDerivedEmployeeWithNoBackslashIsTrimmedAndUnchanged()
    {
        var table = new SweptTable.Table(
            new[] { "FILE-OWNER" },
            new[] { Row("a.csv", ("FILE-OWNER", " user1 ")) },
            1, Array.Empty<string>());

        var derived = ProductionReport.WithDerived(table, "FILE-OWNER", "");

        Assert.Equal("user1", derived.Rows[0].Cells["Employee"]);
    }

    [Fact]
    public void WithDerivedDateAndHourSplitFromADateTimeCell()
    {
        var table = new SweptTable.Table(
            new[] { "DATE-TIME" },
            new[] { Row("a.csv", ("DATE-TIME", "4/1/2025 7:55")) },
            1, Array.Empty<string>());

        var derived = ProductionReport.WithDerived(table, "", "DATE-TIME");

        Assert.Equal("2025-04-01", derived.Rows[0].Cells["Date"]);
        Assert.Equal("07", derived.Rows[0].Cells["Hour"]);
    }

    [Fact]
    public void WithDerivedGarbageDateTimeGivesBlankDateAndHour()
    {
        var table = new SweptTable.Table(
            new[] { "DATE-TIME" },
            new[] { Row("a.csv", ("DATE-TIME", "not a date")) },
            1, Array.Empty<string>());

        var derived = ProductionReport.WithDerived(table, "", "DATE-TIME");

        Assert.Equal("", derived.Rows[0].Cells["Date"]);
        Assert.Equal("", derived.Rows[0].Cells["Hour"]);
    }

    [Fact]
    public void WithDerivedEmployeeCollisionWithARealHeaderLandsUnderDerivedSuffix()
    {
        var table = new SweptTable.Table(
            new[] { "FILE-OWNER", "Employee" },
            new[] { Row("a.csv", ("FILE-OWNER", @"ACME\user3"), ("Employee", "Real Value")) },
            1, Array.Empty<string>());

        var derived = ProductionReport.WithDerived(table, "FILE-OWNER", "");

        Assert.Contains("Employee (derived)", derived.Headers);
        Assert.Equal("user3", derived.Rows[0].Cells["Employee (derived)"]);
        Assert.Equal("Real Value", derived.Rows[0].Cells["Employee"]);
    }

    [Fact]
    public void WithDerivedLeavesTheOriginalTableUntouched()
    {
        var originalHeaders = new[] { "FILE-OWNER", "DATE-TIME" };
        var originalCells = new Dictionary<string, string>
        {
            ["FILE-OWNER"] = @"ACME\user3",
            ["DATE-TIME"] = "4/1/2025 7:55",
        };
        var table = new SweptTable.Table(originalHeaders, new[] { new SweptTable.Row(originalCells, "a.csv") },
            1, Array.Empty<string>());

        ProductionReport.WithDerived(table, "FILE-OWNER", "DATE-TIME");

        Assert.Same(originalHeaders, table.Headers);
        Assert.Equal(2, table.Headers.Count);
        Assert.Same(originalCells, table.Rows[0].Cells);
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.False(table.Rows[0].Cells.ContainsKey("Employee"));
        Assert.False(table.Rows[0].Cells.ContainsKey("Date"));
        Assert.False(table.Rows[0].Cells.ContainsKey("Hour"));
    }

    // ---- ExportCsv ----

    [Fact]
    public void ExportCsvWritesTheExactHeaderLine()
    {
        var dest = Path.Combine(_dir, "header.csv");
        ProductionReport.ExportCsv(new List<ProductionReport.GroupResult>(),
            new[] { "SOURCE-FOLDER", "Employee" }, new[] { "PDF-PAGE-COUNT" }, dest);

        var lines = File.ReadAllLines(dest, Encoding.UTF8);
        Assert.Equal("SOURCE-FOLDER,Employee,record_count,PDF-PAGE-COUNT", lines[0]);
    }

    [Fact]
    public void ExportCsvReturnsTheRowCount()
    {
        var results = new List<ProductionReport.GroupResult>
        {
            new(new[] { "A" }, 2, new Dictionary<string, double> { ["Count"] = 5.0 }),
            new(new[] { "B" }, 1, new Dictionary<string, double> { ["Count"] = 3.0 }),
        };
        var count = ProductionReport.ExportCsv(results, new[] { "Cat" }, new[] { "Count" },
            Path.Combine(_dir, "count.csv"));

        Assert.Equal(2, count);
    }

    /// <summary>A group value with an embedded comma (a category folder name,
    /// say) must survive Csv.WriteRow's escaping, not corrupt the row.</summary>
    [Fact]
    public void ExportCsvEscapesACommaInAGroupValue()
    {
        var results = new List<ProductionReport.GroupResult>
        {
            new(new[] { "Smith, John" }, 1, new Dictionary<string, double> { ["Count"] = 1.0 }),
        };
        var dest = Path.Combine(_dir, "escaped.csv");

        ProductionReport.ExportCsv(results, new[] { "Employee" }, new[] { "Count" }, dest);

        var lines = File.ReadAllLines(dest, Encoding.UTF8);
        Assert.Equal("\"Smith, John\",1,1", lines[1]);
    }

    [Fact]
    public void ExportCsvHasAUtf8Bom()
    {
        var dest = Path.Combine(_dir, "bom.csv");
        ProductionReport.ExportCsv(new List<ProductionReport.GroupResult>(), new[] { "Cat" },
            new[] { "Count" }, dest);

        var bytes = File.ReadAllBytes(dest);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
    }

    // ---- Culture invariance ----

    /// <summary>Mirrors CultureInvariantDatesTests' UnderCulture swap:
    /// WithDerived's date parsing and ExportCsv's number formatting both
    /// route through InvariantCulture, so de-DE's comma decimal separator
    /// and th-TH's Buddhist-era calendar must never change the result.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void WithDerivedAndExportCsvAreByteIdenticalUnderAnyCulture(string culture)
    {
        var table = new SweptTable.Table(
            new[] { "DATE-TIME" },
            new[] { Row("a.csv", ("DATE-TIME", "4/1/2025 7:55")) },
            1, Array.Empty<string>());

        var invariantDerived = ProductionReport.WithDerived(table, "", "DATE-TIME");
        var invariantGroups = ProductionReport.Group(invariantDerived, new[] { "Date" }, Array.Empty<string>());
        var results = new List<ProductionReport.GroupResult>
            { new(new[] { "X" }, 1, new Dictionary<string, double> { ["Count"] = 1234.5 }) };
        var invariantDest = Path.Combine(_dir, "invariant.csv");
        ProductionReport.ExportCsv(results, new[] { "Cat" }, new[] { "Count" }, invariantDest);
        var invariantBytes = File.ReadAllBytes(invariantDest);

        SweptTable.Table swappedDerived = null!;
        List<ProductionReport.GroupResult> swappedGroups = null!;
        byte[] swappedBytes = Array.Empty<byte>();
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            swappedDerived = ProductionReport.WithDerived(table, "", "DATE-TIME");
            swappedGroups = ProductionReport.Group(swappedDerived, new[] { "Date" }, Array.Empty<string>());
            var swappedDest = Path.Combine(_dir, $"swapped-{culture}.csv");
            ProductionReport.ExportCsv(results, new[] { "Cat" }, new[] { "Count" }, swappedDest);
            swappedBytes = File.ReadAllBytes(swappedDest);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }

        Assert.Equal(invariantDerived.Rows[0].Cells["Date"], swappedDerived.Rows[0].Cells["Date"]);
        Assert.Equal(invariantGroups[0].Key, swappedGroups[0].Key);
        Assert.Equal(invariantBytes, swappedBytes);
    }
}
