using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>
/// The filing loop (spec docs/superpowers/specs/2026-09-29-filing-loop-design.md).
///
/// The only thing a key press waits for is the next page on screen. The
/// document just left is moved behind it, one at a time and in order, by
/// <see cref="RunWorkerAsync"/>; the deferred-folder scan and the name
/// suggestions refresh after that, coalesced. The next documents are copied
/// to the local disk while the user reads (<see cref="IDocumentStage"/>), so
/// the preview never waits on the share and never holds an inbox file open.
///
/// What does not change: every move is logged, every failure is reported
/// on the document it is about, Undo undoes the last press, and Stop or
/// closing the window lets every press already made land first.
///
/// Everything here runs on the UI thread; only the moves themselves (and
/// the reads behind the side refresh) go through the work scheduler.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>How many documents may be left on screen but not yet moved.
    /// A press beyond it waits for the oldest move: on a dead share the user
    /// is slowed down, never allowed to run far ahead of what has landed.</summary>
    internal const int MaxInFlight = 3;

    /// <summary>How long a document's local copy is waited for before the
    /// inbox file is shown instead. A copy still running is usually the
    /// faster way to the page, since its bytes are already on their way.</summary>
    internal static readonly TimeSpan StageWait = TimeSpan.FromSeconds(1);

    /// <summary>How long a move waits for a copy of its document still being
    /// made (the copy holds the inbox file open) before trying anyway.</summary>
    internal static readonly TimeSpan LetGoWait = TimeSpan.FromSeconds(30);

    internal const string ChangedSinceShownMessage =
        "This document changed in the inbox after it was shown, so it was not filed. " +
        "It is on screen again as it is now: check it, then file it again.";

    /// <summary>One press: the document it was for and what was asked.</summary>
    private sealed class FilingJob
    {
        public FilingJob(int queueIndex, string path, string typed, Route? route, int? routeIndex)
        {
            QueueIndex = queueIndex;
            Path = path;
            Typed = typed;
            Route = route;
            RouteIndex = routeIndex;
        }

        public int QueueIndex { get; }
        public string Path { get; }
        public string Typed { get; }
        /// <summary>Null for a set-aside.</summary>
        public Route? Route { get; }
        public int? RouteIndex { get; }
        public bool IsSetAside => Route is null;
        public bool ShownFromCopy { get; init; }
        public double ShownMs { get; set; }
        /// <summary>Set the moment the move is handed to the scheduler; a
        /// press not yet at that point can still be dropped safely.</summary>
        public bool MoveStarted { get; set; }

        /// <summary>Done when the move has landed, failed, or been dropped.</summary>
        public TaskCompletionSource Landed { get; } = new();
    }

    // Presses waiting to move, oldest first; the head is the one moving.
    private readonly LinkedList<FilingJob> _jobs = new();
    private bool _workerRunning;
    private Task _worker = Task.CompletedTask;

    // True for the whole of a press; a second press in that moment would
    // file what the first one captured.
    private bool _pressing;
    // Presses taking their document off the screen, not yet queued to move.
    private int _leavingPresses;
    // A failed move is putting its document back on screen.
    private bool _recovering;
    private bool _stopWhenLanded;
    // Bumped whenever a failure sends the screen back to an earlier document.
    private int _rewinds;
    // The viewer's last release: a move must not start while Edge may still
    // have its file open.
    private Task _release = Task.CompletedTask;

    /// <summary>Where the screen is in the session's queue. Runs ahead of
    /// <see cref="Session.Pos"/> by the presses still moving.</summary>
    private int _shownIndex;

    /// <summary>What the viewer was last pointed at: the inbox file or its
    /// local copy. Null once released.</summary>
    private string? _shownSource;

    // Names typed for documents the screen had to go back past (a failed
    // move rewinds it); each is put back when its document shows again.
    private readonly Dictionary<string, string> _carriedNames = new(PathIdentity.PathComparer.Instance);

    private IDocumentStage? _stage;
    private bool _stageUnavailable;
    private readonly Func<IDocumentStage>? _stageFactory;

    private bool _sideRefreshRunning;
    private bool _sideRefreshAgain;

    /// <summary>The document on screen, or null past the end of the queue.</summary>
    internal string? ShownPath
    {
        get
        {
            var queue = _session.Queue;
            var index = _shownIndex;
            return index >= 0 && index < queue.Count ? queue[index] : null;
        }
    }

    /// <summary>The background half of every press still to land, then the
    /// session's end if it ended. Tests and the window wait on it.</summary>
    internal Task FilingLanded => _worker;

    /// <summary>How many presses have not landed yet.</summary>
    internal int PendingFilings => _jobs.Count + _leavingPresses;

    /// <summary>A move, an undo or a session scan is under way, or a press
    /// is on its way to being one. Rescan and a new session wait for it.</summary>
    private bool FilingInProgress => _busy || _workerRunning || _jobs.Count > 0 || _leavingPresses > 0;

    /// <summary>Only the document whose name is in the box can be filed:
    /// never one the screen is still on its way to, or past the end.</summary>
    private bool CanPress() =>
        !_busy && !_pressing && !_recovering && !_stopWhenLanded && !_disposed
        && Screen == Screen.Processing
        && ShownPath is { } shown && PathIdentity.Same(shown, _loadedPath);

    // ------------------------------------------------------------ presses

    internal async Task OnRouteAsync(int index)
    {
        // Index into the session's own buttons, not _cfg.Routes: a mid-session
        // save can replace _cfg.Routes with a peer's reordered list, and the
        // button (and hotkey) the user pressed must still file where it says.
        if (!CanPress() || index < 0 || index >= Routes.Count) return;
        if (!Routes[index].Enabled) return;
        await PressAsync(Routes[index].Route, index);
    }

    internal Task OnSkipAsync() => CanPress() ? PressAsync(route: null, routeIndex: null) : Task.CompletedTask;

    private async Task PressAsync(Route? route, int? routeIndex)
    {
        // set BEFORE the first await, so a fast second press can't capture
        // this document's name for the next one
        _pressing = true;
        try
        {
            var pressed = Stopwatch.GetTimestamp();
            var pressedFor = ShownPath;
            var rewinds = _rewinds;
            while (_jobs.First is { } oldest && _jobs.Count >= MaxInFlight)
                await oldest.Value.Landed.Task;
            if (_disposed || _stopWhenLanded || Screen != Screen.Processing) return;
            // A failure while this press waited sent the screen back to an
            // earlier document: the press was for one no longer showing.
            if (_rewinds != rewinds || ShownPath is not { } path || !PathIdentity.Same(path, pressedFor)) return;

            var job = new FilingJob(_shownIndex, path, TypedName, route, routeIndex)
            {
                ShownFromCopy = _shownSource is not null && !PathIdentity.Same(_shownSource, path),
            };
            if (!job.IsSetAside) RememberName(job.Typed);
            // A name the filing will refuse (a colon, a mode the file can't
            // take) is filed where it stands, as before: the refusal then
            // comes up on the document it is about, with the name still in
            // the box, rather than one document later.
            if (!job.IsSetAside && !NameIsAccepted(path, job.Typed, route))
            {
                Enqueue(job);
                await job.Landed.Task;
                return;
            }

            if (routeIndex is { } pressedRoute)
            {
                // Enter's badge follows the press at once, not the move
                _lastRoute = pressedRoute;
                MarkRouteState();
            }
            // the copy of this document is kept for the check before its move
            _ = _stage?.LetGoAsync(path, TimeSpan.Zero);
            _leavingPresses++;
            try
            {
                await ShowNextAsync(job);
            }
            catch (Exception)
            {
                // An unforeseen fault on the way off the page (the viewer):
                // the press was never queued, so nothing has moved. The screen
                // goes back to the document, name and all, and the command
                // reports the fault.
                RestoreScreenTo(job);
                throw;
            }
            finally
            {
                _leavingPresses--;
            }
            job.ShownMs = Stopwatch.GetElapsedTime(pressed).TotalMilliseconds;
            Enqueue(job);
        }
        finally
        {
            _pressing = false;
        }
    }

    private void Enqueue(FilingJob job)
    {
        _jobs.AddLast(job);
        RaiseUndoState();
        KickWorker();
    }

    /// <summary>Back to the document a press was for, as it was before the
    /// press: no viewer call, so it cannot fail the same way again.</summary>
    private void RestoreScreenTo(FilingJob job)
    {
        _shownIndex = job.QueueIndex;
        _loadedPath = job.Path;
        CurrentFilename = Path.GetFileName(job.Path);
        ShowKnownTaken(job.Path);
        _typedName = job.Typed;
        ResetCycle();
        Raise(nameof(TypedName));
        RefreshSuggestions();
        UpdatePreview();
        RaiseProgress();
        RaiseUndoState();
    }

    /// <summary>The same target the move will build: false only when it
    /// would refuse the name. No disk or network access.</summary>
    private bool NameIsAccepted(string path, string typed, Route? route)
    {
        try
        {
            Naming.BuildTarget(Path.GetFileName(path), typed, route?.NamingMode, _session.SessionMode,
                route?.Suffix ?? "", route?.AppendSuffix ?? false, _ => false,
                IsMedia(path) ? StampPending : null);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The just-used name is suggestable on the very next document,
    /// before the history rebuild that also adds it has run.</summary>
    private void RememberName(string typed)
    {
        if (Naming.IsBlankName(typed)) return;
        var name = Polish(typed);
        if (!_allNames.Contains(name, StringComparer.OrdinalIgnoreCase)) _allNames.Insert(0, name);
    }

    /// <summary>Takes the pressed document off the screen and shows the next.</summary>
    private async Task ShowNextAsync(FilingJob job)
    {
        // Shown straight from the inbox (no local copy): Edge must let go of
        // that file before it moves, exactly as before the read-ahead.
        if (IsShownFromInbox(job.Path)) await ReleaseViewerAsync();
        _shownIndex = job.QueueIndex + 1;
        await LoadCurrentAsync();
    }

    private bool IsShownFromInbox(string path) =>
        _shownSource is not null && PathIdentity.Same(_shownSource, path);

    private Task ReleaseViewerAsync()
    {
        _shownSource = null;
        _release = ReleaseShownAsync();
        return _release;
    }

    /// <summary>Waits out a release another step started. Its failure was
    /// reported where it happened, so it is not reported twice.</summary>
    private async Task SettleReleaseAsync()
    {
        try { await _release; }
        catch (Exception) { }
    }

    // ------------------------------------------------------------ the screen

    internal async Task LoadCurrentAsync()
    {
        var path = ShownPath;
        if (path is null)
        {
            if (_jobs.Count == 0 && _leavingPresses == 0) { ShowDone(); return; }
            // past the last document with moves still landing: an empty pane
            // until they have, then Done (AfterLandedAsync)
            _loadedPath = null;
            CurrentFilename = "";
            _typedName = "";
            ResetCycle();
            Raise(nameof(TypedName));
            RefreshSuggestions();
            UpdatePreview();
            RaiseProgress();
            if (_shownSource is not null) await ReleaseViewerAsync();
            return;
        }
        RaiseProgress();
        CurrentFilename = Path.GetFileName(path);
        ShowKnownTaken(path);
        if (path != _loadedPath)
        {
            _loadedPath = path;
            // the FIELD, not the property — so the setter's bookkeeping is skipped
            // and the frozen suggestion walk has to be cleared by hand, or the
            // next ↓ would offer this document the previous one's names. A
            // document the screen came back to gets back what was typed for it.
            _typedName = _carriedNames.Remove(path, out var carried) ? carried : "";
            ResetCycle();
            Raise(nameof(TypedName));
        }
        else
        {
            // still on screen: what is in the box is what the user wants now
            _carriedNames.Remove(path);
        }
        RefreshSuggestions();
        UpdatePreview();
        RaiseUndoState();
        // Esc closed the session: a document shown now would sit in a hidden
        // preview, which keeps its file open
        if (Screen != Screen.Processing) return;
        RequestNameFocus?.Invoke();
        var copy = await LocalCopyOrInboxAsync(path);
        var media = IsMedia(path);
        var video = IsVideo(path);
        var source = media && !video ? await PreviewSourceAsync(path, copy) : copy;
        // moved on, or closed, while the copy was waited for: that load shows its own
        if (Screen != Screen.Processing || !PathIdentity.Same(ShownPath, path)) return;
        _shownSource = source;
        if (video)
        {
            await ShowVideoAsync(source);
        }
        else
        {
            LeaveVideo();
            // a photo fits the pane by its own size; the PDF zoom is for PDFs
            await _viewer.ShowAsync(source, media ? null : _sessionPage);
        }
        // Edge can take the focus as it opens a document
        RequestNameFocus?.Invoke();
        if (media) _ = ShowTakenAsync(path, copy);
        PrepareNextMedia();
    }

    /// <summary>The document's local copy when there is one in time,
    /// otherwise the inbox file. Never throws: the read-ahead is a speed-up,
    /// and nothing about it may stand between the user and the document.</summary>
    private async Task<string> LocalCopyOrInboxAsync(string path)
    {
        if (_disposed || _stageFactory is null) return path;
        try
        {
            if (_stage is null && !_stageUnavailable)
            {
                try
                {
                    _stage = _stageFactory();
                }
                catch (Exception)
                {
                    // said once per session, not once per document
                    _stageUnavailable = true;
                    throw;
                }
            }
            if (_stage is not { } stage) return path;
            stage.Want(StageWindow());
            return await stage.CopyForAsync(path, StageWait) ?? path;
        }
        catch (Exception ex)
        {
            UnexpectedError?.Invoke(ex);
            return path;
        }
    }

    /// <summary>What the read-ahead keeps: the document on screen and the
    /// next two. Documents on their way out are not listed: their copies are
    /// kept by <see cref="IDocumentStage.LetGoAsync"/> until they move.</summary>
    private List<string> StageWindow()
    {
        var queue = _session.Queue;
        var window = new List<string>();
        for (var index = _shownIndex; index < _shownIndex + 3 && index < queue.Count; index++)
            if (index >= 0) window.Add(queue[index]);
        return window;
    }

    private void DisposeStage()
    {
        var stage = _stage;
        _stage = null;
        try { stage?.Dispose(); }
        catch (Exception ex) { UnexpectedError?.Invoke(ex); }
    }

    // ------------------------------------------------------------ the moves

    private void KickWorker()
    {
        if (_workerRunning || _jobs.Count == 0) return;
        _worker = RunWorkerAsync();
    }

    /// <summary>Moves the presses, oldest first, one at a time: two moves on
    /// one share at once are slower than one after another, and the history
    /// log stays in the order the user pressed.</summary>
    private async Task RunWorkerAsync()
    {
        _workerRunning = true;
        try
        {
            while (!_disposed && _jobs.First is { } node)
            {
                var job = node.Value;
                try
                {
                    await FileOneAsync(job);
                }
                catch (Exception ex)
                {
                    await RecoverFromFaultAsync(job, ex);
                }
                if (node.List is not null) _jobs.Remove(node);
                job.Landed.TrySetResult();
                RaiseUndoState();
            }
        }
        finally
        {
            _workerRunning = false;
        }
        try
        {
            if (!_disposed) await AfterLandedAsync();
        }
        catch (Exception ex)
        {
            ReportUnexpected(ex, "Finishing that session");
        }
    }

    private async Task FileOneAsync(FilingJob job)
    {
        // The session files in queue order, so the head job is always its
        // current document. Anything else means the two have drifted apart
        // (nothing known does that); the screen then follows the session.
        if (!PathIdentity.Same(_session.Current, job.Path))
        {
            await RewindAndShowAsync(job, message: null, title: "");
            return;
        }
        var releaseStarted = Stopwatch.GetTimestamp();
        if (IsShownFromInbox(job.Path)) await ReleaseViewerAsync();
        else await SettleReleaseAsync();
        if (_stage is { } stage)
        {
            await stage.LetGoAsync(job.Path, LetGoWait);
            // what is about to move must be what the user looked at
            if (job.ShownFromCopy && !await stage.IsUnchangedAsync(job.Path))
            {
                stage.Discard(job.Path);
                if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
                await RewindAndShowAsync(job, ChangedSinceShownMessage, "OrdoSort — not filed");
                return;
            }
        }
        var releaseMs = Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds;
        // closed while this waited: the document stays in the inbox, and the
        // history (already closing) is never written to
        if (_disposed) return;
        var moveStarted = Stopwatch.GetTimestamp();
        try
        {
            if (job.IsSetAside) await SetAsideAsync(job);
            else await FileToRouteAsync(job);
        }
        catch (AuditError ex)
        {
            // the move already happened and the queue moved with it — report
            // and carry on rather than reloading a document that isn't there
            if (job.RouteIndex is { } routeIndex)
            {
                _lastRoute = routeIndex;
                MarkRouteState();
            }
            HideLastAction();
            ReportAuditFailure(ex, job.IsSetAside
                ? "OrdoSort — set aside, but not recorded"
                : "OrdoSort — filed, but not recorded");
        }
        catch (CommitError ex)
        {
            // nothing moved: the document comes back, with its name
            if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
            await RewindAndShowAsync(job, ex.Message,
                job.IsSetAside ? "OrdoSort — set-aside failed" : "OrdoSort — couldn't file it");
            return;
        }
        _stage?.Discard(job.Path);
        if (_disposed) return;
        LogTiming(job, releaseMs, Stopwatch.GetElapsedTime(moveStarted).TotalMilliseconds);
        ScheduleSideRefresh();
    }

    private async Task FileToRouteAsync(FilingJob job)
    {
        job.MoveStarted = true;
        // the move itself can be a copy+delete across SMB shares — never on the UI thread
        var outcome = await RunFiling(() => _session.CommitCurrent(job.Typed, job.Route!, MediaTargetFor(job.Path)));
        // the window closed while this was moving: nothing left to show (QC-19)
        if (_disposed) return;
        _lastRoute = job.RouteIndex;
        MarkRouteState();
        if (outcome.Vanished)
        {
            ShowStatusNote("That file disappeared from the inbox — logged and moved on.");
            return;
        }
        ForgetMedia(job.Path);
        var back = Theme.ThemePalette.ParseColor(job.Route!.Color) ?? _palette().Success;
        ShowLastAction($"✓  Filed to {job.Route.Label}", Path.GetFileName(outcome.NewPath!), back);
    }

    private async Task SetAsideAsync(FilingJob job)
    {
        job.MoveStarted = true;
        var outcome = await RunFiling(() => _session.SkipCurrent());
        if (_disposed) return;
        if (outcome.Vanished)
        {
            ShowStatusNote("That file disappeared from the inbox — logged and moved on.");
            return;
        }
        ShowLastAction("✓  Set aside for later", Path.GetFileName(outcome.NewPath!), _palette().Warning);
        if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.SetAside, _cfg.Sounds.SetAside);
    }

    /// <summary>A fault nothing expected: reported as before, and the screen
    /// goes back to wherever the session actually is.</summary>
    private async Task RecoverFromFaultAsync(FilingJob job, Exception ex)
    {
        ReportUnexpected(ex, job.IsSetAside ? "Setting that document aside" : "Filing that document");
        if (_disposed) return;
        await RewindAndShowAsync(job, message: null, title: "");
    }

    /// <summary>Puts the session's current document back on screen, then
    /// says why. Presses are held off until the message is up, so nothing
    /// typed or pressed in between lands on the wrong document.</summary>
    private async Task RewindAndShowAsync(FilingJob failed, string? message, string title)
    {
        Rewind(failed);
        _recovering = true;
        try
        {
            await LoadCurrentAsync();
            if (message is not null && !_disposed) _dialogs.Warn(message, title);
        }
        finally
        {
            _recovering = false;
        }
    }

    /// <summary>The screen goes back to the session's current document.
    /// Every press queued behind <paramref name="failed"/> was for a document
    /// the session has not reached: they are dropped, and each keeps the name
    /// typed for it, as does the document that was on screen.</summary>
    private void Rewind(FilingJob failed)
    {
        _rewinds++;
        if (_loadedPath is { } shown && TypedName.Length > 0) _carriedNames[shown] = TypedName;
        var node = _jobs.First;
        while (node is not null)
        {
            var next = node.Next;
            var job = node.Value;
            if (job.Typed.Length > 0) _carriedNames[job.Path] = job.Typed;
            if (!ReferenceEquals(job, failed))
            {
                _jobs.Remove(node);
                job.Landed.TrySetResult();
            }
            node = next;
        }
        if (failed.Typed.Length > 0) _carriedNames[failed.Path] = failed.Typed;
        _shownIndex = _session.Pos;
        RaiseUndoState();
    }

    /// <summary>Every press has landed: finish a Stop that was waiting, or
    /// the session if the screen has run past its last document.</summary>
    private async Task AfterLandedAsync()
    {
        // a press is still taking its document off the screen: it will queue
        // its move and bring the worker (and this) round again
        if (_leavingPresses > 0) return;
        if (_stopWhenLanded)
        {
            _stopWhenLanded = false;
            if (Screen == Screen.Processing)
            {
                ClearStatus();
                Rescan();
            }
            return;
        }
        // Past the end (Done), or new arrivals joined while the last move
        // landed (ApplySnapshot): show whichever document is now current.
        if (Screen == Screen.Processing
            && (ShownPath is null || !PathIdentity.Same(ShownPath, _loadedPath)))
            await LoadCurrentAsync();
    }

    // ------------------------------------------------------------ side work

    /// <summary>The deferred-folder count and the name suggestions, after
    /// the page is up. Overlapping requests coalesce: one runs, and one more
    /// runs after it if another was asked for meanwhile.</summary>
    private void ScheduleSideRefresh()
    {
        if (_sideRefreshRunning)
        {
            _sideRefreshAgain = true;
            return;
        }
        _ = RunSideRefreshAsync();
    }

    private async Task RunSideRefreshAsync()
    {
        _sideRefreshRunning = true;
        try
        {
            do
            {
                _sideRefreshAgain = false;
                await RefreshDeferredAsync();
                await RefreshCompleterAsync();
            }
            while (_sideRefreshAgain && !_disposed);
        }
        catch (Exception ex)
        {
            // a convenience: crash.log has it, the filing loop carries on
            UnexpectedError?.Invoke(ex);
        }
        finally
        {
            _sideRefreshRunning = false;
        }
    }

    private void LogTiming(FilingJob job, double releaseMs, double moveMs)
    {
        if (!_cfg.Timing) return;
        FilingTimingLog.Append(FilingTimingLog.Line(DateTime.Now, job.IsSetAside, job.ShownMs,
            job.ShownFromCopy, releaseMs, moveMs));
    }

    // ------------------------------------------------------------ undo, stop, close

    internal void OnUndo() => _ = OnUndoAsync();

    internal async Task OnUndoAsync()
    {
        if (_busy || (Screen != Screen.Processing && Screen != Screen.Done)) return;
        if (_pressing)
        {
            ShowStatusNote("Still filing — press Undo again in a moment.");
            return;
        }
        if (_jobs.Count > 0 || _workerRunning)
        {
            // The press being undone has to land first. One that failed
            // instead has nothing to undo, and undoing the filing before it
            // would surprise.
            _pressing = true;
            var rewinds = _rewinds;
            try
            {
                while (_jobs.Count > 0 || _workerRunning)
                {
                    KickWorker();
                    await _worker;
                }
            }
            finally { _pressing = false; }
            if (_rewinds != rewinds || _disposed) return;
            if (Screen != Screen.Processing && Screen != Screen.Done) return;
        }
        if (!_session.CanUndo) { ShowStatusNote("Nothing to undo."); return; }
        _busy = true;
        try
        {
            try
            {
                var (filed, original) = await RunFiling(() => _session.UndoLast());
                // the window closed while this was moving: nothing left to show (QC-19)
                if (_disposed) return;
                ShowStatusNote($"Undid {Path.GetFileName(filed)} → {Path.GetFileName(original)}");
                HideLastAction();   // the card must never claim an undone filing
            }
            catch (AuditError ex)
            {
                // the file is back; only the history row is stale
                HideLastAction();
                ReportAuditFailure(ex, "OrdoSort — undone, but still logged as filed");
            }
            catch (CommitError ex) when (ex.LeftBothCopies)
            {
                // The document is back and the session says so; only the
                // filed copy would not delete. An undo, with a warning: the
                // screen follows the session back to it below (Q2-04).
                HideLastAction();
                _dialogs.Warn(ex.Message, "OrdoSort — undone, but a copy remains");
            }
            catch (CommitError ex)
            {
                _dialogs.Warn(ex.Message, "OrdoSort — undo failed");
                return;
            }
            _shownIndex = _session.Pos;   // the restored document is current again
            if (Screen == Screen.Done)   // undo from Done re-enters the session
                Screen = Screen.Processing;
            await LoadCurrentAsync();
            ScheduleSideRefresh();   // the deferred count, and a reverted name may drop out
        }
        finally { _busy = false; }
    }

    /// <summary>Esc: back to Ready. Nothing is lost — the remaining queue
    /// stays in the inbox, and presses already made land first.</summary>
    internal void StopSession()
    {
        if (Screen != Screen.Processing || _busy) return;
        if (_jobs.Count > 0 || _workerRunning || _leavingPresses > 0)
        {
            _stopWhenLanded = true;
            return;
        }
        ClearStatus();
        Rescan();
    }

    /// <summary>A session starting: the screen at its first document, no
    /// press or copy left over from the last one.</summary>
    private void ResetFilingLoop()
    {
        _shownIndex = 0;
        _shownSource = null;
        _stopWhenLanded = false;
        _stageUnavailable = false;
        _arrivalsWaiting = 0;
        _carriedNames.Clear();
        DisposeStage();
    }

    /// <summary>The window is going: presses whose move has not started stay
    /// in the inbox, unmoved and unlogged, and nothing may wait on them. The
    /// one moving now lands (Dispose waits for it before closing History).
    /// Their number goes to crash.log so a user told "filed" can be told why
    /// not: no document names (QC-21).</summary>
    private void DropUnstartedPresses()
    {
        var dropped = 0;
        var node = _jobs.First;
        while (node is not null)
        {
            var next = node.Next;
            if (!node.Value.MoveStarted)
            {
                _jobs.Remove(node);
                node.Value.Landed.TrySetResult();
                dropped++;
            }
            node = next;
        }
        dropped += _leavingPresses;
        if (dropped > 0)
            UnexpectedError?.Invoke(new OperationCanceledException(
                $"OrdoSort closed with {dropped} pressed document{(dropped == 1 ? "" : "s")} not yet filed; " +
                "they are still in the inbox, not filed and not in History."));
    }
}
