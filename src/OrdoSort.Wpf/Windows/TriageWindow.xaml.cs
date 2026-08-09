using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

/// <summary>One file at a time: ambiguous matches and token-pass
/// suggestions; pick the row the document belongs to, or skip. The PDF
/// appears in Edge (left) and every candidate's full roster row on the
/// right. The viewer's handle is released before every rename — same
/// contract as the filing loop.</summary>
public partial class TriageWindow : Window
{
    /// <summary>Fixed width of the "Why" column <see cref="ShowCurrentAsync"/>
    /// inserts for a suggested-status item, below — pulled out as a constant
    /// (rather than a literal at both call sites) so the roster-column
    /// budget computed in the constructor and the column ShowCurrentAsync
    /// actually builds can never drift out of sync with each other. NOT
    /// capped/resized the way roster columns are: it already avoids
    /// horizontal growth by wrapping its (prose, "a match you can't explain
    /// is one you can't trust") content instead of ellipsizing, since a
    /// truncated reason is far less useful than a taller row — but its own
    /// fixed FOOTPRINT still has to be budgeted for, which the first version
    /// of this fix (2026-08-07) missed: it reasoned about Why's content, not
    /// its column width, and left the roster-column budget unaware Why could
    /// ever be on screen at the same time.
    ///
    /// FIX ROUND 5 (2026-08-08, "columns can't be moved and are hiding text"
    /// task): was 260. Measured off-screen at the default 440px panel width
    /// with the app's default 2-header roster pick ("First name"/"Control
    /// ID"): 260 left the one capped roster column (Control ID) at a 76px
    /// cap — roughly 8-9 characters before ellipsizing, the "hidden text"
    /// the owner reported. Why's own content WRAPS rather than ellipsizing —
    /// narrowing it costs only row height, never information — while the
    /// roster columns ellipsize, and the roster is what a person actually
    /// reads to make the match/merge decision; Why is supporting context,
    /// not the primary content the window exists to show. Cut to 150: still
    /// comfortable for the short reason phrases MatchMerge.cs's token pass
    /// actually generates ("token match on last name" et al. — well under
    /// two wrapped lines at this width, not a wall of near-vertical text),
    /// while raising the default 2-header roster cap from 76px to 169px and
    /// the 3-header cap (AutoFitColumnTests' own worst case) from ~38px to
    /// 84.5px — both re-measured after this change, see AutoFitColumnTests.</summary>
    private const double WhyColumnWidth = 150;

    /// <summary>Headroom subtracted from the roster-column budget for the
    /// DataGrid's own border/padding — a possible vertical scrollbar (a long
    /// candidate list scrolls) is reserved separately, by
    /// <see cref="DataGridColumnCap.Track(DataGrid, Func{double, double},
    /// DataGridColumn[])"/> itself, before this budget ever sees its
    /// viewport width (fix round 5 — see that class's own doc comment).
    /// Border/padding isn't exactly predictable from XAML-declared widths
    /// alone, so this stays a deliberate, conservative buffer rather than
    /// budgeting to the exact pixel and finding out empirically it wasn't
    /// quite enough.</summary>
    private const double SafetyMargin = 20;

    /// <summary>Floor for the filler roster column. Smaller than History/
    /// MatchMerge/BulkRename's 120px star-column floor because this grid's
    /// whole side panel is only 380-440px wide (MinWidth-Width) — a fraction
    /// of those windows' own grid area — and, unlike them, may ALSO have to
    /// share that width with the fixed-260px Why column (see
    /// WhyColumnWidth); the same absolute 120px floor would leave too little
    /// for whatever capped roster columns sit alongside it.</summary>
    private const double FillerMinWidth = 60;

