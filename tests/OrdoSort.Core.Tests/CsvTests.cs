using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Csv is the shared plumbing behind MatchMerge's roster reader and
/// History's exporter — MatchMergeTests and HistoryTests already prove
/// nothing broke when the parsing and escaping moved here unchanged, so these
/// tests focus on Csv's own surface, including the delimiter sniffing that
/// neither caller had before.</summary>
public class CsvTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csvtest_" + Guid.NewGuid());

    public CsvTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------- Parse

    [Fact]
    public void ParsesQuotedFieldsWithCommasEscapedQuotesAndEmbeddedNewlinesAndFiltersBlankRows()
    {
        var note = "Smith, Jones \"the third\" & Co\nMore text";
        var text =
            "Name,Note\n" +
            "Frank,\"" + note.Replace("\"", "\"\"") + "\"\n" +
            "\n" +                                             // blank row: filtered
            "Maria,plain\n";

        var rows = Csv.Parse(text);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Name", "Note" }, rows[0]);
        Assert.Equal(new[] { "Frank", note }, rows[1]);
        Assert.Equal(new[] { "Maria", "plain" }, rows[2]);
    }

    [Fact]
    public void SniffsTabDelimiterFromTheHeaderLine()
    {
        var text = "DATE-TIME\tFILE-OWNER\tFILE-NAME\n2024-01-26\tSMITH\tfile.pdf\n";

        var rows = Csv.Parse(text);

        Assert.Equal(new[] { "DATE-TIME", "FILE-OWNER", "FILE-NAME" }, rows[0]);
        Assert.Equal(new[] { "2024-01-26", "SMITH", "file.pdf" }, rows[1]);
    }

    [Fact]
    public void ATabInsideAQuotedFieldOnLineOneDoesNotFlipToTabDelimiter()
    {
        // line 1 has one tab (inside the quotes) and two commas — commas
        // outnumber tabs, so this still parses as comma-delimited
        var text = "\"A\tB\",C,D\n1,2,3\n";

        var rows = Csv.Parse(text);

        Assert.Equal(new[] { "A\tB", "C", "D" }, rows[0]);
        Assert.Equal(new[] { "1", "2", "3" }, rows[1]);
    }

    [Fact]
    public void NoTabsNoCommasOnLineOneStaysComma()
    {
        var rows = Csv.Parse("onlyfield\nsecondrow\n");

        Assert.Equal(new[] { "onlyfield" }, rows[0]);
        Assert.Equal(new[] { "secondrow" }, rows[1]);
    }

    // ---------------------------------------------------- EscapeField

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    public void LeadingFormulaCharactersGetAnApostropheGuard(string input, string expected) =>
        Assert.Equal(expected, Csv.EscapeField(input));

    [Fact]
    public void PlainValuesAreUntouched() =>
        Assert.Equal("plain value", Csv.EscapeField("plain value"));

    [Fact]
    public void ValuesWithACommaGetQuoted() =>
        Assert.Equal("\"a,b\"", Csv.EscapeField("a,b"));

    [Fact]
    public void ValuesWithQuotesGetQuotedWithQuotesDoubled()
    {
        var input = "say \"hi\"";
        var expected = "\"" + input.Replace("\"", "\"\"") + "\"";

        Assert.Equal(expected, Csv.EscapeField(input));
    }

    // ---------------------------------------------------- WriteRow

    [Fact]
    public void WriteRowJoinsWithCommasAndEscapesEachField()
    {
        var row = Csv.WriteRow(new[] { "a,b", "plain", "=formula" });

        Assert.Equal("\"a,b\",plain,'=formula", row);
    }

    // ---------------------------------------------------- ReadTable

    [Fact]
    public void ReadTableDispatchesACsvPathToParse()
    {
        var path = Path.Combine(_dir, "t.csv");
        File.WriteAllText(path, "A,B\n1,2\n");

        var rows = Csv.ReadTable(path);

        Assert.Equal(new[] { "A", "B" }, rows[0]);
        Assert.Equal(new[] { "1", "2" }, rows[1]);
    }
}
