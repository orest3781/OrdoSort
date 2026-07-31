using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

/// <summary>Constructs every secondary window against the real app resources
/// and forces layout — catches XAML/binding wiring errors without showing UI.</summary>
public static class DialogCheck
{
    public static int Run() => SmokeUi.RunSta(Drive,
        "DIALOGS OK — all eight construct cleanly",
        "DIALOG FAIL:");

    private static List<string> Drive()
    {
        var errors = new List<string>();
        SmokeUi.Boot();
        var dialogs = new RecordingDialogs();
        var dir = Directory.CreateTempSubdirectory("ordo_dialogcheck").FullName;

        void Check(string name, Func<System.Windows.Window> make)
        {
            try
            {
                var w = make();
                w.Measure(new System.Windows.Size(1000, 800));   // force template + bindings
                w.Close();
            }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }

        Check("Unlock", () => new UnlockWindow(new UnlockViewModel(new Config(), () => { })));
        Check("BulkRename", () => new BulkRenameWindow(new BulkRenameViewModel()));
        Check("MatchMerge", () => new MatchMergeWindow(
            new MatchMergeViewModel(new Config(), _ => { }, dialogs)));
        Check("Settings", () => new SettingsWindow(new SettingsViewModel(new Config(), dialogs)));
        Check("LabelMaker", () => new LabelMakerWindow(
            new LabelMakerViewModel(new Config(), Path.Combine(dir, "box-labels.json"), dialogs)));
        Check("PrintPreview", () => new PrintPreviewWindow(
            OrdoSort.Wpf.Views.LabelPrinting.BuildDocument(
                BoxLabels.Batch("ABCD", 1, 12, new DateTime(2026, 7, 25), 30)),
            "smoke", _ => { }));
        Check("TriageWindow", () => new TriageWindow(new List<MatchMerge.MatchResult>(), new[] { "A", "B" }));

        // 3a dedupes ChosenColumns at the view model, but TriageWindow itself
        // takes whatever header list it's handed — a roster with a genuinely
        // repeated column name still reaches it. ShowCurrentAsync's per-row
        // dictionary building must survive that on BOTH the ambiguous and the
        // suggested (Why-column) rendering path.
        try
        {
            var dupHeaders = new[] { "Last", "Last", "Control" };
            var candidate = new MatchMerge.Candidate("111",
                new Dictionary<string, string> { ["Last"] = "EVANS", ["Control"] = "111" });
            var suggestion = new MatchMerge.Suggestion(candidate, "all segments agree");
            var mixed = new List<MatchMerge.MatchResult>
            {
                new("a.pdf", "ambiguous", "EVANS", "FRANK", Candidates: new[] { candidate }),
                new("b.pdf", "suggested", "EVANS", "FRANK", Suggestions: new[] { suggestion }),
            };
            var w = new TriageWindow(mixed, dupHeaders);
            w.ShowCurrentAsync().GetAwaiter().GetResult();   // item 0: ambiguous
            w.Index = 1;
            w.ShowCurrentAsync().GetAwaiter().GetResult();   // item 1: suggested, Why column
            w.Measure(new System.Windows.Size(1000, 800));
            w.Close();
        }
        catch (Exception ex) { errors.Add($"TriageWindow (duplicate headers): {ex.Message}"); }

        using (var history = new History(Path.Combine(dir, "history.sqlite")))
        {
            Check("History", () => new HistoryWindow(new HistoryViewModel(history, dialogs)));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
        return errors;
    }
}