    /// <summary>Resolves the shared Theme/Styles.xaml <c>GridCellText</c>
    /// style (NO selection-contrast trigger of its own — see
    /// <see cref="GridCellTextSelectionAwareStyle"/> below for that) — the
    /// same production resource MatchMergeWindow.xaml/BulkRenameWindow.xaml/
    /// HistoryWindow.xaml's own status-coloured DataGridTextColumn.
    /// ElementStyle markup reaches via
    /// <c>BasedOn="{StaticResource GridCellText}"</c>, just resolved from
    /// code instead of XAML since this window builds its columns
    /// programmatically. Used here only by the "Why" column
    /// (<see cref="ShowCurrentAsync"/>), which — like Note/New name
    /// elsewhere — carries its own unconditional Foreground Setter and so
    /// must declare its own trailing "let selection win" trigger rather
    /// than delegate to the shared one. FindResource, not TryFindResource: a
    /// missing GridCellText style would silently strip every column's
    /// selection-contrast fix, and that should fail loudly rather than fall
    /// back to a plain, unstyled TextBlock.</summary>
    private static Style GridCellTextStyle => (Style)Application.Current.FindResource("GridCellText");

    /// <summary>Resolves the shared Theme/Styles.xaml
    /// <c>GridCellTextSelectionAware</c> style — GridCellText plus the one
    /// trailing "let selection win" DataTrigger, for columns with no status
    /// trigger of their own. Used by the plain roster columns built in the
    /// constructor below, the same "no other Foreground trigger, so the
    /// shared style is the whole fix" shape as MatchMergeWindow's File/
    /// Becomes and BulkRenameWindow's Current name columns (their own XAML
    /// comments explain the underlying bug).</summary>
    private static Style GridCellTextSelectionAwareStyle =>
        (Style)Application.Current.FindResource("GridCellTextSelectionAware");

    private readonly List<MatchMerge.MatchResult> _items;
    private readonly IReadOnlyList<string> _headers;
    private readonly WebViewPdfViewer _pdf;
    private readonly Func<System.Windows.Rect?> _panZone;
    private int _index;
    private bool _whyColumnShown;

    /// <summary>Swappable so the smoke harness can record instead of block —
    /// same pattern as MainWindow's own <c>Dialogs</c> property.</summary>
    internal IDialogService Dialogs { get; set; }

    /// <summary>True once this window has started closing. WebView2 cold
    /// start is not instant, and "Stop reviewing" is IsCancel="True" — so
    /// Close() can land while InitAndShowAsync below is still mid-flight, OR
    /// while a decision (<see cref="UseSelectedAsync"/>'s up-to-2s
    /// <c>ReleaseAsync</c> await, accepted via Enter) is. Set in the Closed
    /// handler BEFORE Viewer.Dispose(), checked after each await in
    /// InitAndShowAsync AND at the top of <see cref="ShowCurrentAsync"/> —
    /// the single choke point every post-decision continuation calls back
    /// into — so a continuation that resumes after the window is gone never
    /// touches the (by then disposed) Viewer and never shows a MessageBox
    /// owned by an already-closed window.</summary>
    internal bool IsClosed { get; private set; }

    /// <summary>Which item is being reviewed. Exposed only so the smoke
    /// test can drive every item's render path in turn without going
    /// through the full rename/skip plumbing.</summary>
    internal int Index { get => _index; set => _index = value; }

    /// <summary>Reentrancy: Enter works window-wide and the viewer release
    /// genuinely suspends, so a fast double Enter (or a stray S mid-merge)
    /// could act twice on one file. Same contract as the shell's commit
    /// guard: while one decision is in flight, further ones are no-ops.</summary>
    private bool _busy;

    public List<BulkRename.RenameOutcome> Outcomes { get; } = new();

