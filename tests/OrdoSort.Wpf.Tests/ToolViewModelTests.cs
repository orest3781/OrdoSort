using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

public class UnlockViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordounlock_" + Guid.NewGuid());
    private readonly Config _cfg = new();
    private int _saves;

    public UnlockViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private UnlockViewModel Vm() => new(_cfg, () => _saves++);

    private string MakeEncrypted(string name, string userPw = "secret")
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task UnlocksInPlaceWithTheRightPassword()
    {
        var vm = Vm();
        var path = MakeEncrypted("locked.pdf");
        await vm.AddFilesAsync(new[] { path });
        vm.Password = "secret";
        await vm.UnlockAsync();
        var line = Assert.Single(vm.ResultLines);
        Assert.Equal(UnlockResultKind.Ok, line.Kind);
        Assert.Contains("locked.pdf — unlocked", line.Text);
        Assert.Equal("1 unlocked", vm.Summary);
    }

    [Fact]
    public async Task WrongPasswordReportsPerFileWithoutTouchingIt()
    {
        var vm = Vm();
        var path = MakeEncrypted("locked.pdf");
        var before = File.ReadAllBytes(path);
        await vm.AddFilesAsync(new[] { path });
        vm.Password = "wrong";
        await vm.UnlockAsync();
        var line = Assert.Single(vm.ResultLines);
        Assert.Equal(UnlockResultKind.Fail, line.Kind);
        Assert.StartsWith("✗", line.Text);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("1 failed", vm.Summary);
    }

    [Fact]
    public async Task AWrongPasswordIsNeverRememberedAndNeverShowsTheSaveBanner()
    {
        // a stored wrong password would just fail again silently next
        // session, so a failed attempt must not save anything AND must not
        // offer to
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });
        vm.Password = "wrong";
        await vm.UnlockAsync();
        Assert.Empty(_cfg.SavedPasswords);
        Assert.Equal(0, _saves);
        Assert.False(vm.SaveBannerVisible);
    }

    [Fact]
    public async Task UnlockingKeepsTheNameAndArchivesTheLockedOriginal()
    {
        // There is no longer a choice to make: the unlocked file always keeps
        // its name and place, and the locked one is always moved aside. The
        // suffixed-copy mode this test used to cover is gone from the app.
        var vm = Vm();
        var path = MakeEncrypted("locked.pdf");
        await vm.AddFilesAsync(new[] { path });
        vm.Password = "secret";
        await vm.UnlockAsync();

        Assert.True(File.Exists(path));                                  // same name, same place
        Assert.Empty(Directory.GetFiles(_dir, "*_unlocked*"));           // nothing renamed
        var archived = Path.Combine(OrdoSort.Core.Unlock.ArchiveFolderFor(path), "locked.pdf");
        Assert.True(File.Exists(archived));                              // original kept, not destroyed
    }

    [Fact]
    public async Task ANewWorkingTypedPasswordShowsTheSaveBanner()
    {
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });
        vm.Password = "secret";
        await vm.UnlockAsync();

        Assert.True(vm.SaveBannerVisible);
        Assert.Equal("✓ 1 unlocked with a new password — save it as:", vm.SaveBannerText);
        Assert.Equal("", vm.SaveBannerName);
    }

    [Fact]
    public async Task TheBannerNeverAppearsWhenOnlyASavedPasswordDidTheWork()
    {
        // the box stayed blank; a saved entry is what unlocked the file — the
        // typed password (there isn't one) earned nothing to offer saving
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "X", Password = PasswordVault.Protect("secret") });
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });

        await vm.UnlockAsync();

        Assert.Equal("1 unlocked", vm.Summary);
        Assert.False(vm.SaveBannerVisible);
    }

    [Fact]
    public async Task TheBannerStaysHiddenWhenTheTypedPasswordIsAlreadySaved()
    {
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "X", Password = PasswordVault.Protect("secret") });
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });
        vm.Password = "secret";   // matches the already-saved entry

        await vm.UnlockAsync();

        Assert.Equal("1 unlocked", vm.Summary);
        Assert.False(vm.SaveBannerVisible);
    }

    [Fact]
    public async Task TheBannerCountsOnlyTypedWinnersNotEveryFileInAMixedRun()
    {
        // File A is unlocked by the typed password; file B is unlocked by a
        // DIFFERENT, already-saved password. Only A's win is a typed win —
        // okViaTyped must count per-file winners, not every ok result in the
        // batch, so the banner must still say 1, not 2.
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "Saved", Password = PasswordVault.Protect("saved-pw") });
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, pw) =>
        {
            var winner = p.Contains("fileA") ? "typed-pw" : "saved-pw";
            return pw == winner
                ? new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true)
                : new OrdoSort.Core.Unlock.UnlockResult("wrong_password", p, Message: "nope");
        });
        await vm.AddFilesAsync(new[] { Touch("fileA.pdf"), Touch("fileB.pdf") });
        vm.Password = "typed-pw";

        await vm.UnlockAsync();

        Assert.Equal("2 unlocked", vm.Summary);
        Assert.True(vm.SaveBannerVisible);
        Assert.Equal("✓ 1 unlocked with a new password — save it as:", vm.SaveBannerText);
    }

    [Fact]
    public async Task SaveBannerCommandAddsTheEntryAndHidesTheBanner()
    {
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });
        vm.Password = "secret";
        await vm.UnlockAsync();
        Assert.True(vm.SaveBannerVisible);
        Assert.False(vm.SaveBannerCommand.CanExecute(null));   // no name yet

        vm.SaveBannerName = "Payer A";
        Assert.True(vm.SaveBannerCommand.CanExecute(null));
        vm.SaveBannerCommand.Execute(null);

        Assert.False(vm.SaveBannerVisible);
        Assert.Equal("", vm.SaveBannerName);
        var saved = Assert.Single(_cfg.SavedPasswords);
        Assert.Equal("Payer A", saved.Label);
        Assert.True(PasswordVault.IsProtected(saved.Password));
        Assert.Equal("secret", PasswordVault.Reveal(saved.Password));
        Assert.Same(saved, Assert.Single(vm.Saved));
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task RetypingThePasswordAfterARunHidesTheBannerButSaveStillProtectsTheWinner()
    {
        // The banner offers to save "good" because that's what unlocked the
        // file. If the box is retyped afterward, the offer must disappear —
        // AND, because RelayCommand.Execute never rechecks CanExecute, even
        // a click that slips through before the UI catches up must still
        // save the password that actually earned the offer, never whatever
        // is live in the box at click time.
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf", "good") });
        vm.Password = "good";
        await vm.UnlockAsync();
        Assert.True(vm.SaveBannerVisible);

        vm.SaveBannerName = "Payer A";
        vm.Password = "bad";   // retyped after the run, before clicking Save

        Assert.False(vm.SaveBannerVisible);      // the offer is gone the instant the box changes
        Assert.Equal("Payer A", vm.SaveBannerName);   // hide-only: the typed name itself is untouched

        vm.SaveBannerCommand.Execute(null);      // a click that slipped through the hide

        var saved = Assert.Single(_cfg.SavedPasswords);
        Assert.Equal("Payer A", saved.Label);
        Assert.Equal("good", PasswordVault.Reveal(saved.Password));   // never "bad"
    }

    [Fact]
    public async Task TheBannerResetsAtTheStartOfTheNextRun()
    {
        var vm = Vm();
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked.pdf") });
        vm.Password = "secret";
        await vm.UnlockAsync();
        Assert.True(vm.SaveBannerVisible);
        vm.SaveBannerName = "partial";   // typed but never saved

        vm.ClearCommand.Execute(null);
        await vm.AddFilesAsync(new[] { MakeEncrypted("locked2.pdf", "another") });
        vm.Password = "typo";            // this run fails outright
        await vm.UnlockAsync();

        // if the reset didn't happen at the START of this run, "partial"
        // would still be sitting in the box — nothing else clears it
        Assert.False(vm.SaveBannerVisible);
        Assert.Equal("", vm.SaveBannerName);
    }

    [Fact]
    public async Task ABatchIsAllUnlockedAndStillReportedInTheOrderItWasAdded()
    {
        // Files are unlocked several at a time now, so the order they FINISH
        // in is not the order they were added. The list must still read in the
        // user's order — one that reshuffles itself is harder to follow than a
        // slower one — and nothing may be dropped or double-counted.
        var vm = Vm();
        var names = Enumerable.Range(1, 9).Select(i => $"doc{i:00}.pdf").ToList();
        await vm.AddFilesAsync(names.Select(MakeEncryptedDefault));
        vm.Password = "secret";
        await vm.UnlockAsync();

        Assert.Equal(names.Count, vm.ResultLines.Count);
        Assert.All(vm.ResultLines, l => Assert.Equal(UnlockResultKind.Ok, l.Kind));
        for (var i = 0; i < names.Count; i++)
            Assert.Contains(names[i], vm.ResultLines[i].Text);
        Assert.Equal("9 unlocked", vm.Summary);
    }

    private string MakeEncryptedDefault(string name) => MakeEncrypted(name);

    /// <summary>A plain, unencrypted stand-in .pdf — fine wherever a test
    /// injects its own fake unlocker, since the real file content never
    /// reaches PdfSharp.</summary>
    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    private static int InterlockedMax(ref int location, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref location))
               && Interlocked.CompareExchange(ref location, value, snapshot) != snapshot) { }
        return snapshot;
    }

    [Fact]
    public async Task CancellingMidBatchSkipsTheFilesNotYetStarted()
    {
        // Deterministic, not timing-hopeful: the injected unlocker blocks the
        // first four files (the whole gate), so files five and six CANNOT have
        // started when cancel lands. Completed files stay completed — each is
        // individually safe — and the rest say so instead of running on after
        // the user asked them not to.
        var started = 0;
        var block = new ManualResetEventSlim(false);
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, _) =>
        {
            Interlocked.Increment(ref started);
            block.Wait();
            return new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true);
        });
        var files = Enumerable.Range(1, 6).Select(i => MakeEncrypted($"f{i}.pdf")).ToList();
        await vm.AddFilesAsync(files);
        vm.Password = "x";

        var run = vm.UnlockAsync();
        for (var i = 0; i < 500 && Volatile.Read(ref started) < 4; i++) await Task.Delay(10);
        Assert.Equal(4, Volatile.Read(ref started));
        Assert.True(vm.IsUnlocking);

        vm.CancelUnlock();
        block.Set();
        await run;

        Assert.False(vm.IsUnlocking);
        Assert.Equal(4, Volatile.Read(ref started));      // 5 and 6 never began
        Assert.Equal(6, vm.ResultLines.Count);            // but both are accounted for
        Assert.Contains("4 unlocked", vm.Summary);
        Assert.Contains("2 cancelled", vm.Summary);
        block.Dispose();
    }

    [Fact]
    public async Task ALargeFileNeverRunsBesideAnotherFile()
    {
        // a buffered file costs ~3x its size in memory and a streamed giant
        // saturates the share — either way a large file taking the whole gate
        // is the bound that keeps four of them from multiplying
        var current = 0; var peak = 0;
        var vm = new UnlockViewModel(_cfg, () => _saves++,
            unlocker: (p, _) =>
            {
                var c = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, c);
                Thread.Sleep(25);
                Interlocked.Decrement(ref current);
                return new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true);
            },
            fileSize: _ => long.MaxValue);   // everything is "large"
        await vm.AddFilesAsync(new[]
            { MakeEncrypted("g1.pdf"), MakeEncrypted("g2.pdf"), MakeEncrypted("g3.pdf") });
        vm.Password = "x";

        await vm.UnlockAsync();

        Assert.Equal(1, peak);
        Assert.Contains("3 unlocked", vm.Summary);
    }

    [Fact]
    public async Task AMixedBatchStillReportsInTheOrderFilesWereAdded()
    {
        // larges run after smalls, so finish order differs from add order —
        // the report must not
        var vm = new UnlockViewModel(_cfg, () => _saves++,
            unlocker: (p, _) => new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true),
            fileSize: p => p.Contains("big") ? long.MaxValue : 0L);
        var names = new[] { "big1.pdf", "tiny.pdf", "big2.pdf" };
        await vm.AddFilesAsync(names.Select(n => MakeEncrypted(n)));
        vm.Password = "x";

        await vm.UnlockAsync();

        Assert.Equal(3, vm.ResultLines.Count);
        for (var i = 0; i < names.Length; i++)
            Assert.Contains(names[i], vm.ResultLines[i].Text);
    }

    [Fact]
    public async Task AutoTryTriesTheTypedPasswordBeforeSavedOnesAndStopsAtFirstSuccess()
    {
        var tried = new List<string>();
        // Saved must be populated BEFORE the VM is constructed: the VM takes
        // a one-time snapshot of cfg.SavedPasswords into its own Saved
        // collection at construction, same as the real window does at open.
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "S1", Password = PasswordVault.Protect("saved-wrong") });
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, pw) =>
        {
            tried.Add(pw);
            return pw == "typed"
                ? new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true)
                : new OrdoSort.Core.Unlock.UnlockResult("wrong_password", p, Message: "nope");
        });
        await vm.AddFilesAsync(new[] { Touch("f.pdf") });
        vm.Password = "typed";

        await vm.UnlockAsync();

        Assert.Equal(new[] { "typed" }, tried);   // typed succeeded — the saved entry was never tried
        Assert.Equal("1 unlocked", vm.Summary);
    }

    [Fact]
    public async Task AutoTryFallsThroughToSavedPasswordsInOrderWhenTypedFails()
    {
        var tried = new List<string>();
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "A", Password = PasswordVault.Protect("also-wrong") });
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "B", Password = PasswordVault.Protect("the-real-one") });
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, pw) =>
        {
            tried.Add(pw);
            return pw == "the-real-one"
                ? new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true)
                : new OrdoSort.Core.Unlock.UnlockResult("wrong_password", p, Message: "nope");
        });
        await vm.AddFilesAsync(new[] { Touch("f.pdf") });
        vm.Password = "typed-wrong";

        await vm.UnlockAsync();

        Assert.Equal(new[] { "typed-wrong", "also-wrong", "the-real-one" }, tried);
        Assert.Equal("1 unlocked", vm.Summary);
    }

    [Fact]
    public async Task ABlankBoxStillTriesEverySavedPasswordInOrder()
    {
        var tried = new List<string>();
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "A", Password = PasswordVault.Protect("secret1") });
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "B", Password = PasswordVault.Protect("secret2") });
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, pw) =>
        {
            tried.Add(pw);
            return pw == "secret2"
                ? new OrdoSort.Core.Unlock.UnlockResult("ok", p, p, InPlace: true)
                : new OrdoSort.Core.Unlock.UnlockResult("wrong_password", p, Message: "nope");
        });
        await vm.AddFilesAsync(new[] { Touch("f.pdf") });
        // Password left blank — the typed candidate is skipped entirely

        await vm.UnlockAsync();

        Assert.Equal(new[] { "secret1", "secret2" }, tried);
        Assert.Equal("1 unlocked", vm.Summary);
    }

    [Fact]
    public async Task ASavedPasswordEqualToTheTypedOneIsNeverTriedTwice()
    {
        var tried = new List<string>();
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "Dup", Password = PasswordVault.Protect("same") });
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "Other", Password = PasswordVault.Protect("other") });
        var vm = new UnlockViewModel(_cfg, () => _saves++, unlocker: (p, pw) =>
        {
            tried.Add(pw);
            return new OrdoSort.Core.Unlock.UnlockResult("wrong_password", p, Message: "nope");
        });
        await vm.AddFilesAsync(new[] { Touch("f.pdf") });
        vm.Password = "same";

        await vm.UnlockAsync();

        Assert.Equal(new[] { "same", "other" }, tried);   // "same" attempted once, not twice
    }

    [Fact]
    public void AddSavedPasswordStoresProtectedAndPersistsImmediately()
    {
        var vm = Vm();
        Assert.True(vm.AddSavedPassword("Payer B", "s3cret"));

        var saved = Assert.Single(vm.Saved);
        Assert.Equal("Payer B", saved.Label);
        Assert.True(PasswordVault.IsProtected(saved.Password));
        Assert.Equal("s3cret", PasswordVault.Reveal(saved.Password));
        Assert.Same(saved, Assert.Single(_cfg.SavedPasswords));
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void AddSavedPasswordRefusesBlankFields()
    {
        var vm = Vm();
        Assert.False(vm.AddSavedPassword("", "secret"));
        Assert.False(vm.AddSavedPassword("Payer", ""));
        Assert.Empty(vm.Saved);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void RemoveSavedCommandRemovesTheSelectedEntryAndPersists()
    {
        _cfg.SavedPasswords.Add(new SavedPassword
        { Label = "X", Password = PasswordVault.Protect("pw123") });
        var vm = Vm();
        vm.SelectedSavedEntry = vm.Saved[0];
        Assert.True(vm.RemoveSavedCommand.CanExecute(null));

        vm.RemoveSavedCommand.Execute(null);

        Assert.Empty(vm.Saved);
        Assert.Empty(_cfg.SavedPasswords);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void PersistingSavedPasswordsReProtectsLegacyPlaintextEntries()
    {
        // Settings used to sweep every saved password and protect any
        // plaintext one on EVERY save, regardless of whether passwords were
        // even touched. That self-healing moved with the list to
        // UnlockViewModel — it must still happen, just triggered by this
        // surface's own persist paths instead.
        var alreadyProtected = new SavedPassword
        { Label = "A", Password = PasswordVault.Protect("already-safe") };
        var legacyPlain = new SavedPassword { Label = "B", Password = "hunter2" };
        _cfg.SavedPasswords.Add(alreadyProtected);
        _cfg.SavedPasswords.Add(legacyPlain);
        var vm = Vm();

        Assert.True(vm.AddSavedPassword("New", "x"));   // any persist path triggers the sweep

        Assert.All(_cfg.SavedPasswords, p => Assert.True(PasswordVault.IsProtected(p.Password)));
        Assert.Equal("hunter2", PasswordVault.Reveal(legacyPlain.Password));
        Assert.Equal("already-safe", PasswordVault.Reveal(alreadyProtected.Password));
        Assert.Equal(1, _saves);   // the sweep itself doesn't persist — only the one Add did
    }

    [Fact]
    public async Task EmptyListDisablesUnlockAndHints()
    {
        var vm = Vm();
        Assert.False(vm.UnlockCommand.CanExecute(null));
        await vm.UnlockAsync();
        Assert.Equal("Add at least one PDF first.", vm.Summary);
    }

    [Fact]
    public async Task OnlyExistingPdfsAreAcceptedAndTheDropExplainsItself()
    {
        var vm = Vm();
        var txt = Path.Combine(_dir, "not.txt");
        File.WriteAllText(txt, "x");
        await vm.AddFilesAsync(new[] { txt, Path.Combine(_dir, "ghost.pdf") });
        Assert.Empty(vm.Files);
        Assert.Contains("nothing added", vm.AddNote);

        var pdf = MakeEncrypted("real.pdf");
        await vm.AddFilesAsync(new[] { pdf, txt });
        Assert.Single(vm.Files);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
        Assert.True(vm.UnlockCommand.CanExecute(null));
    }
}

public class BulkRenameViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordobulk_" + Guid.NewGuid());

    public BulkRenameViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    /// <summary>The preview now computes off the UI thread through a debounced
    /// probe (Task 2 — see DebouncedProbe/BulkRenameViewModel.Refresh), so a
    /// property set (even an "immediate" one — the timer callback still fires
    /// on a threadpool thread) doesn't reflect in Preview/CountsLine/
    /// NeedsNameCount the instant the setter returns. Poll for it, same shape
    /// as SettingsViewModelTests.WaitFor/TilePreviewProbeTests.WaitFor.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    /// <summary>Counts how many times the injected scheduler is actually asked
    /// to run work — i.e. how many times a real Plan() compute landed — while
    /// still doing the work for real via TaskWorkScheduler underneath.
    ///
    /// Needed because a value-only WaitFor (e.g. "NewName == X") can pass on
    /// its very first poll against LEFTOVER state from an earlier compute,
    /// without ever observing the compute the test claims to be waiting for
    /// — especially when a debounced (300ms) trigger is immediately followed
    /// by an "immediate" (0ms) one, which re-arms BulkRenameViewModel's one
    /// shared timer and silently discards the still-pending debounced compute
    /// before its window ever elapses. Waiting on a call COUNT instead of a
    /// value forces the test to observe the specific compute it means to
    /// pin.</summary>
    private sealed class CountingWorkScheduler : IWorkScheduler
    {
        private readonly Action _onRun;
        private readonly IWorkScheduler _inner = new TaskWorkScheduler();
        public CountingWorkScheduler(Action onRun) => _onRun = onRun;
        public Task<T> Run<T>(Func<T> work) { _onRun(); return _inner.Run(work); }
        public Task Run(Action work) { _onRun(); return _inner.Run(work); }
    }

    [Fact]
    public void CountsLineAndAddNoteTrackTheBatch()
    {
        var vm = new BulkRenameViewModel();
        var a = Touch("scan_001.pdf");
        var b = Touch("keep.pdf");
        vm.AddFiles(new[] { a, b, a });   // duplicate ignored
        Assert.Contains("2 added", vm.AddNote);   // set synchronously, not through the probe
        Assert.Contains("1 ignored", vm.AddNote);

        vm.Find = "scan";
        vm.Replace = "fax";
        WaitFor(() => vm.CountsLine == "2 files · 1 will change",
            "CountsLine should eventually reflect the Find/Replace once the debounced probe completes");

        vm.RemoveFiles(new[] { b });
        WaitFor(() => vm.CountsLine == "1 file · 1 will change",
            "CountsLine should eventually reflect the removal");
        Assert.Single(vm.Preview);
    }

    [Fact]
    public void CountsLineCallsOutTheRowsStillWaitingOnAName()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("BROWN_ADAM_4_25_1966_ACME_RECORDS_100000001-1_X.pdf"),
                            Touch("notes_only.pdf") });
        vm.ReceivedDate = new DateTime(2024, 1, 26);
        vm.ReviewMode = true;
        WaitFor(() => vm.CountsLine.Contains("1 will change"),
            "CountsLine should eventually report the one file that changed");
        // was "1 won't (name didn't parse)" — a dead end. It now names the
        // number of files waiting on you, which is the thing to act on.
        Assert.Contains("1 need a name", vm.CountsLine);
    }

    /// <summary>Six strays in a batch of seventy-five is the real workload, so
    /// the tool has to make those six quick to fix rather than make the other
    /// sixty-nine expressible in a pattern language.
    ///
    /// Waits for the probe to settle before returning so the four tests below
    /// that assert on Preview/NeedsNameCount immediately after calling this
    /// don't each need their own WaitFor.</summary>
    private BulkRenameViewModel BatchWithStrays()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[]
        {
            Touch("SMITH_JOHN_5_5_2024_ACME_RECORDS_1-1__08_02_24_1019_X.pdf"),
            Touch("oddball one.pdf"),
            Touch("GARCIA_MARIA_8_5_2024_ACME_RECORDS_2-1__08_02_24_1020_X.pdf"),
            Touch("oddball two.pdf"),
        });
        vm.ReceivedDate = new DateTime(2024, 8, 2);
        vm.ReviewMode = true;
        WaitFor(() => vm.NeedsNameCount == 2, "the batch's preview should settle before the caller asserts on it");
        return vm;
    }

    [Fact]
    public void RowsThatCouldNotBeParsedAreFlaggedAndCounted()
    {
        var vm = BatchWithStrays();

        Assert.Equal(2, vm.NeedsNameCount);
        Assert.False(vm.Preview[0].NeedsName);   // parsed fine
        Assert.True(vm.Preview[1].NeedsName);    // oddball
        Assert.False(vm.Preview[2].NeedsName);
        Assert.True(vm.Preview[3].NeedsName);
        Assert.Contains("2 need a name", vm.CountsLine);
    }

    [Fact]
    public void AStrayOpensPrefilledWithTheDatePrefixSoOnlyTheNameIsTyped()
    {
        // the date is a batch constant the user already chose — retyping it on
        // every stray is both keystrokes and a chance to drift out of format
        var vm = BatchWithStrays();

        Assert.Equal("20240802-", vm.Preview[1].EditSeed);
        // a row that parsed opens with what it already says, to be edited
        Assert.Equal(vm.Preview[0].NewName, vm.Preview[0].EditSeed);
    }

    [Fact]
    public void NextStrayWrapsPastTheRowsThatAreAlreadyRight()
    {
        var vm = BatchWithStrays();

        Assert.Equal(1, vm.IndexOfNextNeedingName(-1));  // from the top
        Assert.Equal(3, vm.IndexOfNextNeedingName(1));   // skips row 2
        Assert.Equal(1, vm.IndexOfNextNeedingName(3));   // wraps around
    }

    [Fact]
    public void FixingAStrayByHandRemovesItFromTheCount()
    {
        var vm = BatchWithStrays();
        vm.SetOverride(vm.Preview[1].Source, "20240802-ODD-ONE");

        WaitFor(() => vm.NeedsNameCount == 1, "NeedsNameCount should drop once the hand edit is applied");
        Assert.Equal(3, vm.IndexOfNextNeedingName(-1));
        Assert.False(vm.Preview[1].NeedsName);
    }

    [Fact]
    public void NoStraysMeansNothingToJumpTo()
    {
        // NeedsNameCount/IndexOfNextNeedingName/CountsLine all read the same
        // (0/-1/no-"need a name") whether or not Find/Replace has actually
        // been applied to this plain filename, so a value-only WaitFor here
        // would trivially match leftover state from the AddFiles-only
        // compute without ever observing the Find/Replace recompute this
        // test claims to exercise. Count computes instead, so the wait
        // actually forces that recompute to land first.
        var calls = 0;
        var vm = new BulkRenameViewModel(scheduler: new CountingWorkScheduler(() => Interlocked.Increment(ref calls)));
        vm.AddFiles(new[] { Touch("scan_001.pdf") });
        WaitFor(() => calls == 1, "the initial add's compute should land");

        vm.Find = "scan";
        vm.Replace = "fax";
        WaitFor(() => calls == 2, "the Find/Replace-triggered recompute should land before asserting");

        Assert.Equal(0, vm.NeedsNameCount);
        Assert.Equal(-1, vm.IndexOfNextNeedingName(-1));
        Assert.DoesNotContain("need a name", vm.CountsLine);
    }

    [Fact]
    public void FindReplacePreviewMatchesThePlan()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("scan_001.pdf"), Touch("keep.pdf") });
        vm.Find = "scan";
        vm.Replace = "fax";

        WaitFor(() => vm.Preview.Count == 2 && vm.Preview[0].NewName == "fax_001.pdf",
            "the preview should eventually reflect the Find/Replace");
        Assert.True(vm.Preview[0].Changed);
        Assert.False(vm.Preview[1].Changed);
        Assert.Equal("Rename 1 file", vm.RenameButtonText);
    }

    [Fact]
    public void ReviewTransformParsesTheMedicalFaxNames()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("BROWN_ADAM_4_25_1966_ACME_RECORDS_100000001-1_X.pdf") });
        vm.ReceivedDate = new DateTime(2024, 1, 26);
        vm.ReviewMode = true;
        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "20240126-BROWN-ADAM.pdf",
            "the preview should eventually reflect the review-mode rebuild");
    }

    [Fact]
    public void HandEditSurvivesAnOpChange()
    {
        // The override's own value ("CUSTOM NAME.pdf") doesn't change across
        // the Find/Replace step below, so a value-only WaitFor there would
        // pass on its first poll against LEFTOVER state from the SetOverride
        // compute — never actually observing the Find/Replace-triggered
        // recompute. Worse: that recompute is debounced (300ms), and the
        // immediate (0ms) SetOverride("") that follows re-arms the same
        // shared timer and DISCARDS it outright if the test doesn't wait for
        // it first. Count computes instead, so each step is proven to have
        // actually landed before the next action fires.
        var calls = 0;
        var vm = new BulkRenameViewModel(scheduler: new CountingWorkScheduler(() => Interlocked.Increment(ref calls)));
        var src = Touch("scan_001.pdf");
        vm.AddFiles(new[] { src });
        WaitFor(() => calls == 1, "the initial add's compute should land");

        vm.SetOverride(src, "CUSTOM NAME.pdf");   // typed extension is stripped
        WaitFor(() => calls == 2 && vm.Preview[0].NewName == "CUSTOM NAME.pdf",
            "the preview should eventually reflect the hand edit");
        Assert.True(vm.Preview[0].Manual);

        vm.Find = "scan";
        vm.Replace = "fax";
        // wait for THIS recompute specifically (not a stale value) to land
        // before proceeding — this is the step the reviewer found skipped
        WaitFor(() => calls == 3, "the Find/Replace-triggered recompute should land before proceeding");
        Assert.Equal("CUSTOM NAME.pdf", vm.Preview[0].NewName);   // the override still beat the op

        vm.SetOverride(src, "");   // clearing goes back to the op result
        WaitFor(() => calls == 4 && vm.Preview[0].NewName == "fax_001.pdf",
            "clearing the override should fall back to the op result");
    }

    [Fact]
    public void ApplyRenamesOnDiskAndUndoRestores()
    {
        var vm = new BulkRenameViewModel();
        var src = Touch("scan_001.pdf");
        vm.AddFiles(new[] { src });
        vm.Find = "scan";
        vm.Replace = "fax";
        // Post finding-1 fix, Apply() executes the plan Preview last
        // rendered rather than re-planning from CurrentOp() — that's the
        // whole point (see BulkRenameViewModel._lastRenderedPlans) — so this
        // test has to let the debounced preview actually settle on
        // fax_001.pdf before calling Apply(), or it would be pinning stale
        // (pre-Find/Replace) plans instead of exercising a real rename.
        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "fax_001.pdf",
            "the preview should settle on the Find/Replace result before Apply");
        vm.Apply();

        Assert.True(File.Exists(Path.Combine(_dir, "fax_001.pdf")));
        Assert.False(File.Exists(src));
        Assert.Contains("Renamed 1 file", vm.Status);
        Assert.Equal("", vm.Find);   // ops reset after a batch

        vm.UndoBatch();
        Assert.True(File.Exists(src));
        Assert.Equal("Original names restored.", vm.Status);
    }

    /// <summary>Final-review finding 1 (2026-08-05 debounce pair): before the
    /// fix, Apply() re-planned from Execute(Plan(_files, CurrentOp(),
    /// _overrides)) — reading Find/Replace/etc. as they stand at the moment
    /// of the click, not as they stood the last time Preview actually
    /// rendered. Find/Replace bind UpdateSourceTrigger=PropertyChanged and
    /// debounce 300ms before the plan recomputes and Preview updates
    /// (BulkRenameViewModel.Refresh/ApplyPlans), so there is a real window —
    /// seconds on the SMB shares this app targets — where the property is
    /// already the NEW value but Preview is still showing the OLD plan and
    /// RenameCommand is still enabled from it.
    ///
    /// This drives that exact divergence rather than merely checking Apply()
    /// still works (which would pass either way, pre- or post-fix): settle
    /// the preview on Find="scan"/Replace="fax" -> fax_001.pdf, then change
    /// Find again WITHOUT letting the new debounce elapse, and call Apply()
    /// synchronously right after — deterministically inside the debounce
    /// window, no sleep needed. Pre-fix, CurrentOp() would see Find="s" and
    /// plan "scan_001".Replace("s","fax") = "faxcan_001.pdf" — an operation
    /// Preview never showed. Post-fix, Apply() executes the retained
    /// fax_001.pdf plan regardless of what Find says by then.</summary>
    [Fact]
    public void ApplyExecutesTheOperationLastRenderedNotAStillDebouncingEdit()
    {
        var vm = new BulkRenameViewModel();
        var src = Touch("scan_001.pdf");
        vm.AddFiles(new[] { src });
        vm.Find = "scan";
        vm.Replace = "fax";
        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "fax_001.pdf",
            "the preview should settle on fax_001.pdf before the test edits Find again");
        Assert.Equal("Rename 1 file", vm.RenameButtonText);   // what the user actually sees at click time

        // The user starts retyping toward something else (e.g. correcting
        // "scan" to "s" + more) — this re-arms the 300ms debounce but the
        // recompute has not landed, so Preview/RenameButtonText still show
        // the fax_001.pdf plan above.
        vm.Find = "s";
        vm.Apply();   // clicked inside the debounce window, before the recompute lands

        // What got renamed on disk must be what Preview showed, not a plan
        // built from the newer, never-rendered Find="s".
        Assert.True(File.Exists(Path.Combine(_dir, "fax_001.pdf")));
        Assert.False(File.Exists(src));
        Assert.False(File.Exists(Path.Combine(_dir, "faxcan_001.pdf")));   // what Find="s" would have produced
        Assert.Contains("Renamed 1 file", vm.Status);
    }

    [Fact]
    public void DeleteSegment2ProducesCorrectPreview()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("A-B-C.pdf") });
        vm.DeleteSeg2 = true;

        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "A-C.pdf",
            "the preview should eventually reflect DeleteSeg2");
        Assert.True(vm.Preview[0].Changed);
    }

    [Fact]
    public void DeleteSegment2AndLastProducesCorrectPreview()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("A-B-C.pdf") });
        vm.DeleteSeg2 = true;
        vm.DeleteSegLast = true;

        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "A.pdf",
            "the preview should eventually reflect DeleteSeg2 + DeleteSegLast");
        Assert.True(vm.Preview[0].Changed);
    }

    [Fact]
    public void UncheckedDeleteSegmentsReturnsOriginal()
    {
        var vm = new BulkRenameViewModel();
        vm.AddFiles(new[] { Touch("A-B-C.pdf") });
        vm.DeleteSeg2 = true;
        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "A-C.pdf",
            "the preview should eventually reflect DeleteSeg2");

        vm.DeleteSeg2 = false;
        WaitFor(() => vm.Preview[0].NewName == "A-B-C.pdf",
            "the preview should eventually revert once DeleteSeg2 is unchecked");
        Assert.False(vm.Preview[0].Changed);
    }

    [Fact]
    public void HandEditedTargetStillOverridesDeleteSegments()
    {
        // Same shape as HandEditSurvivesAnOpChange's false-green (the
        // override value doesn't change across the final step, so a
        // value-only WaitFor could pass against leftover state without
        // observing the DeleteSeg2=false recompute) — lower risk here since
        // DeleteSeg2 is itself immediate (0ms), so nothing races ahead and
        // discards its compute outright the way the debounced Find/Replace
        // one could be discarded above. Fixed the same way regardless, so
        // this test actually proves what its name claims.
        var calls = 0;
        var vm = new BulkRenameViewModel(scheduler: new CountingWorkScheduler(() => Interlocked.Increment(ref calls)));
        var src = Touch("A-B-C.pdf");
        vm.AddFiles(new[] { src });
        WaitFor(() => calls == 1, "the initial add's compute should land");

        vm.DeleteSeg2 = true;
        WaitFor(() => calls == 2 && vm.Preview[0].NewName == "A-C.pdf",
            "the preview should eventually reflect DeleteSeg2");

        vm.SetOverride(src, "CUSTOM-NAME.pdf");
        WaitFor(() => calls == 3 && vm.Preview[0].NewName == "CUSTOM-NAME.pdf",
            "the preview should eventually reflect the hand edit");
        Assert.True(vm.Preview[0].Manual);

        vm.DeleteSeg2 = false;
        // wait for THIS recompute specifically before asserting
        WaitFor(() => calls == 4, "the DeleteSeg2=false-triggered recompute should land before asserting");
        Assert.Equal("CUSTOM-NAME.pdf", vm.Preview[0].NewName);   // the override still beat the op
    }

    [Fact]
    public void DeleteSegmentsResetAfterApply()
    {
        var vm = new BulkRenameViewModel();
        var src = Touch("A-B-C.pdf");
        vm.AddFiles(new[] { src });
        vm.DeleteSeg2 = true;
        vm.Apply();

        Assert.False(vm.DeleteSeg1);
        Assert.False(vm.DeleteSeg2);
        Assert.False(vm.DeleteSeg3);
        Assert.False(vm.DeleteSeg4);
        Assert.False(vm.DeleteSegLast);
    }
}

