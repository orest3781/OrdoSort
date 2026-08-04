using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Windows;

/// <summary>One file at a time: ambiguous matches and token-pass
/// suggestions; pick the row the document belongs to, or skip. The PDF
/// appears in Edge (left) and every candidate's full roster row on the
/// right. The viewer's handle is released before every rename — same
/// contract as the filing loop.</summary>
public partial class TriageWindow : Window
{
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
        foreach (var h in _headers)
            Candidates.Columns.Add(new DataGridTextColumn
            {
                Header = h,
                Binding = new Binding($"[{h}]"),
            });
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
                Width = new DataGridLength(260),
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) },
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
