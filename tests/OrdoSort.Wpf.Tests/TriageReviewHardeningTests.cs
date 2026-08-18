using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The "Review matches" window is the one place in this app where a
/// person hands a document to a NAMED PERSON by hand, so every defect here is
/// a misfile rather than a cosmetic one. This suite pins the review-pass
/// hardening round (2026-08-17), each fact named for the defect it would catch
/// coming back:
///
/// <list type="bullet">
/// <item><description>a decision follows the row on SCREEN, not the row at
/// that index in the unsorted candidate list (the silent-misfile bug);</description></item>
/// <item><description>an id a filename can't hold shows the note it was always
/// meant to, instead of throwing out of an async void handler;</description></item>
/// <item><description>a roster column headed with a comma still shows its
/// values;</description></item>
/// <item><description>a double-click on the chrome is not a decision;</description></item>
/// <item><description>Enter belongs to a focused button, and Escape stops the
/// review even when it arrives from the PDF pane.</description></item>
/// </list>
///
/// TriageWindow is NEVER Show()n here — Loaded starts a real WebView2/Edge
/// init this host cannot complete (TriageWindowDisposalTests' class doc has
/// the full account). Instead <see cref="TriageWindow.ShowCurrentAsync"/> is
/// driven directly and the grid is measured/arranged by hand, the same
/// technique DataGridSelectionContrastTests' BuildTriageWindowWithWhy uses to
/// read realized cells without a window ever being on screen.</summary>
[Collection(HighlightContrastTests.Name)]
public class TriageReviewHardeningTests
{
    private readonly HighlightContrastFixture _fx;
    public TriageReviewHardeningTests(HighlightContrastFixture fx) => _fx = fx;

    // ------------------------------------------------------------ the facts