public class MatchMergeViewModelTests : IDisposable
{
    private const string Csv =
        "First Name,Last Name,Control ID,DOB\n" +
        "Adam,Brown,100000001,4/25/1966\n" +
        "Adam,Brown,696009058,11/10/1955\n" +
        "Frank,Evans,176797656,8/9/1997\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordomm_" + Guid.NewGuid());
    private readonly Config _cfg = new();
    private readonly FakeDialogs _dialogs = new();
    private Dictionary<string, string>? _savedHeaders;

    public MatchMergeViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private MatchMergeViewModel Vm() => new(_cfg, h => _savedHeaders = h, _dialogs);

    private string WriteRoster()
    {
        var path = Path.Combine(_dir, "roster.csv");
        File.WriteAllText(path, Csv, new System.Text.UTF8Encoding(true));
        return path;
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void RosterLoadGuessesHeadersAndRemembersThem()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        Assert.Equal("First Name", vm.FirstHeader);
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);
        Assert.Contains("2 people", vm.Status);   // the two Adam Browns are one person
        Assert.Equal("First Name", _savedHeaders!["first"]);
    }

    [Fact]
    public void SavedHeaderMappingWinsOverTheGuess()
    {
        _cfg.MergeHeaders["control"] = "DOB";   // deliberately odd saved choice
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        Assert.Equal("DOB", vm.ControlHeader);
    }

    [Fact]
    public void FilesClassifyIntoTheFourBuckets()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        vm.AddFiles(new[]
        {
            Touch("20240126-EVANS-FRANK.pdf"),               // unambiguous
            Touch("20240126-BROWN-ADAM.pdf"),                // two Adams -> triage
            Touch("20240126-EVANS-FRANK-176797656.pdf"),     // already merged
            Touch("scan_001.pdf"),                           // no name
        });

        Assert.Equal(1, vm.MergeCount);
        Assert.Equal(1, vm.ReviewCount);
        Assert.Equal("Merge 1 matched", vm.MergeButtonText);
        Assert.True(vm.CanReview);
        Assert.Single(vm.ReviewItems);
        Assert.Contains(vm.Rows, r => r.Note == "already has the id");
    }

    [Fact]
    public void SuggestedFilesJoinTheReviewQueueWithReasons()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        vm.AddFiles(new[] { Touch("20240126-FRANK-EVANS.pdf") });   // inverted -> suggested

        Assert.Equal(1, vm.ReviewCount);
        Assert.True(vm.CanReview);
        var item = Assert.Single(vm.ReviewItems);
        Assert.Equal("suggested", item.Status);
        Assert.Equal("all segments agree", Assert.Single(item.Suggestions!).Reason);
        Assert.Contains("Review 1 match", vm.ReviewButtonText);
        Assert.Contains("1 suggested", vm.BucketsLine);
    }

    [Fact]
    public void MergeRenamesAndUndoRestores()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        var f = Touch("20240126-EVANS-FRANK.pdf");
        vm.AddFiles(new[] { f });

        vm.MergeCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(_dir, "20240126-EVANS-FRANK-176797656.pdf")));
        Assert.Contains("Merged 1 file", vm.Status);

        vm.UndoCommand.Execute(null);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void NoRosterShowsAHintPerFile()
    {
        var vm = Vm();
        Assert.False(vm.HasRoster);   // the header combos stay hidden
        vm.AddFiles(new[] { Touch("20240126-EVANS-FRANK.pdf") });
        Assert.Equal("load a roster first", Assert.Single(vm.Rows).Note);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.Equal("", vm.BucketsLine);
    }

    [Fact]
    public void BucketsLineSummarizesTheGrid()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        Assert.True(vm.HasRoster);
        vm.AddFiles(new[]
        {
            Touch("20240126-EVANS-FRANK.pdf"),               // ready
            Touch("20240126-BROWN-ADAM.pdf"),                // ambiguous
            Touch("20240126-EVANS-FRANK-176797656.pdf"),     // already
            Touch("scan_001.pdf"),                           // no name
        });
        Assert.Equal(
            "1 ready to merge · 1 to review · 1 already merged · 1 no name in the filename",
            vm.BucketsLine);
        Assert.Equal("merge", vm.Rows[0].Status);
        Assert.Equal("ambiguous", vm.Rows[1].Status);
    }

    [Fact]
    public void RemoveFilesAndAddNotesWork()
    {
        var vm = Vm();
        vm.LoadRosterFrom(WriteRoster());
        var keep = Touch("20240126-EVANS-FRANK.pdf");
        var drop = Touch("20240126-BROWN-ADAM.pdf");
        var txt = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(txt, "x");

        vm.AddFiles(new[] { keep, drop, txt });
        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("2 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);

        vm.RemoveFiles(new[] { drop });
        Assert.Single(vm.Rows);
        Assert.Equal(keep, vm.Rows[0].Source);
    }

    [Fact]
    public async Task TheLastRosterLoadsItselfNextSession()
    {
        var roster = Path.Combine(_dir, "roster.csv");
        File.WriteAllLines(roster, new[] { "Last,First,Control", "EVANS,FRANK,111" });
        var cfg = new Config { MergeRoster = roster };
        var vm = new MatchMergeViewModel(cfg, _ => { }, new FakeDialogs());

        await vm.AutoLoadRosterAsync();

        Assert.True(vm.HasRoster);
        Assert.Equal(roster, vm.RosterPath);
    }

    [Fact]
    public async Task AVanishedRosterSaysSoInsteadOfLoading()
    {
        var cfg = new Config { MergeRoster = Path.Combine(_dir, "gone.csv") };
        var vm = new MatchMergeViewModel(cfg, _ => { }, new FakeDialogs());

        await vm.AutoLoadRosterAsync();

        Assert.False(vm.HasRoster);
        Assert.Contains("wasn't found", vm.Status);
    }

    [Fact]
    public void AFailedBrowseLeavesTheGoodRosterInPlace()
    {
        var roster = Path.Combine(_dir, "good.csv");
        File.WriteAllLines(roster, new[] { "Last,First,Control", "EVANS,FRANK,111" });
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        vm.LoadRosterFrom(roster);
        Assert.True(vm.HasRoster);

        vm.LoadRosterFrom(Path.Combine(_dir, "nope.csv"));   // vanished mid-browse

        Assert.True(vm.HasRoster);                            // still matching
        Assert.Equal(roster, vm.RosterPath);                  // the box doesn't lie
    }

    [Fact]
    public void ASuccessfulLoadRemembersThePathAndSaves()
    {
        var roster = Path.Combine(_dir, "roster.csv");
        File.WriteAllLines(roster, new[] { "Last,First,Control", "EVANS,FRANK,111" });
        var cfg = new Config();
        var saves = 0;
        var vm = new MatchMergeViewModel(cfg, _ => { }, new FakeDialogs(), () => saves++);

        vm.LoadRosterFrom(roster);

        Assert.Equal(roster, cfg.MergeRoster);
        Assert.True(saves > 0);
    }

    [Fact]
    public void ColumnChoiceDefaultsToTheMappedHeadersAndPersists()
    {
        var roster = Path.Combine(_dir, "r.csv");
        File.WriteAllLines(roster, new[] { "Last,First,DOB,Dept,Control", "EVANS,FRANK,1970,ER,111" });
        var cfg = new Config();
        var saves = 0;
        var vm = new MatchMergeViewModel(cfg, _ => { }, new FakeDialogs(), () => saves++);
        vm.LoadRosterFrom(roster);

        // nothing picked yet -> the mapped name and id columns
        Assert.Equal(new[] { "Last", "First", "Control" }, vm.ChosenColumns);

        vm.ColumnPicks.Single(p => p.Name == "DOB").IsChosen = true;
        Assert.Contains("DOB", vm.ChosenColumns);
        Assert.Equal(new[] { "Last", "First", "DOB", "Control" }, cfg.MergeColumns);
        Assert.True(saves > 0);

        // a fresh VM against the same config restores the choice
        var vm2 = new MatchMergeViewModel(cfg, _ => { }, new FakeDialogs());
        vm2.LoadRosterFrom(roster);
        Assert.True(vm2.ColumnPicks.Single(p => p.Name == "DOB").IsChosen);
    }

    [Fact]
    public void DuplicateRosterHeadersDontDuplicateChosenColumns()
    {
        // a spreadsheet with a repeated column name (or a roster whose
        // guessed name headers collapse to the same column) must not hand
        // Review matches a duplicate header list — DataGrid tolerates it,
        // but the per-row dictionary building downstream does not
        var roster = Path.Combine(_dir, "dup.csv");
        File.WriteAllLines(roster, new[] { "Last,Last,Control", "EVANS,EVANS,111" });
        var vm = Vm();
        vm.LoadRosterFrom(roster);

        Assert.Equal(2, vm.ColumnPicks.Count);             // one pick per DISTINCT header
        Assert.Equal(vm.ChosenColumns.Count, vm.ChosenColumns.Distinct().Count());
    }
}
