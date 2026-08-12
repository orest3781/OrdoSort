using System.Text;
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

    // ------------------------------------- stray quotes (regression)

    /// <summary>A quote that is not at the start of a field is literal, and
    /// above all does not swallow the rest of the file. Before the fix, the
    /// unescaped quote in `3" pipe` flipped the parser into quote mode with
    /// no closing quote ahead of it, so every following delimiter and newline
    /// was appended as text into that one cell and Sue's row disappeared
    /// entirely from the result — silently, with no exception. Asserting the
    /// row COUNT is what makes this fail against the original: the old parser
    /// returned 2 rows here, not 3.</summary>
    [Fact]
    public void AStrayQuoteMidFieldIsLiteralAndDoesNotSwallowTheFollowingRows()
    {
        var rows = Csv.Parse("Name,Note\nBob,3\" pipe\nSue,ok\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Name", "Note" }, rows[0]);
        Assert.Equal(new[] { "Bob", "3\" pipe" }, rows[1]);
        Assert.Equal(new[] { "Sue", "ok" }, rows[2]);
    }

    /// <summary>The roster shape of the same bug: a nickname quoted inside an
    /// otherwise unquoted field. Every later person must still be parsed —
    /// a dropped roster row means someone silently stops being matchable.</summary>
    [Fact]
    public void AQuotedNicknameInsideAnUnquotedFieldKeepsEveryLaterRow()
    {
        var rows = Csv.Parse("Name,Id\nRobert \"Bob\" Smith,7\nMaria Garcia,8\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Robert \"Bob\" Smith", "7" }, rows[1]);
        Assert.Equal(new[] { "Maria Garcia", "8" }, rows[2]);
    }

    /// <summary>The fix must not regress the properly-quoted case: a quote at
    /// the start of a field still opens a quoted field that may embed the
    /// delimiter, doubled quotes and newlines.</summary>
    [Fact]
    public void AQuoteAtTheStartOfAFieldStillOpensAQuotedField()
    {
        var rows = Csv.Parse("Name,Note\n\"Smith, John\",\"said \"\"hi\"\"\"\nSue,ok\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Smith, John", "said \"hi\"" }, rows[1]);
        Assert.Equal(new[] { "Sue", "ok" }, rows[2]);
    }

    // ------------------------------------- encoding (regression)

    /// <summary>Excel's plain "CSV (Comma delimited)" export writes the system
    /// ANSI codepage with no BOM, not UTF-8. Decoding those bytes as UTF-8
    /// (the old behaviour) does not throw — it substitutes U+FFFD — so GARCÍA
    /// silently loaded as GARC�A and MatchMerge's exact-match key no longer
    /// matched. Asserting the absence of U+FFFD is what fails against the
    /// original.</summary>
    [Fact]
    public void AnAnsiEncodedCsvKeepsItsAccentsInsteadOfBecomingReplacementChars()
    {
        var path = Path.Combine(_dir, "ansi.csv");
        // 0xCD is Í and 0xC9 is É in both Latin-1 and Windows-1252, and
        // neither byte is valid standalone UTF-8.
        File.WriteAllBytes(path, new byte[]
        {
            (byte)'N', (byte)'a', (byte)'m', (byte)'e', (byte)',', (byte)'I', (byte)'d', (byte)'\n',
            (byte)'G', (byte)'A', (byte)'R', (byte)'C', 0xCD, (byte)'A', (byte)',', (byte)'1', (byte)'\n',
            (byte)'J', (byte)'O', (byte)'S', 0xC9, (byte)',', (byte)'2', (byte)'\n',
        });

        var rows = Csv.ReadTable(path);

        Assert.Equal(new[] { "GARCÍA", "1" }, rows[1]);
        Assert.Equal(new[] { "JOSÉ", "2" }, rows[2]);
        Assert.DoesNotContain(rows.SelectMany(r => r), f => f.Contains('�'));
    }

    [Fact]
    public void ValidUtf8IsStillDecodedAsUtf8WithOrWithoutABom()
    {
        var withBom = Path.Combine(_dir, "bom.csv");
        var noBom = Path.Combine(_dir, "nobom.csv");
        File.WriteAllText(withBom, "Name,Id\nGARCÍA,1\n", new UTF8Encoding(true));
        File.WriteAllText(noBom, "Name,Id\nGARCÍA,1\n", new UTF8Encoding(false));

        Assert.Equal(new[] { "GARCÍA", "1" }, Csv.ReadTable(withBom)[1]);
        Assert.Equal(new[] { "GARCÍA", "1" }, Csv.ReadTable(noBom)[1]);
    }
}
