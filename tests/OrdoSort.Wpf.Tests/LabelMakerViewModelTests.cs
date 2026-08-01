using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class LabelMakerViewModelTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("fr_labelvm").FullName;
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

        vm.Persist();
        Assert.Equal("ABCD", Assert.Single(BoxLabelStore.Read(path).LabelClients).Id);

        vm.RemoveClientCommand.Execute(null);
        Assert.Empty(vm.Clients);
        vm.Persist();
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

        vm.Persist();

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

        vm.Persist();

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

        vm.Persist();

        var only = Assert.Single(BoxLabelStore.Read(path).LabelClients);
        Assert.Equal("NEWX", only.Id);
        Assert.Equal(42, only.NextNumber);       // untouched field carries over
        Assert.Equal(30, only.DestroyDays);
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

        vm.Persist();

        Assert.Contains("share the id", Assert.Single(_dialogs.Warnings).Message);
        var stored = BoxLabelStore.Read(path).LabelClients;
        Assert.Equal(2, stored.Count);                                    // nothing removed
        Assert.Equal(99, stored.Single(c => c.Id == "BBBB").NextNumber);   // nothing clobbered
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

        vm.Persist();   // Persist has nothing of its own to write on top of that

        Assert.Equal(afterClaim, File.ReadAllBytes(path));
        Assert.Equal(15, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);   // the claim's write stands
    }

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
