using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class LabelMakerViewModelTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("ordolabelvm_").FullName;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<string> _opened = new();
    private static readonly DateTime Today = new(2026, 7, 25);

    private LabelMakerViewModel Vm(string boxLabelsPath) =>
        new(new Config(), boxLabelsPath, _dialogs, () => Today, _opened.Add,
            new InlineWorkScheduler());

    /// <summary>A fresh box-labels.json path (nothing written yet — the
    /// window seeds an empty roster) or, when clients are given, one already
    /// seeded through the exclusive store, exactly as another station's
    /// prior session would have left it.</summary>
    private string PathWith(params LabelClient[] clients)
    {
        var path = Path.Combine(_dir, $"box-labels-{Guid.NewGuid():N}.json");
        if (clients.Length > 0)
            BoxLabelStore.Mutate(path, d => { d.LabelClients.AddRange(clients); return 0; });
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void BootstrapsFromLegacyInlineLabelClientsWhenTheStoreFileIsMissing()
    {
        // a pre-split config: inline label_clients, but box-labels.json has
        // never been written — opening the window must migrate the inline
        // clients into the store rather than showing (and then persisting)
        // an empty roster
        var path = Path.Combine(_dir, $"box-labels-{Guid.NewGuid():N}.json");
        Assert.False(File.Exists(path));
        var cfg = new Config
        {
            LabelClients = { new LabelClient { Id = "ACME", DestroyDays = 45, NextNumber = 7 } },
        };

        var vm = new LabelMakerViewModel(cfg, path, _dialogs, () => Today, _opened.Add,
            new InlineWorkScheduler());

        Assert.True(File.Exists(path));
        var stored = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("ACME", stored.Id);
        Assert.Equal(45, stored.DestroyDays);
        Assert.Equal(7, stored.NextNumber);

        var shown = Assert.Single(vm.Clients);
        Assert.Equal("ACME", shown.Id);
        Assert.Equal("7", shown.NextNumberText);
    }

    [Fact]
    public void DoesNotBootstrapWhenTheStoreFileAlreadyExists()
    {
        // the store file existing at all (even with zero clients, e.g. a
        // deliberate reset) means the migration already happened, or was
        // never needed — the inline clients must not be replayed over it
        var path = Path.Combine(_dir, $"box-labels-{Guid.NewGuid():N}.json");
        BoxLabelStore.Mutate(path, d => 0);   // file exists, roster deliberately empty
        var cfg = new Config
        {
            LabelClients = { new LabelClient { Id = "STALE", NextNumber = 99 } },
        };

        var vm = new LabelMakerViewModel(cfg, path, _dialogs, () => Today, _opened.Add,
            new InlineWorkScheduler());

        Assert.Empty(vm.Clients);
        Assert.Empty(BoxLabelStore.Read(path).LabelClients);
    }

    [Fact]
    public void LoadsClientsFromConfigAndSelectsTheFirst()
    {
        var path = PathWith(
            new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 42 },
            new LabelClient { Id = "WXYZ", DestroyDays = 90, NextNumber = 7 });
        var vm = Vm(path);
        Assert.Equal(2, vm.Clients.Count);
        Assert.Equal("ABCD", vm.Selected!.Id);
        Assert.Contains("ABCD00000042 – ABCD00000051", vm.Preview);   // default count 10
        Assert.Contains("1 sheet", vm.Preview);

        // the live preview card renders the first label of the batch
        Assert.Equal("ABCD00000042", vm.PreviewItem!.Code);
        Assert.Equal(Today, vm.PreviewItem.Created);
        Assert.Equal(Today.AddDays(30), vm.PreviewItem.Destroy);
    }

    [Fact]
    public void PrintSendsTheSheetsAndAdvancesTheNumber()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 5 });
        var vm = Vm(path);
        IReadOnlyList<BoxLabels.Item>? sent = null;
        string? job = null;
        vm.PrintSheets = (items, name) => { sent = items; job = name; return true; };

        vm.Print();

        Assert.Equal(10, sent!.Count);
        Assert.Equal("ABCD00000005", sent[0].Code);
        Assert.Contains("ABCD00000005", job);
        Assert.Equal("15", vm.Selected!.NextNumberText);            // 10 labels consumed
        Assert.Equal(15, BoxLabelStore.Read(path).LabelClients.Single().NextNumber); // written back...
        Assert.Contains("printer", vm.Status);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public void PrintClaimsFromTheFreshFileEvenWhenTheDialogIsThenCancelled()
    {
        // The claim (BoxLabelStore.Mutate) happens before the sheets are
        // handed to the printer, because the sheets themselves must carry
        // the claimed numbers, not the stale on-screen ones. That means a
        // user backing out of the OS print dialog AFTER the claim landed
        // cannot get the numbers back — reopening the file to "un-claim"
        // would just recreate the race this store exists to close. The
        // counter moves; only the "sent to printer" status line does not.
        var path = PathWith(new LabelClient { Id = "ABCD", NextNumber = 5 });
        var vm = Vm(path);
        vm.PrintSheets = (_, _) => false;   // user backed out

        vm.Print();

        Assert.Equal("15", vm.Selected!.NextNumberText);
        Assert.Equal(15, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
        Assert.Equal("", vm.Status);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public void SavePdfWritesTheFileAdvancesTheNumberAndPersists()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 5 });
        var vm = Vm(path);
        var dest = Path.Combine(_dir, "labels.pdf");
        _dialogs.NextSaveFile = dest;

        vm.SavePdf();

        Assert.True(File.Exists(dest));
        Assert.Equal("15", vm.Selected!.NextNumberText);
        Assert.Equal(15, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
        Assert.Equal(dest, Assert.Single(_opened));                 // handed to the viewer
        Assert.Contains("1 sheet", vm.Status);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public void CancellingTheSaveDialogChangesNothing()
    {
        // Unlike Print(), the Save-PDF cancellation point (choosing a
        // destination) comes BEFORE the claim — there is no reason to burn
        // numbers for a save the user never confirmed a location for.
        var path = PathWith(new LabelClient { Id = "ABCD", NextNumber = 5 });
        var vm = Vm(path);
        _dialogs.NextSaveFile = null;   // user pressed Cancel

        vm.SavePdf();

        Assert.Equal("5", vm.Selected!.NextNumberText);
        Assert.Equal(5, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
        Assert.Empty(_opened);
    }

    [Fact]
    public void BadInputsWarnInsteadOfGenerating()
    {
        var path = PathWith(new LabelClient { Id = "A" });
        var vm = Vm(path);
        vm.LabelCountText = "0";
        _dialogs.NextSaveFile = Path.Combine(_dir, "never.pdf");

        vm.SavePdf();

        var msg = Assert.Single(_dialogs.Warnings).Message;
        Assert.Contains("2 to 8", msg);          // bad client id
        Assert.Contains("1 to 1000", msg);       // bad count
        Assert.False(File.Exists(Path.Combine(_dir, "never.pdf")));
        Assert.StartsWith("⚠", vm.Preview);      // the preview says so live too
        Assert.Null(vm.PreviewItem);              // and the card goes blank
    }

    [Fact]
    public void PrintWithoutAPrinterHookWarns()
    {
        var path = PathWith(new LabelClient { Id = "ABCD" });
        var vm = Vm(path);
        vm.Print();   // PrintSheets never wired
        Assert.Contains("Printing", Assert.Single(_dialogs.Warnings).Message);
        Assert.Equal(1, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);  // untouched
    }

    [Fact]
    public void DuplicateClientIdsAreBlocked()
    {
        var path = PathWith(
            new LabelClient { Id = "ABCD" },
            new LabelClient { Id = "ABCD" });
        var vm = Vm(path);
        vm.SavePdf();
        Assert.Contains("both called", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void ResetTakesTheNumberBackToOne()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", NextNumber = 4242 });
        var vm = Vm(path);
        vm.ResetNumberCommand.Execute(null);
        Assert.Equal("1", vm.Selected!.NextNumberText);
        Assert.Contains("ABCD00000001", vm.Preview);
    }

    [Fact]
    public void AddAndRemoveManageTheListAndPersistOnDemand()
    {
        var path = PathWith();
        var vm = Vm(path);
        Assert.Null(vm.Selected);
        Assert.False(vm.PrintCommand.CanExecute(null));
        Assert.False(vm.SavePdfCommand.CanExecute(null));

        vm.AddClientCommand.Execute(null);
        vm.Selected!.Id = "abcd";                 // typed lowercase...
        Assert.Equal("ABCD", vm.Selected.Id);     // ...uppercased on the way in

        vm.TryPersist();
        Assert.Equal("ABCD", Assert.Single(BoxLabelStore.Read(path).LabelClients).Id);

        vm.RemoveClientCommand.Execute(null);
        Assert.Empty(vm.Clients);
        vm.TryPersist();
        Assert.Empty(BoxLabelStore.Read(path).LabelClients);
    }

    [Fact]
    public void RemovingARealClientAsksFirstAndDecliningKeepsIt()
    {
        var path = PathWith(new LabelClient { Id = "MEDR", NextNumber = 5000 });
        var vm = Vm(path);

        _dialogs.ConfirmAnswer = false;              // "No" — the counter survives
        vm.RemoveClientCommand.Execute(null);
        Assert.Single(vm.Clients);

        _dialogs.ConfirmAnswer = true;               // "Yes" — deliberate removal
        vm.RemoveClientCommand.Execute(null);
        Assert.Empty(vm.Clients);
    }

    [Fact]
    public void RemovingAJustAddedBlankRowDoesNotNag()
    {
        var vm = Vm(PathWith());
        vm.AddClientCommand.Execute(null);
        _dialogs.ConfirmAnswer = false;              // would block if it asked
        vm.RemoveClientCommand.Execute(null);
        Assert.Empty(vm.Clients);                    // removed without a prompt
    }

    [Fact]
    public void RemovalConfirmationShowsTheFreshOnDiskNumberNotTheStaleVmValue()
    {
        // The confirm dialog's whole job is "here is what you're about to
        // lose" — showing the VM's stale in-memory number (loaded when the
        // window opened) instead of what a peer has since advanced it to is
        // a lie about the one thing this prompt exists to state accurately.
        var path = PathWith(new LabelClient { Id = "MEDR", NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "MEDR").NextNumber = 140; return 0; });

        _dialogs.ConfirmAnswer = false;   // decline — only checking what was shown
        vm.RemoveClientCommand.Execute(null);

        var shown = Assert.Single(_dialogs.Confirms).Message;
        Assert.Contains("140", shown);
        Assert.DoesNotContain("(100)", shown);
    }

    [Fact]
    public void RemovingThenReAddingTheSameIdInOneSessionStartsAtOneAsThePromptSays()
    {
        // Audit: does remove-then-re-add-the-same-id in one session reopen
        // the counter-rollback hole? No — Remove is the one destructive,
        // explicitly-confirmed act in this window, and the confirmation text
        // itself promises "re-adding the client starts back at 1." A freshly
        // Added row's NextNumberText defaults to "1" and was never flagged
        // in _numberEdited, so it always takes that fresh-add path — not the
        // merge-into-existing-disk-row path — regardless of what a peer did
        // to the old row in between. This test locks that promise in; it is
        // not a regression test for a bug (nothing here changed to fix it).
        var path = PathWith(new LabelClient { Id = "OLDX", DestroyDays = 30, NextNumber = 42 });
        var vm = Vm(path);

        // a peer even advances the counter in between, just to prove it
        // makes no difference to the documented "starts back at 1" outcome
        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "OLDX").NextNumber = 140; return 0; });

        _dialogs.ConfirmAnswer = true;
        vm.RemoveClientCommand.Execute(null);

        vm.AddClientCommand.Execute(null);
        vm.Selected!.Id = "OLDX";     // same id, brand-new row, never touched NextNumberText

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("OLDX", only.Id);
        Assert.Equal(1, only.NextNumber);   // exactly what the remove prompt promised
    }

    [Fact]
    public void AddRequestsFocusOnTheIdBox()
    {
        var vm = Vm(PathWith());
        var asked = 0;
        vm.RequestIdFocus += () => asked++;
        vm.AddClientCommand.Execute(null);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void BatchNearTheCeilingIsCaughtBeforeTheDialog()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", NextNumber = 99_999_995 });
        var vm = Vm(path);
        _dialogs.NextSaveFile = Path.Combine(_dir, "never.pdf");

        vm.SavePdf();   // 10 labels would pass 99,999,999

        Assert.Contains("99999999", Assert.Single(_dialogs.Warnings).Message);
        Assert.False(File.Exists(Path.Combine(_dir, "never.pdf")));
    }

    [Fact]
    public void PrintClaimsNumbersFromTheFreshFileNotTheScreen()
    {
        var dir = Directory.CreateTempSubdirectory("ordomm_").FullName;
        try
        {
            var path = Path.Combine(dir, "box-labels.json");
            BoxLabelStore.Mutate(path, d => { d.LabelClients.Add(
                new LabelClient { Id = "ACME", DestroyDays = 30, NextNumber = 10 }); return 0; });

            var vm = Vm(path);                    // window opens, sees NextNumber 10
            // another station advances the counter AFTER our window opened:
            BoxLabelStore.Mutate(path, d =>
                { d.LabelClients.Single(c => c.Id == "ACME").NextNumber = 50; return 0; });

            var start = vm.ClaimNumbers(vm.Clients.Single(c => c.Id == "ACME"), 3);
            Assert.Equal(50, start);                 // fresh, not the stale 10
            Assert.Equal(53, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --------------------------------------------------------- merge-Persist

    [Fact]
    // Also stands in for "untouched sibling with a UNIQUE id keeps an
    // external advance" — the regression case for object-identity dirty
    // tracking when ids don't collide (see the id-collision variant below,
    // PersistRefusesUnderDuplicateIdsAndPreservesTheDisk, for the case where
    // they do).
    public void MergePersistWritesEditedClientsAndLeavesUntouchedOnesAtWhateverTheDiskHolds()
    {
        var path = PathWith(
            new LabelClient { Id = "AAAA", DestroyDays = 30, NextNumber = 7 },
            new LabelClient { Id = "BBBB", DestroyDays = 30, NextNumber = 10 });
        var vm = Vm(path);

        vm.Clients.Single(c => c.Id == "AAAA").DestroyDaysText = "45";

        // another station (or this window's own Print/SavePdf elsewhere)
        // advances B's counter after this window opened — our in-memory copy
        // of that row is now stale
        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "BBBB").NextNumber = 50; return 0; });

        vm.TryPersist();

        var stored = BoxLabelStore.Read(path).LabelClients;
        Assert.Equal(45, stored.Single(c => c.Id == "AAAA").DestroyDays);   // our edit landed
        Assert.Equal(50, stored.Single(c => c.Id == "BBBB").NextNumber);   // disk wins — untouched by us
    }

    [Fact]
    public void ZeroEditCloseWritesNothingEvenAfterAnExternalAdvance()
    {
        var path = PathWith(new LabelClient { Id = "AAAA", DestroyDays = 30, NextNumber = 7 });
        var vm = Vm(path);

        // the window never touches this client — an external advance must
        // not be rolled back, and Persist must not write at all
        BoxLabelStore.Mutate(path, d => { d.LabelClients.Single().NextNumber = 99; return 0; });
        var beforeBytes = File.ReadAllBytes(path);

        vm.TryPersist();

        Assert.Equal(beforeBytes, File.ReadAllBytes(path));   // byte-identical: no write happened
        Assert.Equal(99, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
    }

    [Fact]
    public void RenamingAClientRemovesTheOldRowAndAddsTheNewOneOnPersist()
    {
        // an id change is remove-old + dirty-new: the store must not end up
        // with both the pre-edit row and the renamed one
        var path = PathWith(new LabelClient { Id = "OLDX", DestroyDays = 30, NextNumber = 42 });
        var vm = Vm(path);

        vm.Clients.Single().Id = "newx";

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("NEWX", only.Id);
        Assert.Equal(42, only.NextNumber);       // untouched field carries over
        Assert.Equal(30, only.DestroyDays);
    }

    [Fact]
    public void RenamingAClientCarriesAPeersConcurrentCounterAdvanceForwardToTheNewId()
    {
        // Same hazard as EditingAnUnrelatedFieldCannotRollBackAPeersCounterAdvance,
        // reached through a different unrelated-field edit: renaming touches
        // only Id, never NextNumberText, so without help the row's stale
        // in-memory number lands under the new id while the peer's advance
        // (sitting on the now-deleted old-id row) is silently destroyed.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        vm.Clients.Single().Id = "yyyy";   // rename only — NextNumberText untouched

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("YYYY", only.Id);
        Assert.Equal(140, only.NextNumber);   // the peer's advance followed the rename — NOT rolled back to 100
    }

    [Fact]
    public void DeliberatelyEditingTheNumberDuringARenameStillWins()
    {
        // The rename fix above must not make the counter read-only either:
        // if the user renames AND deliberately retypes NextNumberText in the
        // same session, their typed value lands — not the carried-forward
        // disk value.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        var c = vm.Clients.Single();
        c.Id = "yyyy";
        c.NextNumberText = "999";   // deliberate correction, on top of the rename

        vm.TryPersist();

        Assert.Equal(999, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
    }

    [Fact]
    public void RenamingTwiceInOneSessionStillCarriesTheOriginalCounterForward()
    {
        // Multi-hop rename (a typo fixed twice before ever saving): only the
        // FIRST id was ever actually on disk, so that's the row a peer's
        // advance could be sitting on — _originId must track back to it, not
        // just to the immediately-previous in-memory id.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        var c = vm.Clients.Single();
        c.Id = "yyyy";
        c.Id = "zzzz";

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("ZZZZ", only.Id);
        Assert.Equal(140, only.NextNumber);
    }

    [Fact]
    public void RenamingBackToTheOriginalIdStillCarriesThePeersCounterAdvanceForward()
    {
        // Round-trip: X -> Y -> X, all before ever persisting. The FINAL id
        // equals the origin id again, so a guard that reads "current id
        // differs from origin" as "nothing to carry" wrongly skips the
        // snapshot — but the X->Y hop already queued the ORIGINAL "X" row
        // for deletion in _removedIds, and RemoveAll doesn't care that the
        // row's current id happens to match again. Without carrying X's
        // fresh on-disk value across that round trip, the sweep deletes it
        // and the re-add falls back to the VM's stale number.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        var c = vm.Clients.Single();
        c.Id = "yyyy";
        c.Id = "xxxx";   // back to the original id

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("XXXX", only.Id);
        Assert.Equal(140, only.NextNumber);   // the peer's advance survives the round trip
    }

    [Fact]
    public void ThreeHopRenameThatReturnsToTheOriginalIdAlsoCarriesTheCounterForward()
    {
        // Same class of gap, one hop deeper: X -> Y -> Z -> X. Every
        // intermediate id (X and Y — not Z, since Z is the id it left FROM
        // on the final hop, never revisited) ends up in _removedIds; the
        // fix must hold regardless of how many hops it took to get back.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        var c = vm.Clients.Single();
        c.Id = "yyyy";
        c.Id = "zzzz";
        c.Id = "xxxx";   // back to the original id, three hops later

        vm.TryPersist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("XXXX", only.Id);
        Assert.Equal(140, only.NextNumber);
    }

    [Fact]
    public void EditingOneClientsIdCannotSweepAnUntouchedSiblingItTransitsThrough()
    {
        // ACME is being edited down toward AC — a live, distinct, UNTOUCHED
        // sibling client that never itself changes. Each Id assignment below
        // stands in for one keystroke: ACME->ACM->AC->ACX. "ACM" and "AC" are
        // both merely transited THROUGH on the way to the final id "ACX" —
        // but "AC" is a real, live client's actual id, not a throwaway
        // intermediate string, and it must still be sitting on disk
        // afterward, untouched, not just referenced by a status line.
        var path = PathWith(
            new LabelClient { Id = "ACME", DestroyDays = 30, NextNumber = 7 },
            new LabelClient { Id = "AC", DestroyDays = 45, NextNumber = 500 });
        var vm = Vm(path);

        var edited = vm.Clients.Single(c => c.Id == "ACME");
        edited.Id = "ACM";   // ACME -> ACM
        edited.Id = "AC";    // ACM -> AC   (the sibling's own live id, momentarily)
        edited.Id = "ACX";   // AC -> ACX   (final: queues "AC" for the sweep)

        vm.TryPersist();

        var stored = BoxLabelStore.Read(path).LabelClients;
        var sibling = stored.SingleOrDefault(c => c.Id == "AC");
        Assert.NotNull(sibling);                    // AC must still be ON DISK...
        Assert.Equal(500, sibling!.NextNumber);      // ...with its own data intact
        Assert.Contains(stored, c => c.Id == "ACX"); // and the real rename still landed
    }

    [Fact]
    public void BlankingAClientsIdViaEditingAsksBeforeRemovingItLikeExplicitRemoveDoes()
    {
        // Typing a client's Id field down to nothing is the keystroke path's
        // version of Remove — it must ask the same question Remove asks, not
        // silently sweep the row at the next Persist.
        var path = PathWith(new LabelClient { Id = "MEDR", DestroyDays = 30, NextNumber = 5000 });
        var vm = Vm(path);

        _dialogs.ConfirmAnswer = false;   // decline — the client survives
        vm.Clients.Single().Id = "";

        vm.TryPersist();

        var kept = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("MEDR", kept.Id);
        Assert.Equal(5000, kept.NextNumber);
        Assert.Contains("MEDR", Assert.Single(_dialogs.Confirms).Message);
    }

    [Fact]
    public void ConfirmingABlankedIdRemovesItJustLikeExplicitRemove()
    {
        var path = PathWith(new LabelClient { Id = "MEDR", DestroyDays = 30, NextNumber = 5000 });
        var vm = Vm(path);

        _dialogs.ConfirmAnswer = true;    // confirm — deliberate removal
        vm.Clients.Single().Id = "";

        vm.TryPersist();

        Assert.Empty(BoxLabelStore.Read(path).LabelClients);
    }

    [Fact]
    public void PersistRefusesUnderDuplicateIdsAndPreservesTheDisk()
    {
        // renaming AAAA onto BBBB's id is the sharpest way to get two rows
        // with the same id on screen at once (a pre-existing duplicate on
        // disk would reach the same state without any rename at all — Persist
        // must refuse either way). Merging by id is ambiguous under a
        // collision: refuse entirely rather than silently pick a winner and
        // discard whichever row (or concurrent counter advance) loses.
        var path = PathWith(
            new LabelClient { Id = "AAAA", DestroyDays = 30, NextNumber = 7 },
            new LabelClient { Id = "BBBB", DestroyDays = 30, NextNumber = 10 });
        var vm = Vm(path);

        vm.Clients.Single(c => c.Id == "AAAA").Id = "BBBB";   // collision: two rows now say "BBBB"

        // another station advances BBBB's counter while the collision sits
        // on screen — this must survive untouched
        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "BBBB").NextNumber = 99; return 0; });

        vm.TryPersist();

        Assert.Contains("share the id", Assert.Single(_dialogs.Warnings).Message);
        var stored = BoxLabelStore.Read(path).LabelClients;
        Assert.Equal(2, stored.Count);                                    // nothing removed
        Assert.Equal(99, stored.Single(c => c.Id == "BBBB").NextNumber);   // nothing clobbered
    }

    [Fact]
    public void EditingAnUnrelatedFieldCannotRollBackAPeersCounterAdvance()
    {
        // Station A opens with client X at 100. Station B (a different
        // window/station, simulated here by a raw Mutate call) advances X to
        // 140 while A's window is still open. A never touches NextNumberText
        // — only an unrelated field — so A's stale in-memory 100 must not
        // overwrite B's 140 when A closes.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        vm.Clients.Single().DestroyDaysText = "60";   // unrelated edit — retention only
        vm.TryPersist();

        var stored = BoxLabelStore.Read(path).LabelClients.Single();
        Assert.Equal(60, stored.DestroyDays);    // our edit landed
        Assert.Equal(140, stored.NextNumber);    // the peer's advance survives — NOT rolled back to 100
    }

    [Fact]
    public void DeliberatelyEditingTheNextNumberFieldStillWinsOverTheDisk()
    {
        // The fix above must not make the counter read-only: a user who
        // actually types a new NextNumberText is making a deliberate
        // correction, and that value must land even though the disk holds a
        // peer's own concurrent advance at Persist time.
        var path = PathWith(new LabelClient { Id = "XXXX", DestroyDays = 30, NextNumber = 100 });
        var vm = Vm(path);

        BoxLabelStore.Mutate(path, d =>
            { d.LabelClients.Single(c => c.Id == "XXXX").NextNumber = 140; return 0; });

        vm.Clients.Single().NextNumberText = "500";   // deliberate correction
        vm.TryPersist();

        Assert.Equal(500, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
    }

    [Fact]
    public void ClaimNumbersDoesNotDirtyItsClientSoAClaimAloneClosesWithoutWriting()
    {
        // ClaimNumbers already wrote the advance straight to the store — the
        // VM's own NextNumberText update afterward is display-only and must
        // not make Persist think there is local, unsaved state to merge
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 5 });
        var vm = Vm(path);

        Assert.Equal(5, vm.ClaimNumbers(vm.Clients.Single(), 10));
        Assert.Equal("15", vm.Clients.Single().NextNumberText);   // display updated...
        var afterClaim = File.ReadAllBytes(path);                // ...and the claim's write already landed

        vm.TryPersist();   // Persist has nothing of its own to write on top of that

        Assert.Equal(afterClaim, File.ReadAllBytes(path));
        Assert.Equal(15, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);   // the claim's write stands
    }

    // ------------------------------------------------ QC-06: parse failures

    [Theory]
    [InlineData("")]         // cleared the box
    [InlineData("4,211")]    // NumberStyles.Integer (the TryParse default) rejects the thousands separator
    [InlineData("abc")]
    public void PersistLeavesTheStoredNumberAloneWhenTheEditedTextDoesNotParse(string badText)
    {
        // The audit's own example: a client sitting at 4211 must not become
        // 1 on disk because the number box holds something un-parseable at
        // close time. ToClient()'s TryParse fallback (1) must never reach
        // the store here — assert on the STORE, not the VM, or a view model
        // that shows the right number while the store holds 1 would pass.
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 4211 });
        var vm = Vm(path);

        vm.Clients.Single().NextNumberText = badText;

        vm.TryPersist();

        Assert.Equal(4211, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("4,211")]   // parity with the number test — int.TryParse rejects it too
    public void PersistLeavesTheStoredRetentionAloneWhenTheEditedTextDoesNotParse(string badText)
    {
        // Same shape, the other field ToClient() silently defaults (to 30).
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 45, NextNumber = 7 });
        var vm = Vm(path);

        vm.Clients.Single().DestroyDaysText = badText;

        vm.TryPersist();

        Assert.Equal(45, BoxLabelStore.Read(path).LabelClients.Single().DestroyDays);
    }

    // --------------------------------------- QC-07: refusal must not close

    [Fact]
    public void TryPersistRefusesUnderDuplicateIdsWithoutDiscardingAnUnrelatedEdit()
    {
        // The old void Persist() gave the window's Closing handler nothing
        // to check, so it closed anyway on a duplicate id — discarding every
        // edit in the session, including this unrelated one. TryPersist must
        // report the refusal, AND the unrelated edit must still be pending
        // (not silently dropped) so a later, successful TryPersist lands it.
        var path = PathWith(
            new LabelClient { Id = "AAAA", DestroyDays = 30, NextNumber = 7 },
            new LabelClient { Id = "BBBB", DestroyDays = 30, NextNumber = 10 },
            new LabelClient { Id = "CCCC", DestroyDays = 30, NextNumber = 20 });
        var vm = Vm(path);
        var collider = vm.Clients.Single(c => c.Id == "AAAA");
        var unrelated = vm.Clients.Single(c => c.Id == "CCCC");

        unrelated.DestroyDaysText = "90";   // a deliberate edit, unrelated to the collision below
        collider.Id = "BBBB";               // collision: two rows now say "BBBB"

        Assert.False(vm.TryPersist());
        Assert.Contains("share the id", Assert.Single(_dialogs.Warnings).Message);
        Assert.Equal(30, BoxLabelStore.Read(path).LabelClients.Single(c => c.Id == "CCCC").DestroyDays);

        collider.Id = "AAAB";   // fix the duplicate

        Assert.True(vm.TryPersist());
        Assert.Equal(90, BoxLabelStore.Read(path).LabelClients.Single(c => c.Id == "CCCC").DestroyDays);
    }

    [Fact]
    public void TryPersistDoesNotBlockOnAStoreFailureButWarnsThatNothingWasSaved()
    {
        // Fix round 1 (code review, 2026-08-22): unlike a duplicate id, a
        // busy or corrupt store file has no in-window fix-and-retry — the
        // user cannot rename their way out of it. Blocking the close there
        // would trap them the same way ShutdownDuringCommitTests.cs:239-241
        // already recorded once ("The window never closed. That is a worse
        // defect than the one this task set out to fix."), so TryPersist
        // must let the close proceed and say plainly, in the warning itself,
        // that this session's edits were not saved.
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 7 });
        var vm = Vm(path);
        vm.Clients.Single().DestroyDaysText = "45";   // a pending edit — otherwise TryPersist never
                                                       // attempts a write at all (zero-edit close)

        // Simulate a crash mid-write elsewhere: the file exists but is now
        // empty. BoxLabelStore.Mutate refuses to treat that as "no clients
        // yet" (2026-08-04 audit 2.2) and throws instead.
        File.WriteAllText(path, "");

        Assert.True(vm.TryPersist());   // must NOT trap the user the way a duplicate id does
        var warning = Assert.Single(_dialogs.Warnings).Message;
        Assert.Contains("interrupted", warning);                              // the store's own diagnosis
        Assert.Contains("None of this session's changes were saved", warning); // the promise, kept via the message
    }

    // --------------------------------------------- QC-14: Reset confirms

    [Fact]
    public void ResetDeclinedLeavesTheNumberUnchanged()
    {
        var path = PathWith(new LabelClient { Id = "MEDR", NextNumber = 4242 });
        var vm = Vm(path);

        _dialogs.ConfirmAnswer = false;   // "No" — the counter survives
        vm.ResetNumberCommand.Execute(null);

        Assert.Equal("4242", vm.Selected!.NextNumberText);
        Assert.Contains("4242", Assert.Single(_dialogs.Confirms).Message);
    }

    [Fact]
    public void ResetAcceptedSetsTheNumberToOne()
    {
        var path = PathWith(new LabelClient { Id = "MEDR", NextNumber = 4242 });
        var vm = Vm(path);

        _dialogs.ConfirmAnswer = true;   // "Yes" — deliberate reset
        vm.ResetNumberCommand.Execute(null);

        Assert.Equal("1", vm.Selected!.NextNumberText);
        Assert.Single(_dialogs.Confirms);   // the house pattern asks first, even when accepted
    }

    // Note: no test pins the pristine (just-added blank row) carve-out on
    // ResetNumberCommand — a candidate for one is trivially true against
    // BOTH the unfixed code (which never confirms anything) and the fixed
    // code (which confirms everything except the pristine case), so it
    // cannot fail against the defect this task fixes and would be exactly
    // the accidental-pass trap this repo has been bitten by four times. The
    // carve-out is still implemented, matching RemoveClientCommand's shape;
    // it is just not separately claimed as tested here.

    // -------------------------------------------------------------- ceiling

    [Fact]
    public void ClaimNumbersRefusesABatchThatWouldPassTheCeilingAndLeavesTheCounterUnchanged()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", NextNumber = BoxLabels.MaxNumber - 1 });
        var vm = Vm(path);

        var start = vm.ClaimNumbers(vm.Clients.Single(), 3);

        Assert.Null(start);
        Assert.Contains("99 999 999", Assert.Single(_dialogs.Warnings).Message);
        Assert.Equal(BoxLabels.MaxNumber - 1, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
    }

    // ----------------------------------------------------------- date style

    [Fact]
    public void DateStyleIsSeededFromTheStoreAtOpen()
    {
        var path = PathWith();
        BoxLabelStore.Mutate(path, d => { d.DateStyle = BoxLabels.DateStylePlain; return 0; });

        var vm = Vm(path);

        Assert.True(vm.DateStylePlain);
        Assert.False(vm.DateStyleBars);
    }

    [Fact]
    public void FlippingTheDateStyleRadioPersistsImmediately()
    {
        var path = PathWith();   // defaults to "bars"
        var vm = Vm(path);
        Assert.True(vm.DateStyleBars);

        vm.DateStylePlain = true;

        Assert.True(vm.DateStylePlain);
        Assert.False(vm.DateStyleBars);
        Assert.Equal(BoxLabels.DateStylePlain, BoxLabelStore.Read(path).DateStyle);

        vm.DateStyleBars = true;

        Assert.True(vm.DateStyleBars);
        Assert.Equal(BoxLabels.DateStyleBars, BoxLabelStore.Read(path).DateStyle);
    }
}
