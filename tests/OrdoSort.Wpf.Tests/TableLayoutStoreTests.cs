using System.ComponentModel;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Where each table's column layout is remembered on this PC.
/// Pure file I/O, no WPF.</summary>
public sealed class TableLayoutStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_layouts_" + Guid.NewGuid().ToString("N"))).FullName;

    private string FilePath => Path.Combine(_dir, "table-columns.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static TableLayout Sample() => new(
        new[]
        {
            new ColumnLayout("Current name", 310, true, 0),
            new ColumnLayout("Note", 90, false, 2),
        },
        "Current name", ListSortDirection.Descending);

    [Fact]
    public void ALayoutComesBackUnderItsKey()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("BulkRename", Sample());

        var loaded = new TableLayoutStore(FilePath).Load("BulkRename")!;

        Assert.Equal(Sample().Columns, loaded.Columns);
        Assert.Equal("Current name", loaded.SortHeader);
        Assert.Equal(ListSortDirection.Descending, loaded.SortDirection);
    }

    [Fact]
    public void NothingSavedIsNull() =>
        Assert.Null(new TableLayoutStore(FilePath).Load("BulkRename"));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void ADamagedFileIsTreatedAsNothingSaved(string contents)
    {
        File.WriteAllText(FilePath, contents);

        Assert.Null(new TableLayoutStore(FilePath).Load("BulkRename"));
    }

    [Fact]
    public void SavingOneWindowKeepsTheOthers()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("History", Sample());

        store.Save("BulkRename", Sample() with { SortHeader = null, SortDirection = null });

        Assert.NotNull(store.Load("History"));
        Assert.Null(store.Load("BulkRename")!.SortHeader);
    }

    /// <summary>Triage builds different columns per roster; saving one
    /// roster's columns must not forget another's.</summary>
    [Fact]
    public void SavingMergesColumnsByHeader()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("Triage", new TableLayout(new[] { new ColumnLayout("DOB", 120, true, 1) }, null, null));

        store.Save("Triage", new TableLayout(new[] { new ColumnLayout("MRN", 90, true, 1) }, null, null));

        var headers = store.Load("Triage")!.Columns.Select(c => c.Header).OrderBy(h => h);
        Assert.Equal(new[] { "DOB", "MRN" }, headers);
    }

    [Fact]
    public void SavingCreatesTheFolder()
    {
        var nested = Path.Combine(_dir, "a", "b", "table-columns.json");

        new TableLayoutStore(nested).Save("BulkRename", Sample());

        Assert.True(File.Exists(nested));
    }

    /// <summary>The temp folder itself sits under %LOCALAPPDATA% on
    /// Windows, so the check is against the app's own folder there.</summary>
    [Fact]
    public void TheTestRunNeverTouchesTheRealUserProfile() =>
        Assert.False(TableLayoutStore.DefaultPath.StartsWith(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrdoSort"),
            StringComparison.OrdinalIgnoreCase), TableLayoutStore.DefaultPath);
}