    /// <summary>The misfile. The grid's headers are clickable, so the row
    /// displayed first need not be the candidate stored first — and the
    /// decision used to be read as <c>CandidatesOf(r)[SelectedIndex]</c>,
    /// which indexes the SOURCE list with a DISPLAY index. Sorted, that files
    /// the document under a different person, with a perfectly plausible name
    /// and nothing on screen to say so.
    ///
    /// The whole keyboard path is driven here (digit picks, Enter confirms)
    /// rather than the selection being poked in, because the digit is the
    /// other half of the same contract: "1" has to mean the row you can see at
    /// the top, whatever the sort.</summary>
    [Fact]
    public void ASortedGridMergesTheRowOnScreenNotTheOneAtThatSourceIndex() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_sorted_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" }, Item(path,
                Candidate("1111"), Candidate("2222")));
            try
            {
                ShowCurrent(win);

                // A sorted view, arranged with CustomSort rather than a
                // SortDescription: what this test needs is only that DISPLAY
                // order differs from SOURCE order, and CustomSort states that
                // without also depending on how a property path resolves.
                var view = (ListCollectionView)CollectionViewSource
                    .GetDefaultView(win.Candidates.ItemsSource);
                view.CustomSort = ControlIdDescending;
                Assert.Equal("2222", ValueOf(win.Candidates.Items[0], "Control ID"));

                Assert.True(win.DispatchVerb(Key.D1, ModifierKeys.None, null));
                Assert.Equal("2222", ValueOf(win.Candidates.SelectedItem, "Control ID"));

                Assert.True(win.DispatchVerb(Key.Enter, ModifierKeys.None, null));

                var outcome = Assert.Single(win.Outcomes);
                Assert.NotNull(outcome.Final);
                Assert.Equal("SMITH_JOHN-2222",
                    Path.GetFileNameWithoutExtension(outcome.Final!));
                Assert.True(File.Exists(outcome.Final!));
                Assert.False(File.Exists(path));
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>Same contract by mouse: the "Use selected id" button resolves
    /// the selection exactly as the keyboard does (the double-click path has
    /// its own facts below). Worth stating separately because these are
    /// distinct call sites, and a fix applied to only one of them still
    /// misfiles for whoever prefers the mouse.</summary>
    [Fact]
    public void TheButtonPathResolvesTheSortedRowToo() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_sorted_button_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" }, Item(path,
                Candidate("1111"), Candidate("2222")));
            try
            {
                ShowCurrent(win);
                var view = (ListCollectionView)CollectionViewSource
                    .GetDefaultView(win.Candidates.ItemsSource);
                view.CustomSort = ControlIdDescending;
                win.Candidates.SelectedIndex = 0;   // the top row as displayed

                PumpUntilComplete(win.UseSelectedAsync(() => Task.CompletedTask));

                var outcome = Assert.Single(win.Outcomes);
                Assert.Equal("SMITH_JOHN-2222",
                    Path.GetFileNameWithoutExtension(outcome.Final!));
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>MergeOne PLANS before it renames, and a plan it refuses — a
    /// control id carrying ':' or anything else Windows won't put in a
    /// filename — is dropped rather than executed, so the returned list is
    /// EMPTY rather than one failed outcome. Reading <c>outcomes[0]</c>
    /// regardless threw out of an async void handler and into the global crash
    /// dialog, over a file nothing had touched. The window has a note for
    /// exactly this; it just never reached it.</summary>
    [Fact]
    public void AnIdAFilenameCannotHoldShowsTheNoteInsteadOfCrashing() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_illegal_id_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" }, Item(path, Candidate("24:001")));
            try
            {
                ShowCurrent(win);
                win.Candidates.SelectedIndex = 0;

                var flow = win.UseSelectedAsync(() => Task.CompletedTask);
                PumpUntilComplete(flow);

                Assert.True(flow.IsCompletedSuccessfully,
                    "the decision faulted instead of reporting the refusal — this is the " +
                    "outcomes[0] crash, which reaches the user as the global crash dialog");
                Assert.Empty(win.Outcomes);
                Assert.StartsWith("Couldn't rename:", win.Note.Text);
                // names the offending character, which means the reason came
                // from the planner's own check rather than a generic apology
                Assert.Contains("can't contain ':'", win.Note.Text);
                Assert.True(File.Exists(path), "the file was touched by a refused merge");
                Assert.Equal("1 / 1", win.Progress.Text);   // still on the same document
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>Roster headers are a stranger's spreadsheet text, and the
    /// column bindings used to interpolate them into a <c>Binding("[header]")</c>
    /// PATH — which is parsed: "Name, Last" reads as a two-argument indexer,
    /// binds to nothing, and the column renders empty for every candidate. In
    /// the one window whose whole job is showing enough of a roster row to
    /// recognise a person by, that is a blank where the name should be.</summary>
    [Fact]
    public void AHeaderWithACommaStillRendersItsValue() => _fx.Invoke(() =>
    {
        var row = new Dictionary<string, string>
        {
            ["Name, Last"] = "Garcia-Lopez",
            ["Control ID"] = "1111",
        };
        var win = Build(new[] { "Name, Last", "Control ID" },
            Item(@"C:\inbox\doc.pdf", new MatchMerge.Candidate("1111", row)));
        try
        {
            ShowCurrent(win);
            Realize(win);

            var column = win.Candidates.Columns.First(c => (string)c.Header == "Name, Last");
            Assert.Equal("Garcia-Lopez", CellText(win.Candidates, column));

            // The deliberate trade this fix makes, pinned so it stays
            // deliberate: sorting is the one thing still reached by a property
            // path, so a header the parser can't read offers no sort rather
            // than a sort that silently does nothing — while an ordinary
            // header keeps exactly the path it always sorted by.
            Assert.False(column.CanUserSort);
            var plain = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            Assert.True(plain.CanUserSort);
            Assert.Equal("[Control ID]", plain.SortMemberPath);
        }
        finally { win.Close(); }
    });

    /// <summary>The double-click handler sits on the whole grid, and the old
    /// one asked only whether SOMETHING was selected — so double-clicking a
    /// column header (or the gripper between two, or the empty space under the
    /// last row) merged whichever row happened to still be selected. Sorting a
    /// column is a double-click-adjacent gesture on exactly that chrome.</summary>
    [Fact]
    public void ADoubleClickOnTheGridChromeIsNotADecision() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_chrome_click_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" },
                Item(path, Candidate("1111")),
                Item(@"C:\inbox\next.pdf", Candidate("2222")));
            try
            {
                ShowCurrent(win);
                Realize(win);
                win.Candidates.SelectedIndex = 0;   // a row IS selected — that's the trap

                // The chrome this can reach with no window on screen: the grid
                // itself (what a double-click in the empty area under the last
                // row reports) plus every realized column header. The grid is
                // in the list unconditionally so this can never go vacuous if
                // headers happen not to realize headlessly.
                var chrome = new List<object> { win.Candidates };
                chrome.AddRange(Descendants<DataGridColumnHeader>(win.Candidates)
                    .Where(h => h.Column is not null));
                foreach (var source in chrome)
                {
                    RaiseDoubleClick(win.Candidates, source);
                    Assert.Empty(win.Outcomes);
                }

                Assert.True(File.Exists(path), "a double-click on the chrome renamed the document");
                Assert.Equal("1 / 2", win.Progress.Text);   // still on the first document
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>The guard against over-fixing: a double-click that really did
    /// land in a row still merges, which is the gesture the window documents.</summary>
    [Fact]
    public void ADoubleClickInsideARowStillMerges() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_row_click_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" },
                Item(path, Candidate("1111")),
                Item(@"C:\inbox\next.pdf", Candidate("2222")));
            try
            {
                ShowCurrent(win);
                Realize(win);
                win.Candidates.SelectedIndex = 0;

                var cell = CellTextBlock(win.Candidates, win.Candidates.Columns[0]);
                RaiseDoubleClick(win.Candidates, cell);

                var outcome = Assert.Single(win.Outcomes);
                Assert.Equal("SMITH_JOHN-1111",
                    Path.GetFileNameWithoutExtension(outcome.Final!));
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>Enter is a window-wide verb, which meant it was taken from the
    /// buttons: Tab to "Skip this file", press Enter, and the window MERGED
    /// the row still selected behind it instead of skipping. Two Tab stops
    /// away from the most destructive gesture in the app.</summary>
    [Fact]
    public void EnterOnAFocusedButtonIsLeftToThatButton() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_focused_button_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = Build(new[] { "Control ID" }, Item(path, Candidate("1111")));
            try
            {
                ShowCurrent(win);
                win.Candidates.SelectedIndex = 0;

                Assert.False(win.DispatchVerb(Key.Enter, ModifierKeys.None, win.SkipButton));
                Assert.False(win.DispatchVerb(Key.Enter, ModifierKeys.None, win.StopButton));
                Assert.Empty(win.Outcomes);
                Assert.True(File.Exists(path));

                // and the control: with focus anywhere else, Enter still means
                // "use this id" — a guard that swallowed Enter outright would
                // pass the two asserts above and break the window's main verb
                Assert.True(win.DispatchVerb(Key.Enter, ModifierKeys.None, null));
                Assert.Single(win.Outcomes);
            }
            finally { win.Close(); }
        }
        finally { Delete(dir); }
    });

    /// <summary>Escape is answered by the verb dispatch rather than left to
    /// the Stop button's IsCancel, because IsCancel is reached through
    /// AccessKeyManager — which only ever sees a key in InputManager's
    /// post-process stage, and so never sees the PreviewKeyDown the WebView2
    /// control re-raises for a key pressed inside the PDF pane. Escape from
    /// the pane was simply dead.</summary>
    [Fact]
    public void EscapeStopsTheReview() => _fx.Invoke(() =>
    {
        var win = Build(new[] { "Control ID" }, Item(@"C:\inbox\doc.pdf", Candidate("1111")));
        ShowCurrent(win);

        Assert.True(win.DispatchVerb(Key.Escape, ModifierKeys.None, null));
        Assert.True(win.IsClosed);
    });

    /// <summary>S skips, and the dispatch is genuinely the window's own skip
    /// path rather than a lookalike.</summary>
    [Fact]
    public void SAdvancesToTheNextDocumentWithoutMerging() => _fx.Invoke(() =>
    {
        var win = Build(new[] { "Control ID" },
            Item(@"C:\inbox\doc.pdf", Candidate("1111")),
            Item(@"C:\inbox\next.pdf", Candidate("2222")));
        try
        {
            ShowCurrent(win);
            Assert.Equal("1 / 2", win.Progress.Text);

            Assert.True(win.DispatchVerb(Key.S, ModifierKeys.None, null));

            Assert.Equal("2 / 2", win.Progress.Text);
            Assert.Empty(win.Outcomes);
        }
        finally { win.Close(); }
    });

    /// <summary>Modifier combos are not verbs — Ctrl+S is save-as muscle
    /// memory, and must not read as "skip this file".</summary>
    [Fact]
    public void ModifiedKeysAreNotVerbs() => _fx.Invoke(() =>
    {
        var win = Build(new[] { "Control ID" },
            Item(@"C:\inbox\doc.pdf", Candidate("1111")),
            Item(@"C:\inbox\next.pdf", Candidate("2222")));
        try
        {
            ShowCurrent(win);

            Assert.False(win.DispatchVerb(Key.S, ModifierKeys.Control, null));
            Assert.False(win.DispatchVerb(Key.D1, ModifierKeys.Alt, null));
            Assert.False(win.DispatchVerb(Key.Escape, ModifierKeys.Shift, null));

            Assert.Equal("1 / 2", win.Progress.Text);
            Assert.False(win.IsClosed);
        }
        finally { win.Close(); }
    });

    // ------------------------------------------------------------- plumbing

    /// <summary>Same pumping technique, and for the same reason, as
    /// TriageWindowInitRaceTests' own helper: a continuation resumed on this
    /// STA thread's dispatcher can't be observed by a plain blocking wait on
    /// that same thread.</summary>
    private static void PumpUntilComplete(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
    }

    private static readonly IComparer ControlIdDescending = Comparer<object>.Create(
        (a, b) => string.CompareOrdinal(ValueOf(b, "Control ID"), ValueOf(a, "Control ID")));

    private static string ValueOf(object? row, string header) =>
        row is IReadOnlyDictionary<string, string> values && values.TryGetValue(header, out var v)
            ? v
            : throw new InvalidOperationException(
                $"grid row {row?.GetType().Name ?? "null"} carries no \"{header}\"");

    private static MatchMerge.Candidate Candidate(string controlId) =>
        new(controlId, new Dictionary<string, string> { ["Control ID"] = controlId });

    private static MatchMerge.MatchResult Item(string source, params MatchMerge.Candidate[] candidates) =>
        new(source, "ambiguous", "SMITH", "JOHN", Candidates: candidates.ToList());

    private static TriageWindow Build(string[] headers, params MatchMerge.MatchResult[] items) =>
        new(items.ToList(), headers) { Dialogs = new FakeDialogs() };

    /// <summary>Populates Progress/Candidates the way Loaded normally would,
    /// without Show(). Synchronous-safe: the window was never shown, so
    /// WebViewPdfViewer's own _ready flag is false and ShowAsync is a no-op
    /// returning an already-completed task — the same reasoning
    /// TriageWindowDisposalTests' class doc established for this pattern.</summary>
    private static void ShowCurrent(TriageWindow win)
    {
#pragma warning disable xUnit1031
        win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
    }

    /// <summary>Realizes the candidate grid's containers without a window on
    /// screen — DataGridSelectionContrastTests' BuildTriageWindowWithWhy does
    /// the same, at the same 440px side-panel width the app ships.</summary>
    private static void Realize(TriageWindow win)
    {
        win.Candidates.ApplyTemplate();
        win.Candidates.Measure(new Size(440, 500));
        win.Candidates.Arrange(new Rect(0, 0, 440, 500));
        win.Candidates.UpdateLayout();
    }

    private static void Delete(DirectoryInfo dir)
    {
        try { Directory.Delete(dir.FullName, true); } catch { }
    }

    private static void RaiseDoubleClick(DataGrid grid, object originalSource)
    {
        // Source assigned BEFORE raising: RoutedEventArgs' first Source
        // assignment also fixes OriginalSource, and RaiseEvent's own
        // Source-to-sender assignment afterwards leaves that alone — which is
        // what lets this stand in for a click that really landed on the given
        // element.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = originalSource,
        };
        grid.RaiseEvent(args);
    }

    private static string CellText(DataGrid grid, DataGridColumn column) =>
        CellTextBlock(grid, column).Text;

    private static TextBlock CellTextBlock(DataGrid grid, DataGridColumn column)
    {
        grid.UpdateLayout();
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new InvalidOperationException("row 0 never realized a container");
        row.ApplyTemplate();
        row.UpdateLayout();
        var cell = Descendants<DataGridCell>(row).FirstOrDefault(c => c.Column == column)
            ?? throw new InvalidOperationException(
                $"no realized cell for column \"{column.Header}\"");
        cell.ApplyTemplate();
        cell.UpdateLayout();
        return Descendants<TextBlock>(cell).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"column \"{column.Header}\" realized no TextBlock");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