    public TriageWindow(List<MatchMerge.MatchResult> items, IReadOnlyList<string> headers)
    {
        InitializeComponent();
        _items = items;
        _headers = headers;
        Dialogs = new DialogService(this);
        Viewer.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            AdditionalBrowserArguments = "--disable-smooth-scrolling",
        };
        _pdf = new WebViewPdfViewer(Viewer);
        _panZone = () =>
        {
            if (!IsActive || !Viewer.IsVisible) return null;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Viewer);
            var topLeft = Viewer.PointToScreen(new System.Windows.Point(0, 0));
            var device = new System.Windows.Rect(topLeft.X, topLeft.Y,
                Viewer.ActualWidth * dpi.DpiScaleX, Viewer.ActualHeight * dpi.DpiScaleY);
            return PanMath.PanZone(device, dpi.DpiScaleX, dpi.DpiScaleY);
        };
        ViewerInputEnhancer.Register(_panZone);
        Closed += (_, _) =>
        {
            // set BEFORE Dispose() — InitAndShowAsync's post-await checks
            // must see this the instant they resume, not race Dispose() itself
            IsClosed = true;
            ViewerInputEnhancer.Unregister(_panZone);
            // this window's WebView2 is deliberately fresh per review pass (see
            // MatchMergeWindow.OnReview) rather than reused — but nothing
            // reused it on the way out either, so its browser process
            // survived the window unless disposed here explicitly.
            Viewer.Dispose();
        };
        // Roster columns: content-sized (Width="Auto") and capped, one
        // filler (Width="*", MinWidth) absorbing the rest — same shape as
        // MatchMerge/BulkRename. The FIRST header is the filler rather than
        // the last: unlike MatchMerge/BulkRename's fixed schema, this app
        // can't know in advance which arbitrary roster column a person
        // picked to show here — but the roster's own default ("nothing
        // ticked = the name and id columns") leads with a name-shaped field
        // and follows with an id-shaped one, and a name is both more
        // variable and the one worth reading in full, so the leading column
        // gets the room and later ones are capped.
        //
        // The budget divides whatever's genuinely left of the side panel
        // EVENLY among however many capped columns exist, rather than a flat
        // per-column share: a flat share is exactly what shipped first here
        // (2026-08-07) and fit at the default 2-header case only by
        // coincidence — it would have overflowed at 3+.
        //
        // Critically, the budget ALSO reserves WhyColumnWidth: ShowCurrentAsync
        // below inserts that column — fixed-width, never capped or resized —
        // whenever the CURRENT item's Status is "suggested", and per
        // MatchMerge.cs that's a normal, common queue, not a rare one the
        // first version of this fix could afford to ignore (it didn't
        // account for Why's column footprint at all, only reasoned about its
        // wrapped CONTENT never growing sideways — true, but irrelevant to
        // the fixed width the column itself always occupies once inserted).
        // If this window's whole batch contains even one suggested item, Why
        // can appear at ANY point during the review pass — reviewing one
        // ambiguous item can be followed by a suggested one — so the budget
        // reserves Why's full width from the START rather than only once
        // it's actually on screen; a layout that fit before Why appeared and
        // started needing a horizontal scrollbar the moment it did would be
        // exactly the bug this task exists to prevent, just deferred to
        // whenever the batch happens to reach a suggested item. couldShowWhy
        // is therefore computed once, from the WHOLE batch, not the current
        // item — it does not need to be recomputed as the review pass
        // advances.
        //
        // FIX ROUND 5 (2026-08-08, "columns can't be moved and are hiding
        // text" task): the cap used to be computed ONCE here, from
        // SidePanelColumn's DECLARED Width (440) — but that column is
        // actually resizable (TriageWindow.xaml's GridSplitter), so dragging
        // it never revisited the cap. Measured directly: widening the panel
        // from 440 to 700px left the roster cap frozen at 76px both before
        // and after. Now tracked live via DataGridColumnCap.Track (the same
        // mechanism MatchMerge/BulkRename/History already use for their own
        // window-resize case), against Candidates' own live ActualWidth —
        // not SidePanelColumn's, and with no separate margin subtraction:
        // Candidates is the DockPanel's last, non-Dock'd child
        // (DockPanel.LastChildFill, TriageWindow.xaml) so it fills the
        // panel's already-margin-reduced content area on its own; its
        // ActualWidth is already net of the DockPanel's own Margin="12" on
        // each side. DataGridColumnCap.Track further reserves
        // SystemParameters.VerticalScrollBarWidth from that before this
        // formula ever sees it (a long candidate list scrolls), so
        // SafetyMargin below only has to cover the grid's own border/padding.
        var couldShowWhy = items.Any(i => i.Status == "suggested");
        var cappedColumnCount = Math.Max(1, _headers.Count - 1);

        double ComputeRosterColumnCap(double viewportWidth)
        {
            var rosterBudget = Math.Max(FillerMinWidth,
                viewportWidth - SafetyMargin - (couldShowWhy ? WhyColumnWidth : 0));
            return Math.Max(20, (rosterBudget - FillerMinWidth) / cappedColumnCount);
        }

        var cappedRosterColumns = new List<DataGridColumn>();
        for (var i = 0; i < _headers.Count; i++)
        {
            var h = _headers[i];
            var isFiller = i == 0;
            var column = new DataGridTextColumn
            {
                Header = h,
                Binding = new Binding($"[{h}]"),
                Width = isFiller
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : DataGridLength.Auto,
                // BasedOn GridCellTextSelectionAware (Theme/Styles.xaml), the
                // shared style MatchMergeWindow's File/Becomes and
                // BulkRenameWindow's Current name XAML columns also use —
                // this column carries no Foreground trigger of its own, so
                // the shared style's default Theme.Text plus its trailing
                // "let selection win" DataTrigger is the whole selection-
                // contrast story for a roster column, the same as it is for
                // a plain XAML column with no status trigger. Before
                // GridCellText existed, this style deliberately had NO
                // BasedOn at all — a documented workaround (this column had
                // no Foreground Setter of its own to conflict with the
                // cell's own IsSelected inheritance), not a fix; using the
                // shared selection-aware style now makes this column's story
                // identical to every other plain one instead of relying on
                // the absence of a Setter to stay correct by accident.
                ElementStyle = new Style(typeof(TextBlock), GridCellTextSelectionAwareStyle)
                {
                    Setters =
                    {
                        new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                        new Setter(FrameworkElement.ToolTipProperty, new Binding($"[{h}]")),
                    },
                },
            };
            if (isFiller) column.MinWidth = FillerMinWidth;
            else cappedRosterColumns.Add(column);
            Candidates.Columns.Add(column);
        }
        // Track live (see FIX ROUND 5 note above) rather than setting a
        // one-shot MaxWidth on each capped column here — this is also where
        // a user's own manual column resize starts winning over the cap
        // (DataGridColumnCap's own doc comment).
        DataGridColumnCap.Track(Candidates, ComputeRosterColumnCap, cappedRosterColumns.ToArray());
        Loaded += async (_, _) => await InitAndShowAsync(_pdf.InitAsync);
    }

    /// <summary>The Loaded flow: init the viewer, warn on failure, show the
    /// first document — each step re-checked against <see cref="IsClosed"/>
    /// first, since the window can close (Escape → "Stop reviewing",
    /// IsCancel="True") while any one of these is still in flight. Takes the
    /// init call as a delegate (rather than calling <c>_pdf.InitAsync</c>
    /// directly) so a test can control exactly when it resolves without
    /// needing a real, untimeable WebView2 startup.</summary>
    internal async Task InitAndShowAsync(Func<Task<bool>> initAsync)
    {
        var ok = await initAsync();
        if (IsClosed) return;   // closed while InitAsync was pending — Viewer's already disposed
        if (!ok)
            Dialogs.Warn(
                "The PDF viewer (WebView2) failed to start:\n\n" + _pdf.InitError,
                "OrdoSort");
        if (IsClosed) return;   // closed during the warning's modal loop, or between the two checks
        await ShowCurrentAsync();
    }

    private MatchMerge.MatchResult? Current => _index < _items.Count ? _items[_index] : null;

    internal async Task ShowCurrentAsync()
    {
        // the decision path (UseSelectedAsync, after its ReleaseAsync await)
        // and OnSkip both call back in here — this is the one place that
        // guards all of them against a window that closed (and disposed
        // Viewer) while they were suspended, rather than re-checking
        // IsClosed at every call site
        if (IsClosed) return;
        var r = Current;
        if (r is null) { Close(); return; }
        Progress.Text = $"{_index + 1} / {_items.Count}";
        FileName.Text = Path.GetFileName(r.Source);
        Note.Text = "";
        await _pdf.ShowAsync(r.Source);

        // suggested items get a leading "Why" column — every candidate carries
        // its own reason, and a match you can't explain is one you can't trust
        var why = r.Status == "suggested";
        if (why != _whyColumnShown)
        {
            if (why) Candidates.Columns.Insert(0, new DataGridTextColumn
            {
                Header = "Why",
                Binding = new Binding("[__why]"),
                Width = new DataGridLength(WhyColumnWidth),
                // BasedOn plain GridCellText, NOT GridCellTextSelectionAware,
                // for the shared Theme.Text default and TextTrimming/ToolTip
                // plumbing every other grid column shares — the trailing
                // "let selection win" DataTrigger below still has to stay
                // local to THIS style too (it can't delegate to
                // GridCellTextSelectionAware): this column's own
                // unconditional SubtleText Setter, just below, is a Style
                // Setter on the same TextBlock and would otherwise keep
                // outranking whatever a base-level trigger hands it — see
                // GridCellTextSelectionAware's own doc comment in
                // Theme/Styles.xaml.
                ElementStyle = new Style(typeof(TextBlock), GridCellTextStyle)
                {
                    Setters =
                    {
                        new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                        // status-colour-vocabulary plan (2026-08-08), Task 2 Step 2:
                        // every row here is the same kind of message — a token-pass
                        // suggestion's reason, purely informational (nothing to fix,
                        // nothing blocking) — so one flat subtle Setter, not a
                        // DataTrigger keyed on a status this column has no such
                        // binding for. Fixed width (WhyColumnWidth above), so this
                        // adds no pixels to the 397px/416px budget the class doc
                        // measures.
                        new Setter(TextBlock.ForegroundProperty,
                            new DynamicResourceExtension("Theme.SubtleText")),
                    },
                    Triggers =
                    {
                        // Let selection win — same measured trap and fix as
                        // MatchMergeWindow.xaml's/BulkRenameWindow.xaml's Note
                        // columns: Theme.SubtleText against Theme.Accent (the
                        // selected cell's background) fails 4.5:1 (measured
                        // ~1.85:1 light), so a selected Why cell must revert to
                        // Theme.AccentText, the same value the cell's own
                        // IsSelected trigger already paints its background for.
                        new DataTrigger
                        {
                            Binding = new Binding("IsSelected")
                            {
                                RelativeSource = new RelativeSource(
                                    RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1),
                            },
                            Value = true,
                            Setters =
                            {
                                new Setter(TextBlock.ForegroundProperty,
                                    new DynamicResourceExtension("Theme.AccentText")),
                            },
                        },
                    },
                },
            });
            else Candidates.Columns.RemoveAt(0);
            _whyColumnShown = why;
        }
        var candidates = CandidatesOf(r);
        Candidates.ItemsSource = candidates
            .Select((c, i) =>
            {
                // a loop, not ToDictionary: the roster's own header list can
                // carry a duplicate name (or, before the picker deduped its
                // own output, ChosenColumns could too) — last value wins
                // rather than crashing Review matches over it
                var row = new Dictionary<string, string>();
                foreach (var h in _headers)
                    row[h] = c.Row.TryGetValue(h, out var v) ? v : "";
                if (why) row["__why"] = r.Suggestions![i].Reason;
                return row;
            })
            .ToList();
        UseButton.IsEnabled = false;
    }

    /// <summary>Both queues, one shape: ambiguous files carry Candidates,
    /// suggested ones carry Suggestions (ranked, with reasons).</summary>
    private static IReadOnlyList<MatchMerge.Candidate> CandidatesOf(MatchMerge.MatchResult r) =>
        r.Status == "suggested"
            ? r.Suggestions!.Select(s => s.Candidate).ToList()
            : r.Candidates!;

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e) =>
        UseButton.IsEnabled = Candidates.SelectedIndex >= 0;

    private void OnCandidateAccept(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Candidates.SelectedIndex >= 0) OnUseSelected(sender, e);
    }

    /// <summary>The whole window is the keyboard surface — there is nothing to
    /// type here, so keys can be verbs: a digit picks a candidate row, Enter
    /// confirms it, S skips the file. A long review run should never need the
    /// mouse.</summary>
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // bare keys only — Ctrl+S (save-as-muscle-memory) must not read as
        // "skip", and Alt/Shift combos aren't verbs here either
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;

        var key = e.Key;
        var digit = key >= System.Windows.Input.Key.D1 && key <= System.Windows.Input.Key.D9
            ? key - System.Windows.Input.Key.D1
            : key >= System.Windows.Input.Key.NumPad1 && key <= System.Windows.Input.Key.NumPad9
                ? key - System.Windows.Input.Key.NumPad1
                : -1;
        if (digit >= 0 && digit < Candidates.Items.Count)
        {
            Candidates.SelectedIndex = digit;
            Candidates.ScrollIntoView(Candidates.SelectedItem);
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Enter && Candidates.SelectedIndex >= 0)
        {
            e.Handled = true;
            OnUseSelected(sender, e);
        }
        else if (key == System.Windows.Input.Key.S)
        {
            e.Handled = true;
            OnSkip(sender, e);
        }
    }

    private async void OnUseSelected(object sender, RoutedEventArgs e) =>
        await UseSelectedAsync(_pdf.ReleaseAsync);

    /// <summary>The Enter/click decision path. Takes the release call as a
    /// delegate — same reason as <see cref="InitAndShowAsync"/>'s
    /// <c>initAsync</c> parameter — so a test can hold it open across a
    /// Close() without a real, untimeable WebView2 release. <c>ReleaseAsync</c>
    /// is the one await here with genuine (up to 2s) latency, so it's the
    /// only point where "Stop reviewing" (Escape, IsCancel="True") can land
    /// mid-flight; MergeOne still runs when it resumes — the rename is
    /// correct and must complete either way, window open or not — and only
    /// <see cref="ShowCurrentAsync"/>'s own <see cref="IsClosed"/> guard
    /// decides whether to touch the (by then possibly disposed) Viewer
    /// afterward.</summary>
    internal async Task UseSelectedAsync(Func<Task> releaseAsync)
    {
        var r = Current;
        if (_busy || r is null || Candidates.SelectedIndex < 0) return;
        _busy = true;
        try
        {
            var candidate = CandidatesOf(r)[Candidates.SelectedIndex];

            await releaseAsync();   // Edge lets go of the file before the rename
            var outcomes = MatchMerge.MergeOne(r.Source, candidate.ControlId);
            if (outcomes[0].Final is null)
            {
                // ShowCurrentAsync clears Note.Text as part of showing the
                // (unchanged) current file — set the message AFTER it runs,
                // or it never survives to be seen
                var message = "Couldn't rename: " + outcomes[0].Error;
                await ShowCurrentAsync();
                Note.Text = message;
                return;
            }
            Outcomes.AddRange(outcomes);
            _index++;
            await ShowCurrentAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _index++;
            await ShowCurrentAsync();
        }
        finally
        {
            _busy = false;
        }
    }
}
