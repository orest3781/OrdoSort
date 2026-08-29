# Zip, Unzip, and Merge With Passwords — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two single-purpose tool windows — *Zip and unzip* and *Merge PDFs* — where a password-protected zip or PDF (loose or inside an archive) asks for its password instead of failing, the passwords the app already knows are tried first, and a skipped row stays runnable.

**Architecture:** Core grows one dependency (SharpZipLib, reads only) and one contract (`PasswordRequest` + an `ask` callback threaded through every locked operation, with the candidates-then-ask loop written once in `Passwords.Resolve`). `Zipper.Extract`/`Probe` and `PdfMerge.MergeZip`/`MergeFiles` take that pair; `PdfPasswords` is the one place that knows what "wrong password" looks like to PdfSharp. In Wpf, `ZipListViewModel` runs *units* (one zip row, or the whole loose-PDF group) instead of rows, owns the typed-plus-saved candidate list, marshals the prompt with `SynchronizationContext.Send`, and probes new rows off-thread. The 2026-08-25 window split (two windows, no `TabControl`) is carried out unchanged.

**Tech Stack:** .NET 8, WPF, PdfSharp 6.1.1, SharpZipLib 1.4.2 (new), xUnit 2.5.3, the repo's own E2E harness (`tools/OrdoSort.Smoke`).

**Spec:** `docs/superpowers/specs/2026-08-28-zip-and-merge-redo-design.md` — read it before Task 1; it carries the reasoning this plan only executes. The measurements below (2026-08-28, SharpZipLib 1.4.2, a throwaway console program) are facts the plan's code relies on:

| Measured | Consequence |
|---|---|
| A wrong ZipCrypto password throws `ZipException("Invalid password")` at `GetInputStream`; a wrong AES one throws `ZipException("Invalid password for AES")`; no password on an encrypted entry throws `ZipException("No password available for encrypted stream")`. | Catch `SharpZipBaseException` (the base of `ZipException`) around decrypt attempts and treat it as *wrong password*. |
| A wrong ZipCrypto password that passes the 1-byte header check (`wrong147` on the fixture) returned 39 bytes of garbage **silently** from a *stored* entry; from a deflated one it threw `SharpZipBaseException("Unexpected EOF")`. | The CRC check is load-bearing. Compute `Crc32` over the decrypted bytes and compare with `ZipEntry.Crc` for every encrypted non-AES entry. |
| AES entries store no CRC (`entry.Crc == 0`); a colliding AES password (`wrong14796`) threw `ZipException("AES Authentication Code does not match…")` at end of stream. | Skip the CRC compare when `entry.AESKeySize > 0`; always read to the END of the stream so the authentication check runs. |
| Reading an archive written by `System.IO.Compression`, SharpZipLib hands back entry names verbatim: `..\evil.txt`, `../evil2.txt`, `/rooted.txt`, `C:\drive.txt`. Writing through SharpZipLib's own `ZipOutputStream` *cleans* names (`C:\drive.txt` → `drive.txt`). | The ZipSlip guard must refuse `..`, rooted, and drive-qualified names. ZipSlip fixtures must be written with `System.IO.Compression` (as today) or the traversal never reaches the guard. |
| `new ZipFile(path)` on a non-zip throws `ZipException("Cannot find central directory")`. | "not a valid zip", the same voice as today's `InvalidDataException`. |

## Global Constraints

- **One dependency, one direction.** `SharpZipLib` `1.4.2` in `OrdoSort.Core` only. Every zip *read* goes through it; zip *creation* stays on `System.IO.Compression` (`Zipper.CreateZip`, `BuildArchive`, `DefaultName` untouched). Never two libraries for one operation.
- **A password counts only if an entry decrypts *and* verifies** (CRC for ZipCrypto, SharpZipLib's authentication code for AES). The probe verifies against the smallest encrypted entry by uncompressed `ZipEntry.Size`; the run verifies every entry it writes.
- **The ZipSlip guard is ours now.** `Zipper.Extract` refuses any entry whose resolved path is not under the output folder — `..` segments, rooted names, drive-qualified names — before a byte is written.
- **Fail-whole, never partial** for merges: one unopenable PDF fails its whole unit (the zip, or the loose group). No merged output ever silently omits a document.
- **`needs_password` is runnable.** Rows in `Pending` *or* `NeedsPassword` are selected by the next run and counted by every button.
- **Core remembers nothing.** Candidate order (typed-this-window, most recent first; then saved) is the view model's; Core takes `IReadOnlyList<string> candidates` and `Func<PasswordRequest, string?>? ask` and tries them in that order.
- **Nothing is ever saved from these windows.** Saved passwords are read once at window open through `PasswordVault.Reveal`; Unlock stays the only writer.
- **Every Core operation never throws** — every failure is a result record, as `Zipper`, `ZipMerge` and `Unlock` already promise.
- **Copy:** window titles "OrdoSort — Zip and unzip" and "OrdoSort — Merge PDFs"; menu items `_Zip and unzip…` and `Merge _PDFs…` (accelerator `P`, icon `&#xE8A5;`); button labels `Zip N items` / `Zip to…` / `Extract N zips` / `Merge N items` / `Merge to…`; row notes *needs a password*, *a saved password opens this*, *open in another program*, *not merged — X needs a password*; prompt copy `X is password-protected.` / `X inside Y is password-protected.` / `That password didn't open it.` / buttons `Open` and `Skip this one`.
- **Both windows:** 700×520, min 580×420, window-level `AllowDrop`, one `DataGrid`, zero `TabControl`s.
- **The check before every commit:** `dotnet build OrdoSort.sln -t:Rebuild -v minimal` then `dotnet test OrdoSort.sln --no-build -v minimal`, and read the two `Passed!` lines and their counts — an exit code of 0 with no `Passed!` line means nothing ran (`docs/known-flakes.md`). Baseline at the start of this plan: **Core 698, Wpf 1895.**
- **Commit messages explain why**, end with the `Co-Authored-By` / `Claude-Session` trailers the session uses, and never skip hooks.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OrdoSort.Core/Passwords.cs` | **New.** `PasswordRequest`, `PasswordTry`, `PasswordResolution`, and `Passwords.Resolve` — the candidates-then-ask loop, written once. |
| `src/OrdoSort.Core/Zipper.cs` | `CreateZip` side untouched. `Extract` and `Probe` on SharpZipLib with the path guard and CRC/AES verification; `UnlockArchive` and `ReadEntry` are `internal` so `PdfMerge` reads archives the same way. |
| `src/OrdoSort.Core/PdfPasswords.cs` | **New.** `IsProvablyNotEncrypted` (moved from `Unlock`), `OpenWithPasswords`, `Open` — PdfSharp's password loop and exception discipline, in one place. |
| `src/OrdoSort.Core/Unlock.cs` | Calls `PdfPasswords`; no behaviour change (`UnlockTests`, `UnlockProbeAgreementTests` unchanged prove it). |
| `src/OrdoSort.Core/PdfMerge.cs` | **Renamed from `ZipMerge.cs`.** `MergeZip` gains passwords; `MergeFiles` and `DefaultName` new; one shared `AddPdf` routine; `MergeResult.Item`. |
| `src/OrdoSort.Wpf/Services/IDialogService.cs`, `DialogService.cs` | `AskPassword(PasswordRequest)`, defaulted to `null`. |
| `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml(.cs)` | **New.** The prompt, in `MessageWindow`'s shape. |
| `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` | Units, passwords, probe-on-add, `NeedsPassword`, `"pdf"` kind, `Mark`. |
| `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` | Unit builder, zip probe, new constructor. |
| `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` | `{pdf, zip}` intake, zips-then-group units, `MergeToCommand`, zip + PDF probes. |
| `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs` | **Deleted** in Task 9. |
| `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml(.cs)` | **New** in Task 8. |
| `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml(.cs)` | De-tabbed in Task 9; `DataContext` is `ZipExtractViewModel`. |
| `src/OrdoSort.Wpf/MainWindow.xaml(.cs)` | `Merge _PDFs…` item, `OnMergePdfs`, saved passwords handed to both tools. |
| `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs`, `Fixture.cs`, `Scenarios/*.cs` | Password queue, `EncryptedZip` fixture, retargeted and new scenarios. |
| `README.md`, `CONTEXT.md` | Nine Tools entries; `PdfMerge.MergeZipCore` in the created-by-me gate section. |

Each task ends green: every commit builds and passes the full suite. Task 8 briefly offers Merge PDFs in two places (the old tab and the new window); Task 9 removes the tab. That ordering is deliberate — no commit on this branch loses the feature.

---

### Task 1: The dependency and the password loop, written once

**Files:**
- Modify: `src/OrdoSort.Core/OrdoSort.Core.csproj`
- Create: `src/OrdoSort.Core/Passwords.cs`
- Test: `tests/OrdoSort.Core.Tests/PasswordsTests.cs`

**Interfaces:**
- Produces: `PasswordRequest(string Item, string? Inside, bool PreviousAttemptFailed)`; `enum PasswordTry { Opened, WrongPassword, Unreadable }`; `PasswordResolution(string Status, string? Password = null, int? MatchedIndex = null)` with `Status` ∈ `"opened" | "needs_password" | "unreadable"`; `Passwords.Resolve(IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string item, string? inside, Func<string, PasswordTry> tryWith)`. Tasks 2, 3 and 4 all call `Resolve`.

- [ ] **Step 1: Add the package reference**

In `src/OrdoSort.Core/OrdoSort.Core.csproj`, inside the first `<ItemGroup>` after the `PdfSharp` line:

```xml
    <!-- Every zip READ (extract, merge, probe) goes through SharpZipLib: it
         reads ZipCrypto and WinZip-AES archives, which System.IO.Compression
         cannot. Zip CREATION stays on System.IO.Compression — output is never
         encrypted, and ZipFile.Open's atomic FileMode.CreateNew created-gate
         in Zipper.CreateZip is proven and tested. One library per direction;
         never two per operation (2026-08-28 spec). -->
    <PackageReference Include="SharpZipLib" Version="1.4.2" />
```

Run: `dotnet build src/OrdoSort.Core -v quiet`
Expected: `0 Error(s)` (the package is already in the local NuGet cache; no network needed).

- [ ] **Step 2: Write the failing tests**

Create `tests/OrdoSort.Core.Tests/PasswordsTests.cs`:

```csharp
namespace OrdoSort.Core.Tests;

/// <summary>The candidates-then-ask loop every locked operation shares.
/// Nothing here touches a zip or a PDF: <c>tryWith</c> is scripted, so each
/// fact is about the ORDER things are tried in and WHEN the person is asked,
/// which is the whole contract.</summary>
public class PasswordsTests
{
    private static Func<string, PasswordTry> Opens(string right) =>
        pw => pw == right ? PasswordTry.Opened : PasswordTry.WrongPassword;

    [Fact]
    public void CandidatesAreTriedInOrderAndTheAskIsNeverReachedWhenOneWorks()
    {
        var tried = new List<string>();
        var asked = 0;

        var r = Passwords.Resolve(new[] { "a", "b", "c" }, _ => { asked++; return "typed"; },
            "doc.pdf", null, pw => { tried.Add(pw); return pw == "b" ? PasswordTry.Opened : PasswordTry.WrongPassword; });

        Assert.Equal("opened", r.Status);
        Assert.Equal("b", r.Password);
        Assert.Equal(1, r.MatchedIndex);
        Assert.Equal(new[] { "a", "b" }, tried);   // c was never needed
        Assert.Equal(0, asked);
    }

    [Fact]
    public void WhenNoCandidateWorksThePersonIsAskedAndATypedAnswerHasNoIndex()
    {
        var requests = new List<PasswordRequest>();

        var r = Passwords.Resolve(new[] { "a" }, req => { requests.Add(req); return "typed"; },
            "report.pdf", "Batch 12.zip", Opens("typed"));

        Assert.Equal("opened", r.Status);
        Assert.Equal("typed", r.Password);
        Assert.Null(r.MatchedIndex);
        var req = Assert.Single(requests);
        Assert.Equal("report.pdf", req.Item);
        Assert.Equal("Batch 12.zip", req.Inside);
        Assert.False(req.PreviousAttemptFailed);
    }

    [Fact]
    public void AWrongTypedAnswerIsAskedAgainWithTheFailedFlagUntilOneWorks()
    {
        var answers = new Queue<string?>(new[] { "bad", "worse", "right" });
        var flags = new List<bool>();

        var r = Passwords.Resolve(Array.Empty<string>(), req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); },
            "doc.pdf", null, Opens("right"));

        Assert.Equal("opened", r.Status);
        Assert.Equal("right", r.Password);
        Assert.Equal(new[] { false, true, true }, flags);
    }

    [Fact]
    public void SkippingThePromptIsNeedsPassword()
    {
        var r = Passwords.Resolve(new[] { "a" }, _ => null, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.Password);
    }

    [Fact]
    public void AnEmptyAnswerCountsAsASkip()
    {
        var asked = 0;
        var r = Passwords.Resolve(Array.Empty<string>(), _ => { asked++; return ""; }, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
        Assert.Equal(1, asked);   // not re-asked forever
    }

    [Fact]
    public void WithNoAskAtAllAnUnopenedItemIsNeedsPassword()
    {
        var r = Passwords.Resolve(new[] { "a", "b" }, ask: null, "doc.pdf", null, Opens("zzz"));
        Assert.Equal("needs_password", r.Status);
    }

    /// <summary>A damaged file is not a password problem. The first
    /// Unreadable stops everything — later candidates are not tried and the
    /// person is not asked — because asking for a password that cannot help
    /// would be a lie.</summary>
    [Fact]
    public void UnreadableStopsTheLoopWithoutAsking()
    {
        var tried = new List<string>();
        var asked = 0;

        var r = Passwords.Resolve(new[] { "a", "b" }, _ => { asked++; return "typed"; },
            "doc.pdf", null, pw => { tried.Add(pw); return PasswordTry.Unreadable; });

        Assert.Equal("unreadable", r.Status);
        Assert.Equal(new[] { "a" }, tried);
        Assert.Equal(0, asked);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~PasswordsTests" -v minimal`
Expected: build FAILS with `The type or namespace name 'Passwords' could not be found` (and `PasswordTry`, `PasswordRequest`).

- [ ] **Step 4: Write `Passwords.cs`**

Create `src/OrdoSort.Core/Passwords.cs`:

```csharp
namespace OrdoSort.Core;

/// <summary>What a locked item wants from the person: which item, where it
/// lives (null for a loose file or an archive itself; the archive's name for
/// an entry inside one), and whether the previous answer was tried and
/// failed — the prompt shows "That password didn't open it" on exactly that
/// flag.</summary>
public sealed record PasswordRequest(string Item, string? Inside, bool PreviousAttemptFailed);

/// <summary>What one attempt with one password came back as. WrongPassword
/// moves the loop on to the next candidate, or to the prompt; Unreadable
/// stops it — a damaged file is not a password problem, and asking again
/// would be a lie.</summary>
public enum PasswordTry { Opened, WrongPassword, Unreadable }

/// <summary>Status "opened": <see cref="Password"/> is the one that worked,
/// and <see cref="MatchedIndex"/> its position among the candidates — null
/// when it was typed at the prompt instead. "needs_password": nothing worked
/// and the prompt was skipped, or there was no prompt to ask. "unreadable":
/// an attempt failed for a reason no password can fix.</summary>
public sealed record PasswordResolution(string Status, string? Password = null, int? MatchedIndex = null);

/// <summary>The candidates-then-ask loop, written once for every locked
/// thing the app opens — a zip, a loose PDF, a PDF inside a zip. Core
/// remembers nothing: the caller owns the candidate list and the order it
/// comes in (the view models put what was typed in this window first, then
/// the Unlock tool's saved list), and this only walks it.</summary>
public static class Passwords
{
    /// <summary>Try every candidate in order, silently; only when none opens
    /// the item call <paramref name="ask"/>, and keep asking — with
    /// <see cref="PasswordRequest.PreviousAttemptFailed"/> set from the
    /// second time on — until an answer works or the answer is null or
    /// empty (a skip). <paramref name="tryWith"/> is the only thing that
    /// touches the item.</summary>
    public static PasswordResolution Resolve(
        IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask,
        string item, string? inside,
        Func<string, PasswordTry> tryWith)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            switch (tryWith(candidates[i]))
            {
                case PasswordTry.Opened: return new("opened", candidates[i], i);
                case PasswordTry.Unreadable: return new("unreadable");
            }
        }

        if (ask is null) return new("needs_password");

        var previousFailed = false;
        while (true)
        {
            var answer = ask(new PasswordRequest(item, inside, previousFailed));
            if (string.IsNullOrEmpty(answer)) return new("needs_password");
            switch (tryWith(answer))
            {
                case PasswordTry.Opened: return new("opened", answer, null);
                case PasswordTry.Unreadable: return new("unreadable");
            }
            previousFailed = true;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~PasswordsTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 6: Run the full check and commit**

Run the full check (Global Constraints). Expected: Core 705, Wpf 1895, no failures.

```bash
git add src/OrdoSort.Core/OrdoSort.Core.csproj src/OrdoSort.Core/Passwords.cs tests/OrdoSort.Core.Tests/PasswordsTests.cs
git commit -m "feat(core): the password loop, written once, and the library that will need it

Passwords.Resolve is the candidates-then-ask loop every locked operation
shares: try what the caller knows, silently and in order; only then ask,
and keep asking with the failed flag set until an answer works or the
person skips. Unreadable stops it — a damaged file is not a password
problem. Core remembers nothing; the caller owns the list and its order.

SharpZipLib 1.4.2 comes in for zip READS only — it decrypts ZipCrypto and
WinZip-AES archives, which System.IO.Compression cannot. Creation stays
where it is."
```

---

### Task 2: `Zipper.Extract` and `Zipper.Probe` on SharpZipLib, with passwords

**Files:**
- Modify: `src/OrdoSort.Core/Zipper.cs` (the `UnzipResult` record, everything from `Extract` down; the class comment's ZipSlip paragraph)
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs:19-28` (the extractor default only — keeps the build green until Task 6)
- Test: `tests/OrdoSort.Core.Tests/ZipperTests.cs`

**Interfaces:**
- Consumes: `Passwords.Resolve`, `PasswordRequest`, `PasswordTry` from Task 1.
- Produces:
  - `Zipper.UnzipResult(string Zip, string Status, string? OutputFolder, string Message = "")` — unchanged shape; `Status` now `"ok" | "needs_password" | "error"`.
  - `Zipper.ZipProbeResult(string Zip, string Status, int? MatchedIndex = null, string Message = "")` — `Status` ∈ `"not_encrypted" | "ready" | "needs_password" | "unreadable"`.
  - `public static UnzipResult Extract(string zipPath, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)`; `internal static UnzipResult Extract(…, Func<string, string>? pickOutputDir)` (the test seam, kept).
  - `public static ZipProbeResult Probe(string zipPath, IReadOnlyList<string> candidates)`.
  - `internal static PasswordResolution UnlockArchive(ZipFile zip, IReadOnlyList<ZipEntry> entries, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string zipName)` and `internal static byte[] ReadEntry(ZipFile zip, ZipEntry entry)` — Task 4's `PdfMerge` reads archives through these.
  - The one-argument `Extract(string)` is **removed**, not kept as an overload.

- [ ] **Step 1: Update the existing tests to the new signature and add the new facts**

In `tests/OrdoSort.Core.Tests/ZipperTests.cs`:

Add these usings at the top (keep `System.IO.Compression` — the CreateZip facts and the ZipSlip fixture still write through it):

```csharp
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;
```

Add to the class, after `MakeZip`:

```csharp
    private static readonly string[] NoPasswords = Array.Empty<string>();

    /// <summary>An <c>ask</c> that must never be reached: a fact passing a
    /// working candidate proves nothing if the prompt quietly rescued it.</summary>
    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    /// <summary>A locked zip through SharpZipLib's own writer — the only
    /// writer in reach that encrypts. ZipCrypto when <paramref name="aesKeySize"/>
    /// is 0, WinZip AES otherwise. Entries here are deflated; see
    /// MakeStoredLockedZip for the stored variant the check-byte fact needs.</summary>
    private string MakeLockedZip(string name, string password, int aesKeySize,
        params (string EntryName, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        foreach (var (entryName, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new ZipEntry(entryName) { Size = bytes.Length, AESKeySize = aesKeySize };
            zos.PutNextEntry(entry);
            zos.Write(bytes, 0, bytes.Length);
            zos.CloseEntry();
        }
        return path;
    }

    /// <summary>One STORED ZipCrypto entry — no Deflate to choke on garbage,
    /// so a password that slips past the 1-byte header check hands back
    /// garbage silently and only the CRC can tell (measured 2026-08-28).</summary>
    private string MakeStoredLockedZip(string name, string password, string content)
    {
        var path = Path.Combine(_dir, name);
        var bytes = Encoding.UTF8.GetBytes(content);
        var crc = new Crc32();
        crc.Update(bytes);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        var entry = new ZipEntry("s.txt")
        {
            Size = bytes.Length, Crc = crc.Value, CompressionMethod = CompressionMethod.Stored, AESKeySize = 0,
        };
        zos.PutNextEntry(entry);
        zos.Write(bytes, 0, bytes.Length);
        zos.CloseEntry();
        return path;
    }
```

Change every existing `Zipper.Extract(x)` call to `Zipper.Extract(x, NoPasswords, null)` — there are six: in `TwoTopLevelFoldersSharingANameGetACounterSuffixSoTheArchiveRoundTrips`, `ALooseFileAndATopLevelFolderSharingANameStillDedupeAndRoundTrip`, `ExtractCreatesASiblingFolderNamedAfterTheZipWithFullContents`, `ASecondExtractOfTheSameZipGetsACollisionSuffixedFolder` (two calls), `ZipSlipEntryIsRejectedAndLeavesNoTraceOutsideOrInside`, `CorruptZipIsAReadableErrorAndLeavesNoOutputFolder`. Change the seam call in `ExtractFailureNeverDeletesADirectoryThisCallDidNotCreate` to `Zipper.Extract(path, NoPasswords, null, pickOutputDir: _ => peerDir)`.

Rewrite the ZipSlip fact's doc comment and widen it into a theory (the guard is ours now, and it must refuse every form SharpZipLib hands back verbatim):

```csharp
    /// <summary>The ZipSlip guard is Zipper's own since the SharpZipLib move
    /// (2026-08-28) — see the class doc comment. Written with
    /// System.IO.Compression on purpose: that writer keeps entry names
    /// verbatim, while SharpZipLib's own writer cleans them (measured:
    /// "C:\drive.txt" became "drive.txt"), so a SharpZipLib-built fixture
    /// would never reach the guard at all. A crafted name that would land
    /// outside the destination must be refused and leave no trace either
    /// outside the zip's own directory or inside it (the partial output
    /// folder this call created before extraction failed).</summary>
    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData(@"C:\evil.txt")]
    public void ZipSlipEntryIsRejectedAndLeavesNoTraceOutsideOrInside(string entryName)
    {
        var zipPath = Path.Combine(_dir, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry(entryName).Open();
            var bytes = Encoding.UTF8.GetBytes("pwned");
            s.Write(bytes, 0, bytes.Length);
        }

        var r = Zipper.Extract(zipPath, NoPasswords, null);

        Assert.Equal("error", r.Status);
        Assert.Contains("outside", r.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(Path.GetFullPath(_dir))!, "evil.txt")));
        Assert.False(File.Exists(@"C:\evil.txt"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "slip")));
    }
```

Add the new facts at the end of the class (before its closing brace):

```csharp
    // ------------------------------------------------------ passwords

    [Theory]
    [InlineData(0)]     // ZipCrypto
    [InlineData(256)]   // WinZip AES
    public void ALockedZipExtractsWithTheRightCandidateAndNeverAsks(int aesKeySize)
    {
        var zipPath = MakeLockedZip("locked.zip", "right", aesKeySize, ("a.txt", "aaa"), ("sub/b.txt", "bbb"));

        var r = Zipper.Extract(zipPath, new[] { "nope", "right" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        var outDir = Path.Combine(_dir, "locked");
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(outDir, "sub", "b.txt")));
    }

    [Fact]
    public void WhenNoCandidateOpensItThePromptIsAskedForTheArchiveItself()
    {
        var zipPath = MakeLockedZip("asked.zip", "right", 0, ("a.txt", "aaa"));
        var requests = new List<PasswordRequest>();

        var r = Zipper.Extract(zipPath, new[] { "nope" }, req => { requests.Add(req); return "right"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("asked.zip", req.Item);
        Assert.Null(req.Inside);
        Assert.False(req.PreviousAttemptFailed);
    }

    [Fact]
    public void AWrongTypedPasswordIsAskedAgainWithTheFailedFlag()
    {
        var zipPath = MakeLockedZip("twice.zip", "right", 0, ("a.txt", "aaa"));
        var answers = new Queue<string?>(new[] { "bad", "right" });
        var flags = new List<bool>();

        var r = Zipper.Extract(zipPath, NoPasswords, req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); });

        Assert.Equal("ok", r.Status);
        Assert.Equal(new[] { false, true }, flags);
    }

    [Fact]
    public void SkippingThePromptIsNeedsPasswordAndLeavesNoFolder()
    {
        var zipPath = MakeLockedZip("skipped.zip", "right", 0, ("a.txt", "aaa"));

        var r = Zipper.Extract(zipPath, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.OutputFolder);
        Assert.False(Directory.Exists(Path.Combine(_dir, "skipped")));
    }

    [Fact]
    public void WithNoPromptALockedZipNobodyCanOpenIsNeedsPassword()
    {
        var zipPath = MakeLockedZip("noask.zip", "right", 0, ("a.txt", "aaa"));
        var r = Zipper.Extract(zipPath, new[] { "nope" }, ask: null);
        Assert.Equal("needs_password", r.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "noask")));
    }

    /// <summary>The correctness rule behind the CRC check. ZipCrypto's header
    /// check is one byte, so about 1 wrong password in 256 passes it — and on
    /// a STORED entry there is no Deflate to choke on the garbage: measured
    /// 2026-08-28, "wrong147" read 39 bytes silently with the CRC wrong.
    /// The header's 12 random bytes make the colliding password different
    /// every time the fixture is built, so the test finds one at runtime by
    /// asking SharpZipLib directly, then proves Zipper still refuses it.</summary>
    [Fact]
    public void AWrongPasswordThatPassesTheCheckByteIsStillRejected()
    {
        var zipPath = MakeStoredLockedZip("collide.zip", "right", "stored zipcrypto entry with a known crc");

        string? collider = null;
        using (var zip = new SzlZipFile(zipPath))
        {
            var entry = zip[0];
            for (var i = 0; i < 20000 && collider is null; i++)
            {
                zip.Password = "wrong" + i;
                try
                {
                    using var s = zip.GetInputStream(entry);   // throws "Invalid password" unless the check byte matches
                    collider = "wrong" + i;
                }
                catch (ZipException) { }
            }
        }
        Assert.NotNull(collider);   // (255/256)^20000 — a miss here means the fixture is not ZipCrypto

        var extracted = Zipper.Extract(zipPath, new[] { collider! }, ask: null);
        Assert.Equal("needs_password", extracted.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "collide")));

        var probed = Zipper.Probe(zipPath, new[] { collider! });
        Assert.Equal("needs_password", probed.Status);
    }

    [Fact]
    public void MixedEncryptedAndPlainEntriesExtractTogether()
    {
        var zipPath = Path.Combine(_dir, "mixed.zip");
        using (var fs = File.Create(zipPath))
        using (var zos = new ZipOutputStream(fs))
        {
            void Put(string name, string content, string? password)
            {
                zos.Password = password;
                var bytes = Encoding.UTF8.GetBytes(content);
                zos.PutNextEntry(new ZipEntry(name) { Size = bytes.Length, AESKeySize = 0 });
                zos.Write(bytes, 0, bytes.Length);
                zos.CloseEntry();
            }
            Put("plain.txt", "plain", null);
            Put("locked.txt", "locked", "right");
        }

        var r = Zipper.Extract(zipPath, new[] { "right" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal("plain", File.ReadAllText(Path.Combine(_dir, "mixed", "plain.txt")));
        Assert.Equal("locked", File.ReadAllText(Path.Combine(_dir, "mixed", "locked.txt")));
    }

    /// <summary>One password per archive: the password that opens the
    /// smallest encrypted entry is used for all of them, and an entry that
    /// rejects it fails the zip naming that entry — never a half-extracted
    /// folder left behind.</summary>
    [Fact]
    public void ALaterEntryWithADifferentPasswordFailsTheZipNamingIt()
    {
        var zipPath = Path.Combine(_dir, "two-passwords.zip");
        using (var fs = File.Create(zipPath))
        using (var zos = new ZipOutputStream(fs))
        {
            void Put(string name, string content, string password)
            {
                zos.Password = password;
                var bytes = Encoding.UTF8.GetBytes(content);
                zos.PutNextEntry(new ZipEntry(name) { Size = bytes.Length, AESKeySize = 0 });
                zos.Write(bytes, 0, bytes.Length);
                zos.CloseEntry();
            }
            Put("small.txt", "s", "right");                              // the smallest — the probe entry
            Put("other.txt", "a much longer entry body here", "different");
        }

        var r = Zipper.Extract(zipPath, new[] { "right" }, NeverAsked);

        Assert.Equal("error", r.Status);
        Assert.Contains("other.txt", r.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "two-passwords")));
    }

    // ---------------------------------------------------------- Probe

    [Fact]
    public void ProbeReportsNotEncryptedForAPlainZip()
    {
        var zipPath = MakeZip("plain.zip", ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "irrelevant" });
        Assert.Equal("not_encrypted", r.Status);
        Assert.Null(r.MatchedIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void ProbeReportsReadyWithTheIndexOfTheCandidateThatOpensIt(int aesKeySize)
    {
        var zipPath = MakeLockedZip("ready.zip", "right", aesKeySize, ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "nope", "right" });
        Assert.Equal("ready", r.Status);
        Assert.Equal(1, r.MatchedIndex);
    }

    [Fact]
    public void ProbeReportsNeedsPasswordWhenNoCandidateOpensIt()
    {
        var zipPath = MakeLockedZip("needs.zip", "right", 0, ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "nope" });
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.MatchedIndex);
    }

    [Fact]
    public void ProbeReportsUnreadableForSomethingThatIsNotAZip()
    {
        var path = Path.Combine(_dir, "junk.zip");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a zip"));
        var r = Zipper.Probe(path, new[] { "x" });
        Assert.Equal("unreadable", r.Status);
        Assert.Contains("not a valid zip", r.Message);
    }

    /// <summary>The probe writes nothing, anywhere — the same promise
    /// UnlockProbeWritesNothingTests holds Unlock.ProbeReadiness to, proven
    /// the same way: names, sizes and mtimes of the fixture directory before
    /// and after, and no new "ordosort_*" file at the top of %TEMP%.</summary>
    [Fact]
    public void ProbeWritesNothing()
    {
        MakeZip("plain.zip", ("a.txt", "aaa"));
        MakeLockedZip("ready.zip", "aaa", 0, ("a.txt", "aaa"));
        MakeLockedZip("needs.zip", "zzz", 256, ("a.txt", "aaa"));
        File.WriteAllBytes(Path.Combine(_dir, "junk.zip"), Encoding.UTF8.GetBytes("junk"));

        static (string, long, DateTime)[] Snapshot(string dir) => Directory.GetFiles(dir)
            .Select(f => (Path.GetFileName(f)!, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToArray();
        var before = Snapshot(_dir);
        var tempBefore = Directory.GetFiles(Path.GetTempPath(), "ordosort_*").ToHashSet(StringComparer.OrdinalIgnoreCase);

        Zipper.Probe(Path.Combine(_dir, "plain.zip"), new[] { "x" });
        Zipper.Probe(Path.Combine(_dir, "ready.zip"), new[] { "aaa" });
        Zipper.Probe(Path.Combine(_dir, "needs.zip"), new[] { "nope" });
        Zipper.Probe(Path.Combine(_dir, "junk.zip"), new[] { "x" });
        Zipper.Probe(Path.Combine(_dir, "missing.zip"), new[] { "x" });

        Assert.Equal(before, Snapshot(_dir));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "ordosort_*").Except(tempBefore, StringComparer.OrdinalIgnoreCase));
    }
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~ZipperTests" -v minimal`
Expected: build FAILS — `No overload for method 'Extract' takes 3 arguments`, `'Zipper' does not contain a definition for 'Probe'`.

- [ ] **Step 3: Rewrite the Extract side of `Zipper.cs`**

Replace the `using` block at the top of `src/OrdoSort.Core/Zipper.cs` with:

```csharp
using System.IO.Compression;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;
```

(`ZipFile.Open` in `CreateZipCore`/`BuildArchive` keeps resolving to `System.IO.Compression.ZipFile` through the alias; SharpZipLib's reader is `SzlZipFile`.)

Replace the class doc comment's final paragraph (the one beginning `/// ZipSlip: <see cref="Extract(string)"/> hands the whole zip to`) with:

```csharp
/// Reading goes through SharpZipLib (2026-08-28): it decrypts ZipCrypto and
/// WinZip-AES archives, which System.IO.Compression cannot, so Extract and
/// Probe take the candidate passwords the caller knows and an `ask`
/// callback for the ones it doesn't (see <see cref="Passwords.Resolve"/>).
/// Creation stays on System.IO.Compression: output is never encrypted, and
/// ZipFile.Open's atomic CreateNew above is proven.
///
/// Two things the old reader did for free are this class's own now, and
/// both are correctness rules rather than niceties:
///
/// ZipSlip: ZipFile.ExtractToDirectory refused any entry resolving outside
/// the destination. SharpZipLib hands entry names back exactly as the
/// archive stored them — measured: "..\evil.txt", "/rooted.txt" and
/// "C:\drive.txt" all arrive verbatim from an archive another tool wrote —
/// so <see cref="GuardedTarget"/> resolves every entry's full path itself
/// and refuses one that does not sit under the output folder before a byte
/// is written. ZipperTests' ZipSlip theory pins all four forms.
///
/// Verification: ZipCrypto's header check is one byte, so 1 wrong password
/// in 256 passes it and yields garbage — silently, on a stored entry
/// (measured 2026-08-28: "wrong147" read 39 bytes with the CRC wrong). So a
/// password counts only if the entry decrypts AND its CRC matches; AES
/// entries store no CRC (AE-2 writes zero) and are authenticated by
/// SharpZipLib itself at end of stream instead, which is why every read
/// runs to the END of the entry. The probe verifies against the smallest
/// encrypted entry, which bounds its cost; Extract verifies every entry it
/// writes. One password per archive: the one that opens the smallest
/// encrypted entry is set for all of them, and an entry that rejects it
/// fails the zip naming that entry.
/// </summary>
```

Replace the `UnzipResult` record line with:

```csharp
    public sealed record UnzipResult(string Zip, string Status, string? OutputFolder, string Message = "");  // "ok" | "needs_password" | "error"

    /// <summary>The read-only readiness verdict for one archive — the zip
    /// side of Unlock.ProbeReadiness. not_encrypted | ready (with the index
    /// into the candidates that opened it) | needs_password | unreadable.</summary>
    public sealed record ZipProbeResult(string Zip, string Status, int? MatchedIndex = null, string Message = "");
```

Delete everything from the `/// <summary>Extract every entry in <paramref name="zipPath"/> into a` doc comment down to (and including) `ExtractCore`, and put this in its place (keep `RemoveFileQuietly` and `RemoveDirectoryQuietly` below it):

```csharp
    /// <summary>Extract every entry in <paramref name="zipPath"/> into a
    /// fresh sibling folder named after the zip (collision-suffixed via
    /// <see cref="Collision.FreeDirectory"/>, so re-extracting the same zip
    /// never overwrites a previous run's output). A locked archive is opened
    /// with the first of <paramref name="candidates"/> that verifies, else
    /// with what <paramref name="ask"/> supplies; a skipped prompt (or no
    /// prompt at all) is "needs_password" and nothing is written. See the
    /// class doc comment for the path guard, the verification rule, and the
    /// created-gate discipline the cleanup below follows.</summary>
    public static UnzipResult Extract(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask) =>
        Extract(zipPath, candidates, ask, pickOutputDir: null);

    /// <summary>Test seam for the created-gate cleanup (see ExtractCore's own
    /// comment on `created`): <paramref name="pickOutputDir"/> defaults to
    /// <see cref="Collision.FreeDirectory"/> and stands in for it, so a test
    /// can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another process (or a user
    /// in Explorer) claiming that exact folder in the gap between the real
    /// FreeDirectory probe and this call's own Directory.Exists check,
    /// without needing real thread timing to provoke it. Same shape as
    /// PdfMerge.MergeZip's internal pickOutput seam.</summary>
    internal static UnzipResult Extract(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string>? pickOutputDir)
    {
        try
        {
            return ExtractCore(zipPath, candidates, ask, pickOutputDir ?? Collision.FreeDirectory);
        }
        catch (Exception ex)
        {
            return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
        }
    }

    private static UnzipResult ExtractCore(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string> pickOutputDir)
    {
        SzlZipFile zip;
        try
        {
            zip = new SzlZipFile(zipPath);
        }
        catch (ZipException)
        {
            // "Cannot find central directory" — readable voice for what is,
            // from the user's side, just a bad file.
            return new UnzipResult(zipPath, "error", null, "not a valid zip");
        }

        using (zip)
        {
            var entries = zip.Cast<ZipEntry>().ToList();

            // Passwords are settled BEFORE the output folder exists: a skipped
            // prompt must leave nothing behind, and there is nothing to clean up
            // if nothing was created.
            var archive = UnlockArchive(zip, entries, candidates, ask, Path.GetFileName(zipPath));
            if (archive.Status == "needs_password")
                return new UnzipResult(zipPath, "needs_password", null, "needs a password");
            if (archive.Status == "unreadable")
                return new UnzipResult(zipPath, "error", null, "couldn't extract: an encrypted entry couldn't be read");

            var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
            var zipStem = Path.GetFileNameWithoutExtension(zipPath);
            var dir = pickOutputDir(Path.Combine(zipDir, zipStem));

            // Directory.CreateDirectory is idempotent — unlike ZipFile.Open's
            // FileMode.CreateNew for the CreateZip file path, it does NOT throw
            // just because `dir` already exists (empty or not), so "did THIS
            // call create it" can't be inferred from CreateDirectory succeeding
            // the way `created = true` right after ZipFile.Open works above.
            // Instead `created` is decided by an explicit existence check taken
            // immediately before the create call — Collision.FreeDirectory (or
            // whatever pickOutputDir returns) only proves the name was free AT
            // CHECK TIME, and another process/user can still claim it in the gap
            // before this line runs. See the class doc comment for why this is a
            // narrowed race window, not the atomic guarantee the file path gets.
            var created = false;
            try
            {
                created = !Directory.Exists(dir);
                Directory.CreateDirectory(dir);
                foreach (var entry in entries) WriteEntry(zip, entry, dir);
                return new UnzipResult(zipPath, "ok", dir);
            }
            catch (Exception ex)
            {
                // Covers the path guard's refusal, a wrong-password entry
                // failing verification, and ordinary IO failures (locked file,
                // gone share, out of disk space mid-extract).
                if (created) RemoveDirectoryQuietly(dir);
                return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
            }
        }
    }

    /// <summary>Read-only readiness check: does one of <paramref name="candidates"/>
    /// already open this archive? Never writes, moves or deletes anything —
    /// ZipperTests.ProbeWritesNothing holds it to that. The verdicts mirror
    /// Unlock.ProbeReadiness's, minus in_use (an archive is opened once,
    /// read-shared, and a locked one surfaces as unreadable).</summary>
    public static ZipProbeResult Probe(string zipPath, IReadOnlyList<string> candidates)
    {
        try
        {
            using var zip = new SzlZipFile(zipPath);
            var entries = zip.Cast<ZipEntry>().ToList();
            var archive = UnlockArchive(zip, entries, candidates, ask: null, Path.GetFileName(zipPath));
            return archive.Status switch
            {
                "not_encrypted" => new ZipProbeResult(zipPath, "not_encrypted", Message: "This zip isn't password-protected."),
                "opened" => new ZipProbeResult(zipPath, "ready", archive.MatchedIndex, "A saved password opens this."),
                "needs_password" => new ZipProbeResult(zipPath, "needs_password",
                    Message: "This zip needs a password none of the saved ones supply."),
                _ => new ZipProbeResult(zipPath, "unreadable", Message: "An encrypted entry couldn't be read."),
            };
        }
        catch (ZipException)
        {
            return new ZipProbeResult(zipPath, "unreadable", Message: "not a valid zip");
        }
        catch (Exception ex)
        {
            return new ZipProbeResult(zipPath, "unreadable", Message: $"Couldn't read it: {ex.Message}");
        }
    }

    /// <summary>Settles the archive's password when any entry is encrypted:
    /// "not_encrypted" when none is (nothing to do); otherwise
    /// <see cref="Passwords.Resolve"/> over the smallest encrypted entry, and
    /// on "opened" the password is left set on <paramref name="zip"/> for
    /// every later read. Internal so PdfMerge opens archives exactly this way.</summary>
    internal static PasswordResolution UnlockArchive(SzlZipFile zip, IReadOnlyList<ZipEntry> entries,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string zipName)
    {
        var probeEntry = SmallestEncryptedEntry(entries);
        if (probeEntry is null) return new PasswordResolution("not_encrypted");

        var resolution = Passwords.Resolve(candidates, ask, zipName, inside: null,
            password => Decrypts(zip, probeEntry, password));
        if (resolution.Status == "opened") zip.Password = resolution.Password;
        return resolution;
    }

    /// <summary>The whole decrypted entry, verified (see <see cref="CopyVerified"/>).
    /// Throws InvalidDataException when it does not verify. Internal for
    /// PdfMerge, which buffers PDF entries the same way.</summary>
    internal static byte[] ReadEntry(SzlZipFile zip, ZipEntry entry)
    {
        using var output = new MemoryStream();
        using (var input = zip.GetInputStream(entry))
        {
            if (!CopyVerified(input, entry, output))
                throw new InvalidDataException($"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry");
        }
        return output.ToArray();
    }

    private static ZipEntry? SmallestEncryptedEntry(IReadOnlyList<ZipEntry> entries)
    {
        ZipEntry? smallest = null;
        foreach (var entry in entries)
        {
            if (!entry.IsCrypted || !entry.IsFile) continue;
            if (smallest is null || entry.Size < smallest.Size) smallest = entry;
        }
        return smallest;
    }

    /// <summary>One attempt with one password against one entry, read to the
    /// END of the stream so ZipCrypto's CRC can be compared and AES's
    /// authentication code gets checked. SharpZipLib's own exceptions —
    /// "Invalid password" from the header check, an inflater choking on
    /// garbage, the AES code failing — are all the same answer: wrong
    /// password. Anything else (an IO failure) is unreadable.</summary>
    private static PasswordTry Decrypts(SzlZipFile zip, ZipEntry entry, string password)
    {
        zip.Password = password;
        try
        {
            using var stream = zip.GetInputStream(entry);
            return CopyVerified(stream, entry, Stream.Null) ? PasswordTry.Opened : PasswordTry.WrongPassword;
        }
        catch (SharpZipBaseException)
        {
            return PasswordTry.WrongPassword;
        }
        catch (Exception)
        {
            return PasswordTry.Unreadable;
        }
    }

    /// <summary>Copies an entry's decrypted bytes to <paramref name="destination"/>,
    /// computing the CRC on the way. False when an encrypted, non-AES entry's
    /// CRC does not match what the archive recorded — the only thing that
    /// catches a wrong password the 1-byte header check let through. Plain
    /// entries are not second-guessed (the old reader never checked them
    /// either), and AES entries store no CRC at all (measured: entry.Crc is
    /// 0) — SharpZipLib authenticates those itself at end of stream, which is
    /// why this always reads to the end.</summary>
    private static bool CopyVerified(Stream source, ZipEntry entry, Stream destination)
    {
        var crc = new Crc32();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc.Update(new ArraySegment<byte>(buffer, 0, read));
            destination.Write(buffer, 0, read);
        }
        if (!entry.IsCrypted || entry.AESKeySize > 0) return true;
        return (uint)crc.Value == (uint)entry.Crc;
    }

    private static void WriteEntry(SzlZipFile zip, ZipEntry entry, string dir)
    {
        var target = GuardedTarget(dir, entry.Name);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // CreateNew, so a duplicate entry path fails loudly instead of the
        // second one silently overwriting the first — the behaviour
        // ZipFile.ExtractToDirectory had, and which CreateZip's in-archive
        // dedupe exists to avoid producing.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using var input = zip.GetInputStream(entry);
            if (!CopyVerified(input, entry, output))
                throw new InvalidDataException($"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry");
        }
        catch (SharpZipBaseException ex)
        {
            // One password per archive: an entry the archive's password does
            // not open ("Invalid password" from its header check, or the
            // inflater/AES check failing further in) fails the zip NAMING
            // the entry — SharpZipLib's own message never says which one.
            throw new InvalidDataException(
                $"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry ({ex.Message})");
        }
    }

    /// <summary>The ZipSlip guard. Resolves where <paramref name="entryName"/>
    /// would land and refuses anything not strictly under <paramref name="dir"/>:
    /// ".." segments resolve above it, a rooted name ("/evil.txt") resolves to
    /// the drive root, and a drive-qualified one ("C:\evil.txt") makes
    /// Path.Combine discard the folder altogether — all three arrive verbatim
    /// from SharpZipLib. Checked before a byte is written; the caller's
    /// created-gate cleanup removes whatever this call created up to then.</summary>
    private static string GuardedTarget(string dir, string entryName)
    {
        var relative = entryName.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"refused '{entryName}' — it would land outside the output folder");
        return full;
    }
```

- [ ] **Step 4: Keep the view model compiling**

In `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs`, change the one line

```csharp
        _extractor = extractor ?? Zipper.Extract;
```

to

```csharp
        // No passwords yet — Task 6 threads the window's candidates and its
        // prompt through here; until then a locked zip reports needs_password.
        _extractor = extractor ?? (path => Zipper.Extract(path, Array.Empty<string>(), null));
```

- [ ] **Step 5: Run the Zipper suite to verify it passes**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~ZipperTests" -v minimal`
Expected: `Passed!` with 0 failures. If `AWrongPasswordThatPassesTheCheckByteIsStillRejected` fails on `Assert.NotNull(collider)`, the fixture is not ZipCrypto — check `AESKeySize = 0` on the stored entry.

- [ ] **Step 6: Run the full check and commit**

Run the full check. Expected: Core and Wpf both `Failed: 0` (Core count rises by the new facts; Wpf unchanged at 1895).

```bash
git add src/OrdoSort.Core/Zipper.cs src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs tests/OrdoSort.Core.Tests/ZipperTests.cs
git commit -m "feat(zip): extract and probe through SharpZipLib, asking for a password instead of failing

System.IO.Compression has no encryption support, so a locked zip came back
as a corrupt-stream error. Extract now reads through SharpZipLib, tries the
caller's candidates against the smallest encrypted entry, asks through the
callback for what none of them opens, and reports needs_password — nothing
written — when the prompt is skipped. Probe is the same resolution with no
prompt and no output: the zip side of Unlock.ProbeReadiness.

Two things the old reader did for free are Zipper's own now. The ZipSlip
guard: SharpZipLib hands entry names back verbatim — measured: ..\, a
rooted name and a drive-qualified one all arrive as written — so every
entry's full path is resolved and refused unless it sits under the output
folder. Verification: ZipCrypto's one-byte header check lets 1 wrong
password in 256 through, and on a stored entry the garbage comes out
silently (measured: 39 bytes, CRC wrong), so a password counts only if the
entry decrypts AND its CRC matches; AES entries carry an authentication
code SharpZipLib checks at end of stream, which is why every read runs to
the end."
```

---

### Task 3: `PdfPasswords` — PdfSharp's password loop, in one place

**Files:**
- Create: `src/OrdoSort.Core/PdfPasswords.cs`
- Modify: `src/OrdoSort.Core/Unlock.cs` (`ProbeReadiness`'s candidate loop, `UnlockBuffered`'s open, `IsProvablyNotEncrypted` moves out, `IsInUse` becomes internal)
- Test: `tests/OrdoSort.Core.Tests/PdfPasswordsTests.cs`

**Interfaces:**
- Consumes: `Passwords.Resolve`, `PasswordRequest`, `PasswordTry` (Task 1).
- Produces:
  - `PdfPasswords.OpenOutcome(string Status, PdfDocument? Document = null, MemoryStream? Stream = null, int? MatchedIndex = null, string Message = "")` with `Status` ∈ `"opened" | "needs_password" | "unreadable"`. On `"opened"` the caller owns and disposes `Document` and `Stream`, and must keep BOTH alive until it has saved whatever it built from the pages (PdfSharp resolves page objects lazily from the source).
  - `PdfPasswords.OpenWithPasswords(byte[] bytes, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string item, string? inside)` — candidates, then the prompt; used by `Unlock` (which has already proved the file encrypted) and by `Open`.
  - `PdfPasswords.Open(byte[] bytes, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string item, string? inside)` — tries no password first, so an unencrypted PDF never reaches the prompt; used by `PdfMerge` (Task 4).
  - `PdfPasswords.IsProvablyNotEncrypted(Stream stream)` — moved verbatim from `Unlock`.
  - `Unlock.IsInUse(IOException)` becomes `internal static` (Task 4's `MergeFiles` uses it).

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Core.Tests/PdfPasswordsTests.cs`:

```csharp
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

/// <summary>The PdfSharp side of the password contract: candidates before
/// the prompt, the prompt only for something encrypted, and a damaged file
/// reported as damaged rather than mistaken for a locked one. Real PdfSharp
/// documents throughout (ZipMergeTests' own fixture voice) — the exception
/// discipline under test is PdfSharp's, so nothing here can be faked.</summary>
public class PdfPasswordsTests
{
    private static byte[] MakePdfBytes(int pageCount = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static byte[] MakeEncryptedPdfBytes(string userPassword)
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPassword;
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    [Fact]
    public void APlainPdfOpensWithoutTouchingCandidatesOrThePrompt()
    {
        var r = PdfPasswords.Open(MakePdfBytes(2), new[] { "irrelevant" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.Equal(2, r.Document!.PageCount);
        r.Document.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void ALockedPdfOpensWithTheCandidateThatMatchesAndReportsItsIndex()
    {
        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope", "secret" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Equal(1, r.MatchedIndex);
        Assert.Equal(1, r.Document!.PageCount);
        r.Document.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void WhenNoCandidateOpensItThePromptIsAskedWithTheItemAndWhereItLives()
    {
        var requests = new List<PasswordRequest>();

        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope" },
            req => { requests.Add(req); return "secret"; }, "report.pdf", "Batch 12.zip");

        Assert.Equal("opened", r.Status);
        Assert.Null(r.MatchedIndex);   // typed, not a candidate
        var req = Assert.Single(requests);
        Assert.Equal("report.pdf", req.Item);
        Assert.Equal("Batch 12.zip", req.Inside);
        Assert.False(req.PreviousAttemptFailed);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void AWrongAnswerIsAskedAgainWithTheFailedFlag()
    {
        var answers = new Queue<string?>(new[] { "bad", "secret" });
        var flags = new List<bool>();

        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), Array.Empty<string>(),
            req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); }, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Equal(new[] { false, true }, flags);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void SkippingThePromptIsNeedsPasswordWithNothingOpen()
    {
        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope" }, _ => null, "doc.pdf", null);

        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.Document);
        Assert.Null(r.Stream);
    }

    /// <summary>Random bytes under a .pdf name: no "%PDF", no "/Encrypt"
    /// anywhere. Damage is not a password problem — nobody is asked, and the
    /// reason PdfSharp gave is carried in Message.</summary>
    [Fact]
    public void GarbageIsUnreadableAndNobodyIsAsked()
    {
        var garbage = new byte[512];
        new Random(1234).NextBytes(garbage);

        var r = PdfPasswords.Open(garbage, new[] { "whatever" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("unreadable", r.Status);
        Assert.NotEqual("", r.Message);
    }

    [Fact]
    public void OpenWithPasswordsOnAPlainPdfStillOpensItOnTheFirstCandidate()
    {
        // Unlock proves a file unencrypted BEFORE reaching this loop, so
        // "plain" never actually gets here from Unlock — but PdfSharp opens
        // an unencrypted document under any password, and the loop must not
        // turn that into a lie about which password mattered.
        var r = PdfPasswords.OpenWithPasswords(MakePdfBytes(), new[] { "anything" }, null, "doc.pdf", null);
        Assert.Equal("opened", r.Status);
        Assert.Equal(0, r.MatchedIndex);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void IsProvablyNotEncryptedTellsPlainFromLocked()
    {
        using var plain = new MemoryStream(MakePdfBytes(), writable: false);
        using var locked = new MemoryStream(MakeEncryptedPdfBytes("secret"), writable: false);
        Assert.True(PdfPasswords.IsProvablyNotEncrypted(plain));
        Assert.False(PdfPasswords.IsProvablyNotEncrypted(locked));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~PdfPasswordsTests" -v minimal`
Expected: build FAILS with `The name 'PdfPasswords' does not exist`.

- [ ] **Step 3: Write `PdfPasswords.cs`**

Create `src/OrdoSort.Core/PdfPasswords.cs`:

```csharp
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// The one place that knows what "wrong password" looks like to PdfSharp.
/// Unlock carried this loop privately for its probe and its buffered unlock;
/// PdfMerge needs the same loop for a loose PDF and for one inside a zip,
/// with the prompt added. Written once here, over <see cref="Passwords.Resolve"/>,
/// so the exception discipline cannot drift between the three callers.
///
/// The discipline, verbatim from Unlock.ProbeReadiness's own doc comment:
/// <see cref="PdfReaderException"/> is a wrong password for that one
/// candidate — try the next; anything else — including a failure while
/// touching a page — is unreadable and stops. Collapsing these would report
/// a damaged file as merely needing a password.
///
/// Every successful open is followed by touching every page (VerifyReadable's
/// technique, see Unlock.cs) so a document whose page dictionaries are
/// broken is reported here, by the open, rather than later by AddPage —
/// exact parity with what Unlock's probe already does, and measured there to
/// cost nothing observable.
/// </summary>
public static class PdfPasswords
{
    /// <summary>"opened": <see cref="Document"/> and the <see cref="Stream"/>
    /// it reads from are the caller's to keep alive — BOTH, until whatever
    /// was built from the pages has been saved, because PdfSharp's Import
    /// mode resolves page objects from the source lazily — and then dispose.
    /// <see cref="MatchedIndex"/> is the winning candidate's position, or
    /// null when the password was typed at the prompt or none was needed.
    /// "needs_password": nothing worked and the prompt was skipped.
    /// "unreadable": <see cref="Message"/> says why.</summary>
    public sealed record OpenOutcome(string Status, PdfDocument? Document = null, MemoryStream? Stream = null,
        int? MatchedIndex = null, string Message = "");

    /// <summary>Shared by <see cref="Unlock.ProbeReadiness"/> and
    /// Unlock.UnlockBuffered — both need the identical no-password encryption
    /// check. Opening WITH a password cannot answer "is this encrypted",
    /// because a correctly decrypted document reports itself unencrypted
    /// just like one that never was. Returns true only when opening without
    /// a password succeeded AND proved the document unencrypted; false means
    /// "couldn't prove that" — encrypted, or damaged in a way that looks the
    /// same from here — and the caller falls through to its own
    /// password-based path either way. <paramref name="stream"/> must be
    /// freshly positioned at 0; this does not rewind it, so callers pass a
    /// stream they are about to discard.</summary>
    public static bool IsProvablyNotEncrypted(Stream stream)
    {
        try
        {
            using var probe = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return !probe.SecuritySettings.IsEncrypted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Open a PDF that may or may not be locked: no password first —
    /// an unencrypted document must never reach the prompt — then
    /// <see cref="OpenWithPasswords"/>. A document that fails to open without
    /// a password and carries no /Encrypt dictionary is damaged, not locked,
    /// and is reported unreadable without anyone being asked.</summary>
    public static OpenOutcome Open(byte[] bytes, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, string item, string? inside)
    {
        var plain = TryOpenPlain(bytes, out var plainFailure);
        if (plain is not null) return plain;
        if (!LooksEncrypted(bytes)) return new OpenOutcome("unreadable", Message: plainFailure);
        return OpenWithPasswords(bytes, candidates, ask, item, inside);
    }

    /// <summary>The candidate loop: every password in order, then the
    /// prompt, each attempt an open-plus-page-touch over a fresh view of
    /// <paramref name="bytes"/>. The source is read from disk exactly ONCE
    /// by the caller regardless of how many candidates are tried — the
    /// discipline Unlock.UnlockBuffered's doc comment explains (three
    /// separate opens over a share meant three full transfers).</summary>
    public static OpenOutcome OpenWithPasswords(byte[] bytes, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, string item, string? inside)
    {
        PdfDocument? opened = null;
        MemoryStream? openedStream = null;
        var unreadable = "";

        var resolution = Passwords.Resolve(candidates, ask, item, inside, password =>
        {
            var stream = new MemoryStream(bytes, writable: false);
            PdfDocument? doc = null;
            try
            {
                doc = PdfReader.Open(stream, password, PdfDocumentOpenMode.Import);
                for (var p = 0; p < doc.PageCount; p++) { var _ = doc.Pages[p]; }
                opened = doc;
                openedStream = stream;
                return PasswordTry.Opened;
            }
            catch (PdfReaderException)
            {
                doc?.Dispose();
                stream.Dispose();
                return PasswordTry.WrongPassword;
            }
            catch (Exception ex)
            {
                doc?.Dispose();
                stream.Dispose();
                unreadable = ex.Message;
                return PasswordTry.Unreadable;
            }
        });

        return resolution.Status switch
        {
            "opened" => new OpenOutcome("opened", opened, openedStream, resolution.MatchedIndex),
            "needs_password" => new OpenOutcome("needs_password"),
            _ => new OpenOutcome("unreadable", Message: unreadable),
        };
    }

    /// <summary>Opens with no password and touches every page; null when
    /// that fails for any reason, with the reason in <paramref name="failure"/>.
    /// A document that opens here but still reports itself encrypted (an
    /// owner-password-only PDF) counts as opened: Import mode reads it
    /// without the owner password, which is all a merge needs.</summary>
    private static OpenOutcome? TryOpenPlain(byte[] bytes, out string failure)
    {
        var stream = new MemoryStream(bytes, writable: false);
        PdfDocument? doc = null;
        try
        {
            doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            for (var p = 0; p < doc.PageCount; p++) { var _ = doc.Pages[p]; }
            failure = "";
            return new OpenOutcome("opened", doc, stream);
        }
        catch (Exception ex)
        {
            doc?.Dispose();
            stream.Dispose();
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>An encrypted PDF's trailer names its /Encrypt dictionary; a
    /// document with no such token anywhere in its bytes cannot be locked,
    /// however badly it failed to open. A plain byte scan, deliberately —
    /// parsing a file that has just failed to parse is not an option.</summary>
    private static bool LooksEncrypted(byte[] bytes)
    {
        var token = Encoding.ASCII.GetBytes("/Encrypt");
        return bytes.AsSpan().IndexOf(token) >= 0;
    }
}
```

- [ ] **Step 4: Point `Unlock` at it**

In `src/OrdoSort.Core/Unlock.cs`:

(a) Delete the private `IsProvablyNotEncrypted` method and its doc comment (the block beginning `/// <summary>Shared by <see cref="ProbeReadiness"/> and`). Change both call sites — in `ProbeReadiness` and in `UnlockBuffered` — from `IsProvablyNotEncrypted(probeStream)` to `PdfPasswords.IsProvablyNotEncrypted(probeStream)`.

(b) In `ProbeReadiness`, replace the whole candidate loop — from `for (var i = 0; i < candidates.Count; i++)` down to and including the final `return new("needs_password", src, Message: "This PDF needs a password none of the saved ones supply.");` — with:

```csharp
        // The candidate loop lives in PdfPasswords now (2026-08-28), shared
        // with the merge tools: the same open mode, the same
        // PdfReaderException-is-a-wrong-password discipline, and the same
        // page touch on the winner — which is what keeps the agreement with
        // UnlockPdf below meaningful rather than coincidental.
        var outcome = PdfPasswords.OpenWithPasswords(sourceBytes, candidates, ask: null,
            Path.GetFileName(src), inside: null);
        outcome.Document?.Dispose();
        outcome.Stream?.Dispose();
        return outcome.Status switch
        {
            "opened" => new("ready", src, MatchedIndex: outcome.MatchedIndex, Message: "A saved password opens this."),
            "unreadable" => new("unreadable", src, Message: $"Couldn't read it: {outcome.Message}"),
            _ => new("needs_password", src, Message: "This PDF needs a password none of the saved ones supply."),
        };
```

(c) In `UnlockBuffered`, replace the block from `byte[] unlockedBytes;` down to the closing brace of `catch (Exception ex) { return new("error", src, Message: $"Couldn't unlock it: {ex.Message}"); }` (the try that opens with the password, adds pages and saves) with:

```csharp
        var opened = PdfPasswords.OpenWithPasswords(sourceBytes, new[] { password }, ask: null,
            Path.GetFileName(src), inside: null);
        if (opened.Status == "needs_password")
            return new("wrong_password", src, Message: "That password didn't work.");
        if (opened.Status == "unreadable")
            return new("error", src, Message: $"Couldn't unlock it: {opened.Message}");

        byte[] unlockedBytes;
        try
        {
            using var inStream = opened.Stream!;
            using var input = opened.Document!;
            using var output = new PdfDocument();
            foreach (var page in input.Pages) output.AddPage(page);
            using var outStream = new MemoryStream();
            output.Save(outStream, closeStream: false);
            unlockedBytes = outStream.ToArray();
        }
        catch (Exception ex)
        {
            return new("error", src, Message: $"Couldn't unlock it: {ex.Message}");
        }
```

(d) Change `private static bool IsInUse(IOException ex)` to `internal static bool IsInUse(IOException ex)` — `PdfMerge.MergeFiles` needs the same sharing-violation test.

`UnlockStreaming` (the ≥32 MB path) is left alone: it streams from disk rather than buffering, takes exactly one password, and its `catch (PdfReaderException)` stays — it is the one caller that cannot hand a byte array to the helper without defeating its own reason to exist.

- [ ] **Step 5: Run the Core suite to verify it passes**

Run: `dotnet test tests/OrdoSort.Core.Tests -v minimal`
Expected: `Passed!` with 0 failures — the new `PdfPasswordsTests` green, and `UnlockTests`, `UnlockProbeTests`, `UnlockProbeAgreementTests`, `UnlockProbeWritesNothingTests`, `UnlockNeverOverwritesTests` all passing **without modification**. That unchanged pass is the proof the extraction changed no behaviour; if any of them fails, the helper diverged — fix the helper, not the test.

- [ ] **Step 6: Run the full check and commit**

```bash
git add src/OrdoSort.Core/PdfPasswords.cs src/OrdoSort.Core/Unlock.cs tests/OrdoSort.Core.Tests/PdfPasswordsTests.cs
git commit -m "refactor(core): one place knows what a wrong password looks like to PdfSharp

Unlock carried the candidate loop privately, for its probe and its buffered
unlock. The merge tools need the same loop for a loose PDF and for one
inside a zip, with the prompt added — and two copies of an exception
discipline is how they drift. PdfPasswords holds it once, over
Passwords.Resolve: PdfReaderException is a wrong password for that one
candidate, anything else is unreadable and stops, and every winner gets the
same page touch Unlock's probe already did.

Open tries no password first, so an unencrypted document never reaches the
prompt, and a document that fails to open with no /Encrypt dictionary in it
is reported damaged rather than mistaken for locked. Unlock's own suites
pass unmodified, which is the proof its behaviour did not move."
```

---

### Task 4: `PdfMerge` — merge from a zip with passwords, and merge loose PDFs

**Files:**
- Rename: `src/OrdoSort.Core/ZipMerge.cs` → `src/OrdoSort.Core/PdfMerge.cs` (`git mv`, then rewrite)
- Rename: `tests/OrdoSort.Core.Tests/ZipMergeTests.cs` → `tests/OrdoSort.Core.Tests/PdfMergeTests.cs`
- Modify (rename fallout only): `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs`, `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`, `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`, `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`, `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs`, `tests/OrdoSort.Wpf.Tests/DataGridSelectionContrastTests.cs`, `tests/OrdoSort.Wpf.Tests/DataGridNoteColourTests.cs`, `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs` (doc comments), `CONTEXT.md:67`

**Interfaces:**
- Consumes: `Zipper.UnlockArchive`, `Zipper.ReadEntry` (Task 2); `PdfPasswords.Open` (Task 3); `Unlock.IsInUse` (Task 3); `AtomicPlace.TryReplace`, `Collision.FreeFile`, `NaturalSort.Instance` (existing).
- Produces:
  - `PdfMerge.MergeResult(string Source, string Status, string? Output = null, int PdfCount = 0, int SkippedEntries = 0, string Message = "", string? Item = null)`; `Status` ∈ `"ok" | "no_pdfs" | "needs_password" | "error"`. `Item` is the file path (`MergeFiles`) or entry name (`MergeZip`) that stopped the merge; null on `ok`/`no_pdfs`.
  - `public static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)` and the `internal` `pickOutput` seam overload.
  - `public static MergeResult MergeFiles(IReadOnlyList<string> pdfPaths, string? outputPath, IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)`.
  - `public static string DefaultName(IReadOnlyList<string> pdfPaths)`.
  - The one-argument `ZipMerge.MergeZip(string)` is **removed**.

- [ ] **Step 1: Rename the files and every reference**

```bash
git mv src/OrdoSort.Core/ZipMerge.cs src/OrdoSort.Core/PdfMerge.cs
git mv tests/OrdoSort.Core.Tests/ZipMergeTests.cs tests/OrdoSort.Core.Tests/PdfMergeTests.cs
```

Then replace `ZipMerge.` with `PdfMerge.` (the qualifier — `ZipMerge.MergeResult`, `ZipMerge.MergeZip`, `ZipMerge.MergeZipCore`) in every file the list above names, and `class ZipMergeTests` with `class PdfMergeTests`. Prose mentions in doc comments ("ZipMerge's class comment", "the same discipline ZipMerge…") become `PdfMerge`. Run

```bash
grep -rn "ZipMerge" --include=*.cs --include=*.md --include=*.xaml . | grep -v "ZipMergeScenarios\|ZipMergeTests\|docs/superpowers/\|/bin/\|/obj/"
```

and fix whatever it still lists. Two names stay: the E2E class `ZipMergeScenarios` (its surface is called "Zip merge" and `E2ERunner`'s filter matches on it) and the spec/plan documents under `docs/superpowers/`, which record history.

- [ ] **Step 2: Update the existing tests and add the new facts**

In `tests/OrdoSort.Core.Tests/PdfMergeTests.cs`:

Add usings:

```csharp
using ICSharpCode.SharpZipLib.Zip;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;
```

Add helpers after `MakeZip`:

```csharp
    private static readonly string[] NoPasswords = Array.Empty<string>();

    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    private string MakePdfFile(string name, int pageCount = 1, double widthPt = 200)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, MakePdfBytes(pageCount, widthPt));
        return path;
    }

    private string MakeEncryptedPdfFile(string name, string userPassword = "secret")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, MakeEncryptedPdfBytes(userPassword));
        return path;
    }

    /// <summary>A password-protected ARCHIVE (AES-256, SharpZipLib's writer)
    /// holding PDFs — as distinct from a plain archive holding a locked PDF.</summary>
    private string MakeLockedZip(string name, string password, params (string EntryName, byte[] Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        foreach (var (entryName, content) in entries)
        {
            zos.PutNextEntry(new ZipEntry(entryName) { Size = content.Length, AESKeySize = 256 });
            zos.Write(content, 0, content.Length);
            zos.CloseEntry();
        }
        return path;
    }
```

Change every `ZipMerge.MergeZip(zip)` call to `PdfMerge.MergeZip(zip, NoPasswords, null)` (eight facts) and the seam call in `SaveFailureNeverDeletesAFileThisCallDidNotCreate` to `PdfMerge.MergeZip(zip, NoPasswords, null, pickOutput: _ => peerPath)`.

Rewrite fact (e) — this is the one existing case whose *outcome* changes by design, from a dead-end error to a runnable `needs_password`:

```csharp
    // (e) an encrypted entry nobody can open still fails the WHOLE zip and
    // leaves no output — but as needs_password, naming the entry, so the row
    // can be run again once someone knows the password. Fail-whole is
    // unchanged: no partial file from the entry that merged fine first.
    [Fact]
    public void AnEncryptedEntryNobodyCanOpenIsNeedsPasswordNamingItAndLeavesNoOutput()
    {
        var zip = MakeZip("locked.zip",
            ("a-ok.pdf", MakePdfBytes(1)),
            ("z-bad.pdf", MakeEncryptedPdfBytes()));

        var r = PdfMerge.MergeZip(zip, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Contains("z-bad.pdf", r.Message);
        Assert.Equal("z-bad.pdf", r.Item);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "locked.pdf")));
    }
```

Add the new facts at the end of the class:

```csharp
    // ------------------------------------------- passwords inside a zip

    [Fact]
    public void ALockedEntryOpensWithACandidateAndNobodyIsAsked()
    {
        var zip = MakeZip("cand.zip", ("a.pdf", MakePdfBytes(1)), ("b.pdf", MakeEncryptedPdfBytes("secret")));

        var r = PdfMerge.MergeZip(zip, new[] { "nope", "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
    }

    [Fact]
    public void ALockedEntryAsksWithTheZipAsWhereItLives()
    {
        var zip = MakeZip("asked.zip", ("b.pdf", MakeEncryptedPdfBytes("secret")));
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeZip(zip, NoPasswords, req => { requests.Add(req); return "secret"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("b.pdf", req.Item);
        Assert.Equal("asked.zip", req.Inside);
    }

    [Fact]
    public void ALockedArchiveOpensWithACandidate()
    {
        var zip = MakeLockedZip("archive.zip", "zippw", ("a.pdf", MakePdfBytes(2)));

        var r = PdfMerge.MergeZip(zip, new[] { "zippw" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
    }

    [Fact]
    public void ALockedArchiveSkippedIsNeedsPasswordForTheArchiveItself()
    {
        var zip = MakeLockedZip("archive2.zip", "zippw", ("a.pdf", MakePdfBytes(1)));
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeZip(zip, new[] { "nope" }, req => { requests.Add(req); return null; });

        Assert.Equal("needs_password", r.Status);
        Assert.Equal("archive2.zip", r.Item);
        Assert.Equal("archive2.zip", Assert.Single(requests).Item);
        Assert.Null(Assert.Single(requests).Inside);
        Assert.Null(r.Output);
    }

    // --------------------------------------------------- loose PDFs

    // "10.pdf" is created first and listed first; a merge that kept input
    // order or sorted lexically would get this backwards.
    [Fact]
    public void LoosePdfsMergeInNaturalOrderOfTheirNames()
    {
        var ten = MakePdfFile("10.pdf", widthPt: 110);
        var two = MakePdfFile("2.pdf", widthPt: 102);

        var r = PdfMerge.MergeFiles(new[] { ten, two }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(102, merged.Pages[0].Width.Point, 3);
        Assert.Equal(110, merged.Pages[1].Width.Point, 3);
    }

    /// <summary>The same default-name rule Zipper.DefaultName applies to a
    /// zip: the folder CONTAINING the first document, placed beside it.</summary>
    [Fact]
    public void TheDefaultOutputIsNamedAfterTheFolderAndPlacedBesideTheFirst()
    {
        var a = MakePdfFile("a.pdf");
        var b = MakePdfFile("b.pdf");

        var r = PdfMerge.MergeFiles(new[] { b, a }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf"), r.Output);
        Assert.Equal(Path.GetFileName(_dir) + ".pdf", PdfMerge.DefaultName(new[] { b, a }));
    }

    [Fact]
    public void APreExistingDefaultNameGetsACollisionSuffix()
    {
        var a = MakePdfFile("a.pdf");
        var taken = Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf");
        File.WriteAllText(taken, "existing");

        var r = PdfMerge.MergeFiles(new[] { a }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, Path.GetFileName(_dir) + " (2).pdf"), r.Output);
        Assert.Equal("existing", File.ReadAllText(taken));
    }

    /// <summary>Merge to… is a Save-As, and a Save-As path is an answer the
    /// dialog already asked the user to confirm: the file there is replaced —
    /// through AtomicPlace, so never by deleting it up front, and with no
    /// temp sibling left behind.</summary>
    [Fact]
    public void MergeToReplacesTheChosenPathAndLeavesNoTempSibling()
    {
        var a = MakePdfFile("a.pdf", pageCount: 3);
        var chosen = Path.Combine(_dir, "chosen.pdf");
        File.WriteAllText(chosen, "old content, not a real pdf");

        var r = PdfMerge.MergeFiles(new[] { a }, chosen, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(chosen, r.Output);
        using (var merged = PdfReader.Open(chosen, PdfDocumentOpenMode.Import))
            Assert.Equal(3, merged.PageCount);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_dir),
            f => f.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ALockedLoosePdfOpensWithACandidate()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");
        var plain = MakePdfFile("plain.pdf");

        var r = PdfMerge.MergeFiles(new[] { locked, plain }, null, new[] { "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
    }

    [Fact]
    public void ALockedLoosePdfAsksNamingTheFileWithNowhereInside()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeFiles(new[] { locked }, null, NoPasswords, req => { requests.Add(req); return "secret"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("locked.pdf", req.Item);
        Assert.Null(req.Inside);
    }

    /// <summary>Fail-whole for the loose group: one skipped document merges
    /// nothing, and Item names it so the caller can mark the right row.</summary>
    [Fact]
    public void SkippingALockedLoosePdfMergesNothingAndNamesIt()
    {
        var plain = MakePdfFile("a-plain.pdf");
        var locked = MakeEncryptedPdfFile("z-locked.pdf", "secret");

        var r = PdfMerge.MergeFiles(new[] { plain, locked }, null, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Equal(locked, r.Item);
        Assert.Equal("needs a password", r.Message);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf")));
    }

    [Fact]
    public void AGarbageLoosePdfIsAnErrorNamingItAndNobodyIsAsked()
    {
        var plain = MakePdfFile("a.pdf");
        var junk = Path.Combine(_dir, "junk.pdf");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var r = PdfMerge.MergeFiles(new[] { plain, junk }, null, NoPasswords, NeverAsked);

        Assert.Equal("error", r.Status);
        Assert.Equal(junk, r.Item);
        Assert.StartsWith("couldn't read it", r.Message);
        Assert.Null(r.Output);
    }

    [Fact]
    public void TheMergedOutputIsNotEncryptedEvenWhenEverySourceWas()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");

        var r = PdfMerge.MergeFiles(new[] { locked }, null, new[] { "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.False(merged.SecuritySettings.IsEncrypted);
    }

    [Fact]
    public void MergingNothingIsAnErrorNotAThrow()
    {
        var r = PdfMerge.MergeFiles(Array.Empty<string>(), null, NoPasswords, NeverAsked);
        Assert.Equal("error", r.Status);
        Assert.Equal("Merged.pdf", PdfMerge.DefaultName(Array.Empty<string>()));
    }
```

- [ ] **Step 3: Run the suite to verify it fails**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~PdfMergeTests" -v minimal`
Expected: build FAILS — `No overload for method 'MergeZip' takes 3 arguments`, `'PdfMerge' does not contain a definition for 'MergeFiles'`.

- [ ] **Step 4: Write `PdfMerge.cs`**

Replace the entire content of `src/OrdoSort.Core/PdfMerge.cs` with:

```csharp
using ICSharpCode.SharpZipLib.Zip;
using PdfSharp.Pdf;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace OrdoSort.Core;

/// <summary>
/// Merge PDFs into one document. Two shapes, one routine: every PDF inside
/// a zip into "&lt;zipname&gt;.pdf" saved beside the zip, or a handful of
/// loose PDFs into one file saved beside the first of them. Never throws —
/// the same discipline PageCounts.Count and Unlock.UnlockPdf use for their
/// own PdfSharp calls: every failure comes back as a MergeResult, not an
/// exception.
///
/// Passwords (2026-08-28): a locked archive, a locked loose PDF and a locked
/// PDF inside an archive all take the caller's candidate list and its
/// prompt through the same contract (<see cref="Passwords.Resolve"/>), and
/// report "needs_password" — naming the item in <see cref="MergeResult.Item"/>,
/// nothing written — when the prompt is skipped. The output is always a
/// plain, unencrypted document: Import mode copies pages into a fresh one,
/// exactly as Unlock does.
///
/// ZipSlip immunity: entry names never touch the filesystem here. A zip entry
/// with a crafted name like "../../evil.pdf" is only ever used as a content
/// SOURCE (read through <see cref="Zipper.ReadEntry"/> straight into memory)
/// and, separately, as TEXT in a message — never as a filesystem path passed
/// to File/Directory/Path APIs, which is what a ZipSlip exploit needs to
/// escape the zip's own folder. The only path this class ever writes to is
/// built from the ZIP FILE's own name (zipStem) plus ".pdf", or from the
/// first loose PDF's folder, run through <see cref="Collision.FreeFile"/> —
/// nothing an entry inside the zip controls.
///
/// Fail-whole, not partial output: one bad document (skipped at the prompt,
/// corrupt, or anything AddPage chokes on) fails the WHOLE unit — the zip,
/// or the loose group — rather than silently omitting that one PDF from the
/// merge. A merged file that quietly dropped a page range looks identical
/// to a complete one until someone notices a document is missing; a loud,
/// whole-unit failure that names the offending item is safer than a merge
/// nobody can trust without re-checking page by page.
///
/// Memory: every source PDF this class reads is buffered in memory (a zip
/// entry's own stream is forward-only, and PdfReader.Open needs random
/// access), and the buffers all stay alive until the merged document is
/// saved — so peak memory is roughly the SUM of every PDF's size in the
/// unit, not just the largest one. Acceptable for v1, the same call
/// Unlock.cs's own doc comment makes for its buffered path;
/// <see cref="Unlock.LargeFileThresholdBytes"/> is the precedent this would
/// follow if a unit's PDFs ever turn out too large to buffer whole.
/// </summary>
public static class PdfMerge
{
    /// <summary><see cref="Source"/> is the zip, or the first loose PDF in
    /// merge order. <see cref="Item"/> is the file path (MergeFiles) or the
    /// entry name (MergeZip) that stopped a merge — what lets a caller mark
    /// the right row — and null on ok / no_pdfs.</summary>
    public sealed record MergeResult(string Source, string Status, string? Output = null,
        int PdfCount = 0, int SkippedEntries = 0, string Message = "", string? Item = null);
    // Status: "ok" | "no_pdfs" | "needs_password" | "error" — never throws

    /// <summary>Merge every PDF inside <paramref name="zipPath"/>, natural-
    /// sorted by entry path, into "&lt;zipStem&gt;.pdf" saved beside the zip
    /// (collision-suffixed, never overwritten). Wrapped so nothing this
    /// method does — a missing/garbage zip file, an entry that fails to
    /// parse as a PDF, a save that fails partway — can ever throw out to
    /// the caller; every one of those becomes a readable MergeResult.</summary>
    public static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask) =>
        MergeZip(zipPath, candidates, ask, pickOutput: null);

    /// <summary>Test seam for the save-failure cleanup gate (see
    /// MergeZipCore's own comment on <c>created</c>): <paramref name="pickOutput"/>
    /// defaults to <see cref="Collision.FreeFile"/> and stands in for it, so a
    /// test can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another station claiming
    /// that exact name in the gap between the real FreeFile probe and this
    /// call's own FileMode.CreateNew, without needing real thread timing to
    /// provoke it.</summary>
    internal static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string>? pickOutput)
    {
        try
        {
            return MergeZipCore(zipPath, candidates, ask, pickOutput ?? Collision.FreeFile);
        }
        catch (Exception ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }
    }

    private static MergeResult MergeZipCore(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string> pickOutput)
    {
        var zipName = Path.GetFileName(zipPath);
        SzlZipFile zip;
        try
        {
            zip = new SzlZipFile(zipPath);
        }
        catch (ZipException ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }

        using (zip)
        {
            var entries = zip.Cast<ZipEntry>().ToList();

            // The archive's own password first, exactly as Zipper.Extract
            // settles it — before anything is read, so a skipped prompt costs
            // nothing and writes nothing.
            var archive = Zipper.UnlockArchive(zip, entries, candidates, ask, zipName);
            if (archive.Status == "needs_password")
                return new(zipPath, "needs_password", Message: "needs a password", Item: zipName);
            if (archive.Status == "unreadable")
                return new(zipPath, "error", Message: "couldn't read the zip: an encrypted entry couldn't be read");

            // Directory entries are skipped without counting. Everything
            // else that isn't a .pdf counts toward SkippedEntries so the
            // caller can tell "an empty zip" apart from "a zip full of things
            // that aren't PDFs".
            var pdfEntries = new List<ZipEntry>();
            var skipped = 0;
            foreach (var entry in entries)
            {
                if (!entry.IsFile) continue;
                if (entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) pdfEntries.Add(entry);
                else skipped++;
            }
            if (pdfEntries.Count == 0)
                return new(zipPath, "no_pdfs", SkippedEntries: skipped, Message: "no PDFs inside");

            // NaturalSort, not the zip's own entry order: "2.pdf" must merge
            // before "10.pdf" the same way this app lists any other batch of
            // files, and a zip's central directory carries no ordering
            // guarantee beyond "however the tool that built it happened to
            // write entries".
            pdfEntries.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));

            using var output = new PdfDocument();
            var openDocs = new List<IDisposable>();
            try
            {
                foreach (var entry in pdfEntries)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = Zipper.ReadEntry(zip, entry);
                    }
                    catch (Exception ex)
                    {
                        return new(zipPath, "error", Message: $"couldn't read '{entry.Name}': {ex.Message}", Item: entry.Name);
                    }
                    var stopped = AddPdf(bytes, entry.Name, zipName, entry.Name, candidates, ask, output, openDocs);
                    if (stopped is not null) return stopped with { Source = zipPath };
                }

                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
                var zipStem = Path.GetFileNameWithoutExtension(zipPath);
                var target = pickOutput(Path.Combine(zipDir, zipStem + ".pdf"));
                return SaveNew(output, target, zipPath, pdfEntries.Count, skipped);
            }
            finally
            {
                foreach (var d in openDocs) d.Dispose();
            }
        }
    }

    /// <summary>Merge <paramref name="pdfPaths"/> — natural-sorted by file
    /// name, ties by full path — into one document. With
    /// <paramref name="outputPath"/> null the result is named by
    /// <see cref="DefaultName"/> and placed beside the first document in that
    /// order, collision-suffixed; a non-null path is a Save-As answer and is
    /// replaced through <see cref="AtomicPlace.TryReplace"/>, the way
    /// Zipper.CreateZip places a Save-As archive — built to a GUID-named temp
    /// sibling, moved into place only once complete, so a merge that fails
    /// part-way leaves whatever was at that name untouched.</summary>
    public static MergeResult MergeFiles(IReadOnlyList<string> pdfPaths, string? outputPath,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var ordered = InMergeOrder(pdfPaths);
        if (ordered.Count == 0) return new("", "error", Message: "nothing to merge");
        try
        {
            return MergeFilesCore(ordered, outputPath, candidates, ask);
        }
        catch (Exception ex)
        {
            return new(ordered[0], "error", Message: $"couldn't merge: {ex.Message}");
        }
    }

    private static MergeResult MergeFilesCore(IReadOnlyList<string> ordered, string? outputPath,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var source = ordered[0];
        using var output = new PdfDocument();
        var openDocs = new List<IDisposable>();
        try
        {
            foreach (var path in ordered)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException ex) when (Unlock.IsInUse(ex))
                {
                    return new(source, "error", Item: path,
                        Message: "It's open in another program — close it there and merge again.");
                }
                catch (Exception ex)
                {
                    return new(source, "error", Item: path, Message: $"couldn't read it: {ex.Message}");
                }
                var stopped = AddPdf(bytes, Path.GetFileName(path), null, path, candidates, ask, output, openDocs);
                if (stopped is not null) return stopped with { Source = source };
            }

            if (outputPath is not null)
            {
                if (!AtomicPlace.TryReplace(outputPath, tmp => output.Save(tmp), out var placeError))
                    return new(source, "error", Message: $"couldn't save the merged PDF: {placeError}");
                return new(source, "ok", Output: outputPath, PdfCount: ordered.Count);
            }

            var target = Collision.FreeFile(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, DefaultName(ordered)));
            return SaveNew(output, target, source, ordered.Count, 0);
        }
        finally
        {
            foreach (var d in openDocs) d.Dispose();
        }
    }

    /// <summary>The default name for a loose merge — just the file name, so
    /// it doubles as the Save-As dialog's suggested name: the folder
    /// CONTAINING the first document in merge order ("C:\Jobs\Job 4471\cover.pdf"
    /// → "Job 4471.pdf"), the same rule <see cref="Zipper.DefaultName"/>
    /// applies to a zip so the two windows guess alike. "Merged.pdf" when
    /// that folder has no name (a drive root) or there is nothing to merge.</summary>
    public static string DefaultName(IReadOnlyList<string> pdfPaths)
    {
        var ordered = InMergeOrder(pdfPaths);
        if (ordered.Count == 0) return "Merged.pdf";
        var parentName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(ordered[0])) ?? "");
        return parentName.Length == 0 ? "Merged.pdf" : parentName + ".pdf";
    }

    /// <summary>Natural sort by file name — "2.pdf" before "10.pdf", the way
    /// every list in this app sorts — with two same-named files in different
    /// folders falling back to full-path order so the result is deterministic.</summary>
    private static List<string> InMergeOrder(IReadOnlyList<string> pdfPaths) =>
        pdfPaths
            .OrderBy(p => Path.GetFileName(p), NaturalSort.Instance)
            .ThenBy(p => p, NaturalSort.Instance)
            .ToList();

    /// <summary>The one routine both merges share: open <paramref name="bytes"/>
    /// with the passwords the caller knows (and the prompt, if it comes to
    /// that), then add every page to <paramref name="output"/>. Returns null
    /// when the pages went in; otherwise the failure to report, with
    /// <see cref="MergeResult.Source"/> left blank for the caller to fill and
    /// <see cref="MergeResult.Item"/> set to <paramref name="itemKey"/> — the
    /// full path of a loose file, the entry name inside a zip. Every source
    /// document opened here — and the MemoryStream backing it — has to stay
    /// alive until output.Save() runs, not just through its own AddPage
    /// loop: PdfSharp's Import-mode AddPage does not fully materialise a
    /// page's content at call time, it keeps resolving objects from the
    /// SOURCE document lazily, up to Save. That is why both go into
    /// <paramref name="openDocs"/> and are disposed together at the end.</summary>
    private static MergeResult? AddPdf(byte[] bytes, string displayName, string? inside, string itemKey,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask,
        PdfDocument output, List<IDisposable> openDocs)
    {
        var opened = PdfPasswords.Open(bytes, candidates, ask, displayName, inside);
        switch (opened.Status)
        {
            case "needs_password":
                return new("", "needs_password", Item: itemKey,
                    Message: inside is null ? "needs a password" : $"'{displayName}' inside needs a password");
            case "unreadable":
                return new("", "error", Item: itemKey,
                    Message: inside is null
                        ? $"couldn't read it: {opened.Message}"
                        : $"couldn't read '{displayName}': {opened.Message}");
        }

        openDocs.Add(opened.Document!);
        openDocs.Add(opened.Stream!);
        try
        {
            foreach (var page in opened.Document!.Pages) output.AddPage(page);
        }
        catch (Exception ex)
        {
            return new("", "error", Item: itemKey,
                Message: inside is null
                    ? $"couldn't read it: {ex.Message}"
                    : $"couldn't read '{displayName}': {ex.Message}");
        }
        return null;
    }

    /// <summary>Exclusive-create save behind the created-by-me gate.
    /// <c>created</c> is set ONLY once FileMode.CreateNew has actually
    /// succeeded — mirroring Unlock.PlaceAndSwap's own markCreated gate
    /// (2026-08 audit finding 1.2). Collision.FreeFile only proves the name
    /// was free AT CHECK TIME: another process can create that exact file in
    /// the gap before this line runs, in which case the FileStream ctor
    /// itself throws and `created` is never set — so the catch below must
    /// NOT call RemoveQuietly in that case, or it deletes a file this call
    /// never wrote a single byte of. RemoveQuietly only ever runs against a
    /// target THIS call is certain it created.</summary>
    private static MergeResult SaveNew(PdfDocument output, string target, string source, int pdfCount, int skipped)
    {
        var created = false;
        try
        {
            using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            created = true;
            output.Save(fs, closeStream: false);
        }
        catch (Exception ex)
        {
            if (created) RemoveQuietly(target);
            return new(source, "error", Message: $"couldn't save the merged PDF: {ex.Message}");
        }
        return new(source, "ok", Output: target, PdfCount: pdfCount, SkippedEntries: skipped);
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
```

- [ ] **Step 5: Keep the view model compiling**

In `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`, the field and default become:

```csharp
    private readonly Func<string, PdfMerge.MergeResult> _merger;
```

```csharp
        // No passwords yet — Task 7 threads the window's candidates and its
        // prompt through here; until then a locked PDF reports needs_password.
        _merger = merger ?? (path => PdfMerge.MergeZip(path, Array.Empty<string>(), null));
```

and the constructor parameter type is `Func<string, PdfMerge.MergeResult>? merger`. `ZipItemRow.Apply(PdfMerge.MergeResult)` in `ZipListViewModel.cs` needs only the type rename in this task — `"needs_password"` falls into its `_ => Error` arm until Task 6 adds the status.

In `CONTEXT.md:67`, `ZipMerge.MergeZipCore` becomes `PdfMerge.MergeZipCore`. Nothing else in that file changes — checked in the spec, not assumed.

- [ ] **Step 6: Run the Core suite, then the full check, and commit**

Run: `dotnet test tests/OrdoSort.Core.Tests -v minimal` — expected `Failed: 0`.
Run the full check — expected both `Failed: 0` (Wpf still 1895: every Wpf zip test is seam-driven and only the type name changed).

```bash
git add -A src/OrdoSort.Core/PdfMerge.cs src/OrdoSort.Core/ZipMerge.cs tests/OrdoSort.Core.Tests/PdfMergeTests.cs tests/OrdoSort.Core.Tests/ZipMergeTests.cs src/OrdoSort.Wpf/ViewModels tests/OrdoSort.Wpf.Tests tools/OrdoSort.Smoke CONTEXT.md
git commit -m "feat(merge): merge loose PDFs too, and ask for a password instead of failing

ZipMerge becomes PdfMerge, because it merges PDFs and a zip is now only
one place they come from. MergeFiles takes a handful of loose documents —
natural-sorted by name, so 2.pdf precedes 10.pdf like every list in the
app — into one file named after their folder and placed beside the first,
or wherever Merge to… sends it through AtomicPlace. MergeZip keeps its
shape and gains the same password pair every locked operation takes now.

Both run through one AddPdf routine over PdfPasswords.Open, so a locked
document — loose, inside an archive, or the archive itself — tries the
caller's candidates, asks through the callback, and on a skip reports
needs_password naming the item, with nothing written. Fail-whole is
unchanged: one unopenable document fails its whole unit. The one existing
fact whose outcome moves is the locked-entry case, from a dead-end error
to a runnable needs_password — which is the point."
```

---

### Task 5: The prompt — `IDialogService.AskPassword` and `PasswordWindow`

**Files:**
- Modify: `src/OrdoSort.Wpf/Services/IDialogService.cs`, `src/OrdoSort.Wpf/Services/DialogService.cs`
- Create: `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml`, `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml.cs`
- Modify: `tests/OrdoSort.Wpf.Tests/Fakes.cs` (`FakeDialogs`), `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs`, `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs` (registry entry)
- Test: `tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs`

**Interfaces:**
- Consumes: `PasswordRequest` (Task 1).
- Produces: `string? IDialogService.AskPassword(PasswordRequest request)` (default `null` = skip); `PasswordWindow.Ask(Window? owner, PasswordRequest request)`; `internal static PasswordWindow Build(Window? owner, PasswordRequest request)` and `internal string? Answer` for tests; `FakeDialogs.PasswordAnswers` (a `Queue<string?>`) and `FakeDialogs.PasswordRequests`; `ScriptedDialogs.QueuePassword(params string?[] answers)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The prompt a locked zip or PDF raises mid-run. Built through the
/// same internal Build seam MessageWindow exposes, so the real window is
/// constructed and shown off-screen without entering the modal loop Ask
/// would then have to escape. Escape is simulated through InputManager, the
/// way UnlockEnterKeyTests drives a keystroke: the window handles it in
/// PreviewKeyDown, which a tunnelling event from the root reaches.</summary>
[Collection(HighlightContrastTests.Name)]
public class PasswordWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public PasswordWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static PasswordWindow Show(PasswordRequest request)
    {
        var w = PasswordWindow.Build(null, request);
        w.Left = -20000; w.Top = 0; w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Show();
        w.UpdateLayout();
        OverflowProbe.PumpRender();
        w.UpdateLayout();
        return w;
    }

    [Fact]
    public void ALooseItemIsNamedOnItsOwn() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("Batch 12.zip", null, false));
        try
        {
            Assert.Equal("Batch 12.zip is password-protected.", w.MessageText.Text);
            Assert.False(w.FailedText.IsVisible);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void AnItemInsideAnArchiveSaysWhereItLivesAndAFailedTrySaysSo() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("report.pdf", "Batch 12.zip", true));
        try
        {
            Assert.Equal("report.pdf inside Batch 12.zip is password-protected.", w.MessageText.Text);
            Assert.True(w.FailedText.IsVisible);
            Assert.Equal("That password didn't open it.", w.FailedText.Text);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void OpenAnswersWithWhatWasTypedAndIsTheDefaultButton() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            Assert.True(w.OpenButton.IsDefault, "Enter must mean Open");
            w.PwBox.Password = "secret";
            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("secret", w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void OpenWithNothingTypedStaysOpenRatherThanAnsweringNothing() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(w.IsVisible);
            Assert.Null(w.Answer);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void SkipAnswersNull() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "typed but abandoned";
            w.SkipButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void EscapeIsASkipEvenWithAPasswordTyped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "typed but abandoned";
            var source = PresentationSource.FromVisual(w)!;
            InputManager.Current.ProcessInput(
                new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void ShowRevealsTheTypedPasswordAndHidingItKeepsIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "secret";
            w.ShowPw.IsChecked = true;
            w.UpdateLayout();
            Assert.True(w.PwPlain.IsVisible);
            Assert.False(w.PwBox.IsVisible);
            Assert.Equal("secret", w.PwPlain.Text);

            w.PwPlain.Text = "secret2";
            w.ShowPw.IsChecked = false;
            w.UpdateLayout();
            Assert.True(w.PwBox.IsVisible);
            Assert.Equal("secret2", w.PwBox.Password);

            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("secret2", w.Answer);
        }
        finally { if (w.IsVisible) w.Close(); }
    });
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PasswordWindowTests" -v minimal`
Expected: build FAILS with `The type or namespace name 'PasswordWindow' could not be found`.

- [ ] **Step 3: Add `AskPassword` to the dialog service**

In `src/OrdoSort.Wpf/Services/IDialogService.cs`, add `using OrdoSort.Core;` at the top and this member after `BrowseFolder`:

```csharp
    /// <summary>Ask for one locked item's password, mid-run. Null is a skip:
    /// that item is reported as needing a password and the batch moves on.
    /// Defaulted rather than abstract for the same reason
    /// <see cref="AskOpenFiles"/> is: fourteen classes implement this
    /// interface and only the real one shows a window, so the fakes,
    /// recorders and scripted stubs inherit "skip" instead of each carrying a
    /// throwaway override — and a scenario that never expected a prompt
    /// fails on the needs_password row it produces, not on a missing
    /// method.</summary>
    string? AskPassword(PasswordRequest request) => null;
```

In `src/OrdoSort.Wpf/Services/DialogService.cs`, add `using OrdoSort.Core;` and, after `BrowseFolder`:

```csharp
    public string? AskPassword(PasswordRequest request) => PasswordWindow.Ask(_owner, request);
```

- [ ] **Step 4: Create `PasswordWindow.xaml`**

```xml
<Window x:Class="OrdoSort.Wpf.Windows.PasswordWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="OrdoSort — Password needed"
        SizeToContent="WidthAndHeight" ResizeMode="NoResize"
        MinWidth="380" MaxWidth="520"
        ShowInTaskbar="False"
        WindowStartupLocation="CenterOwner"
        Style="{StaticResource {x:Type Window}}">
    <!-- The prompt a locked zip or PDF raises mid-run. MessageWindow's shape
         on purpose: a plain Window, so it inherits the implicit Window style
         and TitleBar's dark caption for free, and the glyph is a token
         ThemeTests already enforces against Theme.WindowBg.

         No button carries IsCancel: Escape is handled once, at the window,
         and always means Skip (see MessageWindow's own comment on why a
         button with both IsCancel and a Click handler is a race). Skip is
         the negative answer here and the safe one — nothing happens to the
         item — but Open is the default, because a prompt that exists to be
         answered should answer on Enter. -->
    <DockPanel Margin="18,16,18,14">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,18,0,0">
            <!-- A styled TextBlock label, never a plain string: see
                 MessageWindow.ConfigureAsStatement for the contrast trap a
                 plain-string Content on PrimaryButton falls into. -->
            <Button x:Name="OpenButton" Click="OnOpen" MinWidth="96" Margin="0,0,8,0" IsDefault="True"
                    Style="{StaticResource PrimaryButton}" AutomationProperties.Name="Open">
                <TextBlock Text="Open" Style="{StaticResource PrimaryButtonLabel}" />
            </Button>
            <Button x:Name="SkipButton" Content="Skip this one" Click="OnSkip" MinWidth="110" />
        </StackPanel>

        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <TextBlock x:Name="Glyph" Grid.RowSpan="3" Style="{StaticResource Icon}" FontSize="22"
                       Text="&#xE72E;" VerticalAlignment="Top" Margin="0,1,14,0" />

            <!-- MaxWidth, not a fixed Width: SizeToContent lets a short name
                 make a small dialog, and this stops a long path growing a
                 single unreadable line across the monitor. -->
            <TextBlock x:Name="MessageText" Grid.Column="1" TextWrapping="Wrap" MaxWidth="420" />

            <!-- Shown only after a typed password failed. Amber: "needs
                 attention", not an error (status-colour-vocabulary plan,
                 2026-08-08). -->
            <TextBlock x:Name="FailedText" Grid.Column="1" Grid.Row="1" TextWrapping="Wrap" MaxWidth="420"
                       Margin="0,6,0,0" Text="That password didn't open it."
                       Foreground="{DynamicResource Theme.StatusAmber}" Visibility="Collapsed" />

            <!-- PwBox/PwPlain swap on Show, the same pair UnlockWindow keeps:
                 a PasswordBox cannot bind, so the visible TextBox is a second
                 view of the same value, and whichever is showing is the one
                 that answers. -->
            <Grid Grid.Column="1" Grid.Row="2" Margin="0,12,0,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="220" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="Password:" Style="{StaticResource FieldLabel}" />
                <PasswordBox Grid.Column="1" x:Name="PwBox" AutomationProperties.Name="Password" />
                <TextBox Grid.Column="1" x:Name="PwPlain" Visibility="Collapsed"
                         AutomationProperties.Name="Password (visible)" />
                <CheckBox Grid.Column="2" x:Name="ShowPw" Content="Show" Margin="8,0,0,0"
                          VerticalAlignment="Center" Checked="OnShowPw" Unchecked="OnShowPw" />
            </Grid>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 5: Create `PasswordWindow.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Windows;

/// <summary>The password prompt behind <see cref="Services.IDialogService.AskPassword"/>.
/// One question, two answers: a password (Open, the default button) or
/// null (Skip, and Escape). The Core operation that raised it is blocked on
/// a SynchronizationContext.Send while this is up — see
/// ZipListViewModel.AskPassword — so a closed window always answers, and
/// answers exactly once.</summary>
public partial class PasswordWindow : Window
{
    private string? _answer;

    private PasswordWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _answer = null;
            Close();
        };
        // Focus lands in the box, so the next keystroke is the password and
        // Enter is live immediately.
        Loaded += (_, _) => PwBox.Focus();
    }

    /// <summary>The answer: the typed password, or null for a skip. Internal
    /// so PasswordWindowTests can read it off a window driven by simulated
    /// clicks and keys instead of the modal loop.</summary>
    internal string? Answer => _answer;

    /// <summary>Owner-modal. Returns the password, or null when skipped.</summary>
    public static string? Ask(Window? owner, PasswordRequest request)
    {
        var w = Build(owner, request);
        w.ShowDialog();
        return w._answer;
    }

    /// <summary>Internal, not private, so tests can build the real thing and
    /// drive it without entering ShowDialog — the seam MessageWindow.Build
    /// already established.</summary>
    internal static PasswordWindow Build(Window? owner, PasswordRequest request)
    {
        var w = new PasswordWindow();
        // WPF throws if handed an owner that has never been shown.
        if (owner is { IsVisible: true }) w.Owner = owner;

        w.MessageText.Text = request.Inside is null
            ? $"{request.Item} is password-protected."
            : $"{request.Item} inside {request.Inside} is password-protected.";
        // The window's accessible name is its title, so without this a
        // screen reader announces the dialog and then has nothing to say
        // about what wants a password.
        AutomationProperties.SetName(w.MessageText, w.MessageText.Text);
        w.FailedText.Visibility = request.PreviousAttemptFailed ? Visibility.Visible : Visibility.Collapsed;
        // SetResourceReference, not a one-off brush, so the glyph follows a
        // live theme switch like everything else; AccentBronze is a pairing
        // ThemeTests already enforces against Theme.WindowBg.
        w.Glyph.SetResourceReference(ForegroundProperty, "Theme.AccentBronze");
        return w;
    }

    private string Typed => ShowPw.IsChecked == true ? PwPlain.Text : PwBox.Password;

    /// <summary>Nothing typed is not an answer: the window stays, rather than
    /// "opening" with an empty password Core would only reject and re-ask.</summary>
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var typed = Typed;
        if (typed.Length == 0) return;
        _answer = typed;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _answer = null;
        Close();
    }

    private void OnShowPw(object sender, RoutedEventArgs e)
    {
        var show = ShowPw.IsChecked == true;
        if (show) PwPlain.Text = PwBox.Password;
        else PwBox.Password = PwPlain.Text;
        PwPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PwBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        // Keyboard.Focus on a Collapsed element silently lands nowhere, so
        // focus follows whichever box is actually showing.
        if (show) PwPlain.Focus();
        else PwBox.Focus();
    }
}
```

- [ ] **Step 6: Teach the fakes**

In `tests/OrdoSort.Wpf.Tests/Fakes.cs`, add `using OrdoSort.Core;` and, inside `FakeDialogs`:

```csharp
    /// <summary>Scripted prompt answers, one per AskPassword call; an empty
    /// queue answers null — the person skipped — so a test that never
    /// expected a prompt sees a needs_password row rather than a hang.
    /// Every request is recorded, so a test can assert on what was asked
    /// and how often, not just on what came back.</summary>
    public Queue<string?> PasswordAnswers { get; } = new();
    public List<PasswordRequest> PasswordRequests { get; } = new();

    public string? AskPassword(PasswordRequest request)
    {
        PasswordRequests.Add(request);
        return PasswordAnswers.Count > 0 ? PasswordAnswers.Dequeue() : null;
    }
```

In `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs`, add `using OrdoSort.Core;`, a queue, its scripting method, the implementation, and its `Unconsumed` line:

```csharp
    private readonly Queue<string?> _password = new();
```

```csharp
    public ScriptedDialogs QueuePassword(params string?[] answers) { foreach (var a in answers) _password.Enqueue(a); return this; }
```

```csharp
    // An empty password queue answers null — the person skipped — so a
    // scenario that never expected a prompt fails on the row it produces.
    public string? AskPassword(PasswordRequest request) => _password.Count > 0 ? _password.Dequeue() : null;
```

and in `Unconsumed`, after the `BrowseFolder` line:

```csharp
            if (_password.Count > 0) left.Add($"AskPassword ({_password.Count})");
```

- [ ] **Step 7: Register the window for the overflow and accessible-name suites**

In `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs`, add `using OrdoSort.Core;` if absent, and this entry to `Registry()` directly after the `["MatchMergeWindow"]` entry (alphabetical neighbours are `MatchMerge` and `PageCounts`):

```csharp
        // SizeToContent: heights are 0 so the probe leaves Height alone and
        // only drives Width between MinWidth and MaxWidth. A long item name
        // inside a long archive name, with the failed line showing, is the
        // widest this window gets.
        ["PasswordWindow"] = new(380, 520, 0, 0, () => (PasswordWindow.Build(null,
            new PasswordRequest("a-long-enough-document-name-to-matter.pdf",
                "a-long-enough-archive-name-to-matter.zip", true)), null), MinExamined: 9999),
```

Then measure `MinExamined`: run `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~WindowOverflowTests" -v minimal`, read `the probe examined N elements` out of the PasswordWindow failure, and set `MinExamined` to three quarters of N rounded up, with `// N measured` beside it — the registry's own rule.

- [ ] **Step 8: Run the window suites to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PasswordWindowTests|FullyQualifiedName~WindowOverflowTests|FullyQualifiedName~AccessibleNameTests" -v minimal`
Expected: `Failed: 0`. If `AccessibleNameTests` names a nameless control on PasswordWindow, it is the `CheckBox` or a box missing its `AutomationProperties.Name` — fix the XAML, not the test.

- [ ] **Step 9: Run the full check and commit**

```bash
git add src/OrdoSort.Wpf/Services/IDialogService.cs src/OrdoSort.Wpf/Services/DialogService.cs src/OrdoSort.Wpf/Windows/PasswordWindow.xaml src/OrdoSort.Wpf/Windows/PasswordWindow.xaml.cs tests/OrdoSort.Wpf.Tests/Fakes.cs tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs
git commit -m "feat(dialogs): the password prompt, in the message window's shape

IDialogService.AskPassword answers a PasswordRequest with a password or
null, defaulted to null in the interface so the fourteen fakes, recorders
and scripted stubs inherit a skip instead of a throwaway override. The
real one is PasswordWindow: a plain WPF window (so the theme and the dark
caption reach it, unlike a Win32 box), Open as the default button, Escape
handled once at the window and always meaning Skip, and the same
PasswordBox/TextBox pair UnlockWindow uses behind its Show checkbox.
Nothing typed is not an answer — the window stays rather than answering
with an empty password Core would only reject."
```

---

### Task 6: `ZipListViewModel` runs units, owns passwords, probes on add — and `ZipExtractViewModel` uses all three

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` (rewrite)
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` (rewrite)
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs:20-25` (constructor body only)
- Modify: `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` (base constructor call only — Task 7 rewrites the rest)
- Test: `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`, `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs`, `tests/OrdoSort.Wpf.Tests/ZipListClearAndRemoveTests.cs`, `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs` (one constructor call)

**Interfaces:**
- Consumes: `Zipper.Extract(path, candidates, ask)`, `Zipper.Probe(path, candidates)`, `Zipper.ZipProbeResult` (Task 2); `PdfMerge.MergeResult` (Task 4); `IDialogService.AskPassword`, `FakeDialogs.PasswordAnswers` (Task 5).
- Produces:
  - `ZipItemRowStatus.NeedsPassword`; `ZipItemRow.IsRunnable`, `ZipItemRow.IsPdf`, `ZipItemRow.KindOf` → `"pdf"`; `internal void ZipItemRow.Mark(ZipItemRowStatus status, string note)`.
  - `ZipListViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords, IWorkScheduler? scheduler, SynchronizationContext? uiContext)`; `protected IReadOnlyList<string> Candidates()`; `protected string? AskPassword(PasswordRequest request)`; `protected abstract (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords)`; `protected static FromZipProbe(Zipper.ZipProbeResult)` and `FromPdfProbe(Unlock.ProbeResult)`; `protected sealed record Unit<TResult>(IReadOnlyList<ZipItemRow> Rows, Func<IReadOnlyList<string>, TResult> Operation)`; `protected sealed record TallyClause(string Status, string Label, string? Plural = null)`; `RunBatchAsync<TResult>(IReadOnlyList<Unit<TResult>> units, Func<TResult, string> statusOf, Action<IReadOnlyList<ZipItemRow>, TResult> apply, string progressVerb, IReadOnlyList<TallyClause> clauses)`.
  - `ZipExtractViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords, IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null, Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult>? extractor = null, Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null)`.

- [ ] **Step 1: Update `ZipItemRowTests` and write the new row facts**

In `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`, the `KindOf` theory's first case becomes `[InlineData(@"C:\in\a.pdf", "pdf")]`, and add `[InlineData(@"C:\in\a.PDF", "pdf")]` and `[InlineData(@"C:\in\a.txt", "file")]`. Then add at the end of the class:

```csharp
    [Fact]
    public void ApplyingANeedsPasswordExtractLeavesTheRowRunnable()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "needs_password", null, "needs a password"));
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("needs a password", row.Note);
        Assert.True(row.IsRunnable);
        Assert.Null(row.Output);
    }

    [Fact]
    public void ApplyingANeedsPasswordMergeKeepsTheMessageThatNamesTheEntry()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(@"C:\in\a.zip", "needs_password",
            Message: "'report.pdf' inside needs a password", Item: "report.pdf"));
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("'report.pdf' inside needs a password", row.Note);
        Assert.True(row.IsRunnable);
    }

    [Theory]
    [InlineData(ZipItemRowStatus.Pending, true)]
    [InlineData(ZipItemRowStatus.NeedsPassword, true)]
    [InlineData(ZipItemRowStatus.Ok, false)]
    [InlineData(ZipItemRowStatus.NoPdfs, false)]
    [InlineData(ZipItemRowStatus.Error, false)]
    public void OnlyPendingAndNeedsPasswordAreRunnable(ZipItemRowStatus status, bool runnable)
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Mark(status, "");
        Assert.Equal(runnable, row.IsRunnable);
    }

    /// <summary>A probe's verdict, or "not merged — x needs a password" on a
    /// row a culprit held back: status and note only, never Output.</summary>
    [Fact]
    public void MarkSetsStatusAndNoteWithoutTouchingOutput()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "ok", @"C:\in\a"));
        row.Mark(ZipItemRowStatus.Pending, "a saved password opens this");
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);
        Assert.Equal("a saved password opens this", row.Note);
        Assert.Equal(@"C:\in\a", row.Output);
    }
```

- [ ] **Step 2: Update `ZipExtractViewModelTests` and write the new view-model facts**

In `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs`:

Add `using ICSharpCode.SharpZipLib.Zip;` and change `using System.IO.Compression;` to `using ZipFile = System.IO.Compression.ZipFile;` plus `using System.IO.Compression;` kept for `ZipArchiveMode` (the two `ZipFile` types collide otherwise).

Replace `MakeVm` with:

```csharp
    /// <summary>The probe defaults to "not encrypted" so every fact that is
    /// not ABOUT probing keeps its rows Pending — the real Zipper.Probe on a
    /// TempDir's one-byte "x" files would report every one of them
    /// unreadable and leave nothing runnable.</summary>
    private static ZipExtractViewModel MakeVm(
        IDialogService? dialogs = null,
        IReadOnlyList<string>? savedPasswords = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult>? extractor = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        SynchronizationContext? uiContext = null) =>
        new(dialogs ?? new FakeDialogs(), savedPasswords ?? Array.Empty<string>(), new InlineWorkScheduler(), uiContext,
            zipper, extractor, zipProbe ?? ((path, _) => new Zipper.ZipProbeResult(path, "not_encrypted")));
```

Change every scripted extractor from the one-parameter shape `extractor: path => …` to `extractor: (path, _, _) => …` (twelve facts use one). In `TheExtractLabelIsRightWhenItAnnouncesItselfEvenWhenApplyIsMarshalled`, replace the direct constructor call with `MakeVm(uiContext: ctx, extractor: (p, _, _) => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")))`.

Add these facts before the `QueueingContext` class:

```csharp
    // ---- passwords ---------------------------------------------------

    /// <summary>A row that needed a password is not finished: the next run
    /// asks again. No remove-and-re-add.</summary>
    [Fact]
    public async Task ANeedsPasswordRowIsRunAgainByTheNextExtract()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var calls = 0;
        var vm = MakeVm(extractor: (p, _, _) => ++calls == 1
            ? new Zipper.UnzipResult(p, "needs_password", null, "needs a password")
            : new Zipper.UnzipResult(p, "ok", p + ".out"));
        await vm.AddPaths(new[] { zip });

        await vm.ExtractAsync();
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);
        Assert.True(vm.ExtractCommand.CanExecute(null));
        Assert.Equal("1 needs a password", vm.Status);

        await vm.ExtractAsync();
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TheTallyPluralisesNeedsAPassword()
    {
        using var dir = new TempDir();
        var vm = MakeVm(extractor: (p, _, _) => new Zipper.UnzipResult(p, "needs_password", null, "needs a password"));
        await vm.AddPaths(new[] { dir.File("a.zip"), dir.File("b.zip") });
        await vm.ExtractAsync();
        Assert.Equal("2 need a password", vm.Status);
    }

    /// <summary>The order the extractor sees: what was typed in this window,
    /// most recent first, then the saved list. Typed once, remembered for the
    /// next item — the prompt is reached once, not twice.</summary>
    [Fact]
    public async Task ATypedPasswordIsTriedBeforeTheSavedOnesOnTheNextItemWithoutASecondPrompt()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var dialogs = new FakeDialogs();
        dialogs.PasswordAnswers.Enqueue("typed");
        var seen = new List<IReadOnlyList<string>>();
        var vm = MakeVm(dialogs: dialogs, savedPasswords: new[] { "saved" }, extractor: (p, candidates, ask) =>
        {
            seen.Add(candidates.ToList());
            if (candidates.Contains("typed")) return new Zipper.UnzipResult(p, "ok", p + ".out");
            var answer = ask!(new PasswordRequest(Path.GetFileName(p), null, false));
            return answer == "typed"
                ? new Zipper.UnzipResult(p, "ok", p + ".out")
                : new Zipper.UnzipResult(p, "needs_password", null, "needs a password");
        });

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();

        Assert.Equal(new[] { "saved" }, seen[0]);
        Assert.Equal(new[] { "typed", "saved" }, seen[1]);
        Assert.Single(dialogs.PasswordRequests);
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind));
    }

    [Fact]
    public async Task ASkippedPromptLeavesTheRowNeedingAPasswordAndNothingElseIsTouched()
    {
        using var dir = new TempDir();
        var locked = dir.File("locked.zip");
        var plain = dir.File("plain.zip");
        var dialogs = new FakeDialogs();   // empty queue: every prompt is skipped
        var vm = MakeVm(dialogs: dialogs, extractor: (p, _, ask) => p == locked
            ? (ask!(new PasswordRequest("locked.zip", null, false)) is null
                ? new Zipper.UnzipResult(p, "needs_password", null, "needs a password")
                : new Zipper.UnzipResult(p, "ok", p + ".out"))
            : new Zipper.UnzipResult(p, "ok", p + ".out"));

        await vm.AddPaths(new[] { locked, plain });
        await vm.ExtractAsync();

        Assert.Equal(ZipItemRowStatus.NeedsPassword, vm.Rows.Single(r => r.Path == locked).StatusKind);
        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single(r => r.Path == plain).StatusKind);
        Assert.Equal("1 extracted · 1 needs a password", vm.Status);
    }

    /// <summary>A SynchronizationContext that runs what it is handed inline
    /// but counts HOW it was handed: the prompt must cross to the UI thread
    /// with Send — the worker waits on the person — never Post, and never
    /// directly. The 2026-08-19 merge shipped a marshalling gap every test
    /// hid by passing uiContext: null; this pin exists so that cannot happen
    /// to the prompt.</summary>
    private sealed class SendRecordingContext : SynchronizationContext
    {
        public int Sends { get; private set; }
        public int Posts { get; private set; }
        public override void Send(SendOrPostCallback d, object? state) { Sends++; d(state); }
        public override void Post(SendOrPostCallback d, object? state) { Posts++; d(state); }
    }

    [Fact]
    public async Task ThePromptIsMarshalledSynchronouslyOntoTheUiContext()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var ctx = new SendRecordingContext();
        var dialogs = new FakeDialogs();
        dialogs.PasswordAnswers.Enqueue("typed");
        var vm = MakeVm(dialogs: dialogs, uiContext: ctx, extractor: (p, _, ask) =>
            ask!(new PasswordRequest("a.zip", null, false)) is null
                ? new Zipper.UnzipResult(p, "needs_password", null, "needs a password")
                : new Zipper.UnzipResult(p, "ok", p + ".out"));
        await vm.AddPaths(new[] { zip });

        await vm.ExtractAsync();

        Assert.Equal(1, ctx.Sends);
        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single().StatusKind);
    }

    // ---- the probe on add --------------------------------------------

    [Theory]
    [InlineData("not_encrypted", ZipItemRowStatus.Pending, "")]
    [InlineData("ready", ZipItemRowStatus.Pending, "a saved password opens this")]
    [InlineData("needs_password", ZipItemRowStatus.NeedsPassword, "needs a password")]
    [InlineData("unreadable", ZipItemRowStatus.Error, "not a valid zip")]
    public async Task TheProbeVerdictLandsOnTheRowAsItIsAdded(string verdict, ZipItemRowStatus expected, string note)
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var vm = MakeVm(zipProbe: (p, _) => new Zipper.ZipProbeResult(p, verdict, verdict == "ready" ? 0 : null, "not a valid zip"));

        await vm.AddPaths(new[] { zip });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(expected, row.StatusKind);
        Assert.Equal(note, row.Note);
    }

    /// <summary>The probe gets the SAVED passwords only — never the typed
    /// ones — so "a saved password opens this" is exactly true, the same
    /// discipline Unlock's probe keeps (risk 2 in its own doc comment).</summary>
    [Fact]
    public async Task OnlyZipRowsAreProbedAndOnlyWithTheSavedPasswords()
    {
        using var dir = new TempDir();
        var txt = dir.File("notes.txt");
        var zip = dir.File("a.zip");
        var probed = new List<(string Path, IReadOnlyList<string> Saved)>();
        var vm = MakeVm(savedPasswords: new[] { "saved" }, zipProbe: (p, saved) =>
        {
            probed.Add((p, saved.ToList()));
            return new Zipper.ZipProbeResult(p, "not_encrypted");
        });

        await vm.AddPaths(new[] { txt, zip });

        var one = Assert.Single(probed);
        Assert.Equal(zip, one.Path);
        Assert.Equal(new[] { "saved" }, one.Saved);
    }

    [Fact]
    public async Task ClearWhileAProbeIsInFlightDropsItsVerdict()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var scheduler = new ControlledWorkScheduler();
        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), scheduler, uiContext: null,
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "needs_password"));

        var adding = vm.AddPaths(new[] { zip });
        scheduler.ReleaseNext();   // the intake check: the row lands, its probe is queued
        Assert.Single(vm.Rows);

        vm.ClearCommand.Execute(null);
        scheduler.ReleaseAll();    // the probe answers into a list that no longer holds the row
        await adding;

        Assert.Empty(vm.Rows);
    }

    /// <summary>The real probe on a real locked archive — the whole
    /// difference between this file's scripted probes and the feature.</summary>
    [Fact]
    public async Task TheRealProbeMarksARealLockedZipAsNeedingAPassword()
    {
        using var dir = new TempDir();
        var zipPath = Path.Combine(dir.Path, "locked.zip");
        using (var fs = File.Create(zipPath))
        using (var zos = new ZipOutputStream(fs) { Password = "secret" })
        {
            var bytes = "hello"u8.ToArray();
            zos.PutNextEntry(new ZipEntry("a.txt") { Size = bytes.Length, AESKeySize = 256 });
            zos.Write(bytes, 0, bytes.Length);
            zos.CloseEntry();
        }
        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler());

        await vm.AddPaths(new[] { zipPath });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);
    }
```

In `tests/OrdoSort.Wpf.Tests/ZipListClearAndRemoveTests.cs`, replace `MakeVm` with:

```csharp
    private static ZipExtractViewModel MakeVm(Func<string, Zipper.UnzipResult>? extractor = null) =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            extractor: extractor is null ? null : (p, _, _) => extractor(p),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));
```

In `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`, in `ItsListIsIndependentOfTheZipAndUnzipTab`, the `ZipExtractViewModel` construction becomes:

```csharp
        var zipExtract = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            extractor: (p, _, _) => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));
```

- [ ] **Step 3: Run the suites to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipItemRowTests|FullyQualifiedName~ZipExtractViewModelTests|FullyQualifiedName~ZipListClearAndRemoveTests" -v minimal`
Expected: build FAILS — `'ZipItemRowStatus' does not contain a definition for 'NeedsPassword'`, no 7-argument `ZipExtractViewModel` constructor, no `IsRunnable`.

- [ ] **Step 4: Rewrite `ZipListViewModel.cs`**

Replace the entire file with:

```csharp
using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a row's operation ended in. NoPdfs is reachable only from a
/// merge. NeedsPassword is reachable from any operation that met a lock
/// nobody could open — and, unlike the other three finished states, it is
/// RUNNABLE again: the next run asks again, so a skipped prompt never needs
/// a remove-and-re-add (see <see cref="ZipItemRow.IsRunnable"/>).</summary>
public enum ZipItemRowStatus { Pending, Ok, NoPdfs, Error, NeedsPassword }

/// <summary>One listed source: a loose file, a PDF, a whole folder, or an
/// archive. Kind is a plain string tag rather than an enum for the same
/// reason PathRow's was — nothing switches on it but its own grid column,
/// <see cref="IsZip"/> and <see cref="IsPdf"/>.</summary>
public sealed class ZipItemRow : ObservableObject
{
    public string Path { get; }
    public string Kind { get; }

    /// <summary>Drives which actions a window can offer for this row.</summary>
    public bool IsZip => Kind == "zip";
    public bool IsPdf => Kind == "pdf";

    /// <summary>The file name for a file or archive row; the folder's OWN
    /// name for a folder row — DirectoryInfo.Name handles a trailing
    /// separator correctly where a bare Path.GetFileName returns "".</summary>
    public string Display => Kind == "folder"
        ? new DirectoryInfo(Path).Name
        : System.IO.Path.GetFileName(Path);

    public ZipItemRow(string path, string kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Classifies a path the one way both windows agree on. Checked
    /// in this order deliberately: a directory named "x.zip" is a folder.</summary>
    public static string KindOf(string path)
    {
        if (Directory.Exists(path)) return "folder";
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return "zip";
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        return "file";
    }

    private ZipItemRowStatus _statusKind = ZipItemRowStatus.Pending;
    public ZipItemRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>A row the next run will pick up. Pending has never run;
    /// NeedsPassword ran and was skipped at the prompt — and a password is
    /// something that can be known now that wasn't then.</summary>
    public bool IsRunnable => StatusKind is ZipItemRowStatus.Pending or ZipItemRowStatus.NeedsPassword;

    /// <summary>"" while Pending with nothing to say; a probe's readiness
    /// note while still Pending; the operation's own message on a failure; a
    /// short result line on success.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _output;
    public string? Output { get => _output; private set => Set(ref _output, value); }

    internal void Apply(Zipper.UnzipResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "needs_password" => ZipItemRowStatus.NeedsPassword,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.OutputFolder;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.OutputFolder!)}"
            : result.Message;
    }

    internal void Apply(PdfMerge.MergeResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "no_pdfs" => ZipItemRowStatus.NoPdfs,
            "needs_password" => ZipItemRowStatus.NeedsPassword,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.Output;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.Output!)} ({result.PdfCount} PDF{(result.PdfCount == 1 ? "" : "s")})"
            : result.Message;
    }

    /// <summary>A verdict that is not an operation's result: a probe's
    /// readiness note on a row still Pending, or "not merged — x needs a
    /// password" on the rows a culprit held back. Status and note only;
    /// Output is left exactly as it is.</summary>
    internal void Mark(ZipItemRowStatus status, string note)
    {
        StatusKind = status;
        Note = note;
    }
}

/// <summary>Everything the two zip-tool windows share: the list, intake and
/// its dedupe, selection removal, Clear, the add note, the status line, the
/// passwords, the probe on add, and the sequential cancellable batch runner.
/// Each window owns its OWN instance, so nothing here is shared state
/// between them.
///
/// The runner works in UNITS: one Core call and the rows it answers for. A
/// zip row is a unit of one; the loose PDFs in the Merge window are one
/// unit of many, because they become one document. Sequential rather than
/// parallel, and cancelled BETWEEN units rather than mid-unit: each
/// operation writes a folder or a document, so running several at once buys
/// contention rather than speed, and a half-written output is worse than a
/// late one.
///
/// Passwords: Core tries the candidates this class hands it — what was
/// typed in this window, most recent first, then the Unlock tool's saved
/// list — and asks through <see cref="AskPassword"/> for anything none of
/// them opens. The prompt crosses to the UI thread with
/// SynchronizationContext.Send: the worker WAITS on the person, which is
/// what "the operation pauses" means. Nothing typed here is ever saved.
///
/// The probe on add: each new row is checked off-thread (four at a time,
/// Unlock's own figure — a probe is a real read, often over a share) against
/// the SAVED passwords only, so "a saved password opens this" is exactly
/// true, and its verdict lands in the Result column while the row is still
/// pending. A probe token replaced on Clear and cancelled on close keeps a
/// verdict from landing on a row nobody can see.</summary>
public abstract class ZipListViewModel : ObservableObject
{
    protected readonly IWorkScheduler Scheduler;
    protected readonly SynchronizationContext? UiContext;
    protected readonly IDialogService Dialogs;

    private readonly IReadOnlyList<string> _savedPasswords;

    /// <summary>What was typed at the prompt in this window, most recent
    /// first, kept for the window's lifetime — a second run never re-asks
    /// for a password the first one learned. Touched only on the UI thread,
    /// inside <see cref="AskPassword"/>'s Send callback, and read only on the
    /// UI thread, in <see cref="Candidates"/> just before each unit is
    /// scheduled — so the worker never sees the live list.</summary>
    private readonly List<string> _typedPasswords = new();

    /// <summary>How many probes run at once — the same figure and the same
    /// reasoning as UnlockViewModel.MaxConcurrentUnlocks: a probe is a real
    /// read, often over a slow share, and four overlaps most of that waiting
    /// without turning the share itself into the bottleneck.</summary>
    internal const int MaxConcurrentProbes = 4;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes);

    // Replaced (not merely cancelled) on Clear and cancelled for good on
    // close — the same shape UnlockViewModel._probeCts has, for the same
    // reason: a probe must never write to a row nobody can see anymore, and
    // the NEXT add still needs a token that isn't born cancelled.
    private CancellationTokenSource _probeCts = new();

    // Cancelled for good from the window's OnClosed — a closed window must
    // not keep working invisibly — and cancelled-then-REPLACED by every
    // Clear (QC-05): a list just wiped by the user must not go on being
    // written to by whatever batch was running, but the NEXT batch still
    // needs a token that isn't born cancelled. No longer readonly for
    // exactly that swap; see ClearCommand below.
    private CancellationTokenSource _cts = new();

    public ObservableCollection<ZipItemRow> Rows { get; } = new();

    protected ZipListViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler, SynchronizationContext? uiContext)
    {
        Dialogs = dialogs;
        _savedPasswords = savedPasswords;
        Scheduler = scheduler ?? new TaskWorkScheduler();
        UiContext = uiContext;

        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            OnRowsChanged();
            // A batch running when Clear is pressed must stop instead of
            // going on to apply results to rows nobody can see anymore
            // (QC-05) — cancel the RUN token, then hand out a FRESH one so
            // the next Extract/Merge isn't born cancelled, the same swap
            // Unlock's own ClearCommand already does for its probe token.
            // RunBatchAsync's tail checks for exactly this replacement so it
            // doesn't overwrite the "" just set above with a stale partial
            // count. The probe token gets the identical swap, for the
            // identical reason.
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();

            var oldProbeCts = _probeCts;
            _probeCts = new CancellationTokenSource();
            oldProbeCts.Cancel();
            oldProbeCts.Dispose();
        });

        Rows.CollectionChanged += (_, _) => OnRowsChanged();
    }

    /// <summary>Which extensions this window accepts, in Intake's shape
    /// (dot-less, lowercase); null means anything that exists, files and
    /// folders alike.</summary>
    protected abstract ISet<string>? Extensions { get; }

    /// <summary>The noun Intake's own note builder uses — "item" where a
    /// window takes anything, "PDF or zip" where it takes those only.</summary>
    protected abstract string IntakeNoun { get; }

    /// <summary>The readiness check for one newly added row, run off the UI
    /// thread against the SAVED passwords: what the row should show while
    /// it is still pending, or null to leave it alone (a loose file in the
    /// Zip window needs nothing). Each window decides which rows it probes
    /// and with what; <see cref="FromZipProbe"/> and <see cref="FromPdfProbe"/>
    /// are the two mappings they share.</summary>
    protected abstract (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords);

    /// <summary>Raised whenever the list changes so a subclass can refresh
    /// its own button texts and command enablement.</summary>
    protected virtual void OnRowsChanged() { }

    public RelayCommand ClearCommand { get; }

    private bool _isBusy;

    /// <summary>True while RunBatchAsync (Extract or Merge, whichever
    /// subclass called it) is running. Gates Remove selected — see IsIdle —
    /// the third place this exact defect (QC-05) turned up: a row removed
    /// mid-batch would still be worked on by a loop that had already
    /// snapshotted it, then leave nothing visible to explain the result.
    /// Deliberately does NOT gate Clear: unlike Remove selected, Clear has
    /// to stay reachable during a run, since pressing it is what actually
    /// stops one (see ClearCommand above).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    /// <summary>The inverse of IsBusy — Remove selected is a Click handler
    /// with no CanExecute of its own to disable it, same shape as
    /// BulkRenameViewModel.IsIdle.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Feedback for the last AddPaths call ("2 added · 1 ignored…");
    /// blank when it added something with nothing to complain about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress during a batch, then its verdict; or a single
    /// verdict for a one-shot operation. One line per window.</summary>
    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    /// <summary>Called by drag-drop and the Add buttons. Existence checks run
    /// off-thread: a big drop from a slow share must not stall the UI thread
    /// one File.Exists at a time. Awaits the probe of whatever it added, so a
    /// caller that awaits this sees the verdicts too; production callers
    /// fire and forget it either way.</summary>
    public async Task AddPaths(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = Rows.Select(r => r.Path).ToList();
        var extensions = Extensions;

        var (offThread, kinds) = await Scheduler.Run(() =>
        {
            var taken = extensions is null
                ? Intake.Add(already, candidates, exists: p => File.Exists(p) || Directory.Exists(p))
                : Intake.Add(already, candidates, extensions, File.Exists);
            var kind = taken.Files.ToDictionary(
                p => p, ZipItemRow.KindOf, StringComparer.OrdinalIgnoreCase);
            return (taken, kind);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — otherwise a second drop landing mid-await duplicates rows.
        var settled = Intake.Add(Rows.Select(r => r.Path), offThread.Files);
        var added = new List<ZipItemRow>();
        foreach (var p in settled.Files)
        {
            var row = new ZipItemRow(p, kinds[p]);
            Rows.Add(row);
            added.Add(row);
        }

        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note(IntakeNoun);

        await ProbeRowsAsync(added, _probeCts.Token);
    }

    /// <summary>Removes exactly the rows the window's grid selection holds.
    /// The button is disabled mid-batch (IsIdle), but the guard lives here
    /// too: this is public, and dropping a row while RunBatchAsync's own
    /// snapshot still holds it would let the loop go on to apply a result
    /// to a row nobody can see anymore (QC-05).</summary>
    public void RemoveSelected(IList rows)
    {
        if (IsBusy) return;
        foreach (var item in rows.Cast<ZipItemRow>().ToList())
            Rows.Remove(item);
    }

    // ------------------------------------------------------------ passwords

    /// <summary>The order Core tries: typed in this window (most recent
    /// first), then saved. A fresh list every call, taken on the UI thread
    /// just before a unit is scheduled — the worker gets a snapshot, never
    /// the live list.</summary>
    protected IReadOnlyList<string> Candidates() => _typedPasswords.Concat(_savedPasswords).ToList();

    /// <summary>Core's <c>ask</c> callback, invoked on the worker thread from
    /// inside a running operation. Crosses to the UI thread with Send —
    /// synchronous, so the worker waits on the person and the operation
    /// genuinely pauses — shows the prompt, remembers a non-empty answer at
    /// the front of the typed list, and hands it back. Runs inline when
    /// there is no UiContext (every unit test, the E2E harness's inline
    /// scheduler). ShowDialog disables the owner, so Clear and Remove cannot
    /// fire while the prompt is up.</summary>
    protected string? AskPassword(PasswordRequest request)
    {
        string? answer = null;
        void Prompt()
        {
            answer = Dialogs.AskPassword(request);
            if (string.IsNullOrEmpty(answer)) return;
            _typedPasswords.Remove(answer);
            _typedPasswords.Insert(0, answer);
        }
        if (UiContext is null) Prompt();
        else UiContext.Send(_ => Prompt(), null);
        return answer;
    }

    // ---------------------------------------------------------------- probe

    /// <summary>The verdict a zip probe writes into a row still pending.
    /// The spec's table: not encrypted stays quiet; ready says which kind of
    /// password; needs_password is the runnable NeedsPassword state; an
    /// unreadable archive is an Error with the probe's own message.</summary>
    protected static (ZipItemRowStatus Status, string Note) FromZipProbe(Zipper.ZipProbeResult result) =>
        result.Status switch
        {
            "not_encrypted" => (ZipItemRowStatus.Pending, ""),
            "ready" => (ZipItemRowStatus.Pending, "a saved password opens this"),
            "needs_password" => (ZipItemRowStatus.NeedsPassword, "needs a password"),
            _ => (ZipItemRowStatus.Error, result.Message),
        };

    /// <summary>The same for a loose PDF, from Unlock's own probe. In use is
    /// a passing condition, not a verdict: the row stays pending with a note
    /// and the run reports whatever is true by then.</summary>
    protected static (ZipItemRowStatus Status, string Note) FromPdfProbe(Unlock.ProbeResult result) =>
        result.Status switch
        {
            "not_encrypted" => (ZipItemRowStatus.Pending, ""),
            "ready" => (ZipItemRowStatus.Pending, "a saved password opens this"),
            "needs_password" => (ZipItemRowStatus.NeedsPassword, "needs a password"),
            "in_use" => (ZipItemRowStatus.Pending, "open in another program"),
            _ => (ZipItemRowStatus.Error, result.Message),
        };

    private async Task ProbeRowsAsync(IReadOnlyList<ZipItemRow> rows, CancellationToken token)
    {
        if (rows.Count == 0) return;
        var saved = _savedPasswords;
        await Task.WhenAll(rows.Select(async row =>
        {
            await _probeGate.WaitAsync();
            try
            {
                if (token.IsCancellationRequested) return;
                var verdict = await Scheduler.Run(() => Probe(row, saved));
                if (verdict is null || token.IsCancellationRequested) return;
                var (status, note) = verdict.Value;
                RunOnUi(() =>
                {
                    // Only a row still waiting for its first word: a run that
                    // finished meanwhile has said something truer, and a row
                    // Clear removed is nobody's to write to.
                    if (!Rows.Contains(row) || row.StatusKind != ZipItemRowStatus.Pending) return;
                    row.Mark(status, note);
                    OnRowsChanged();
                });
            }
            finally
            {
                _probeGate.Release();
            }
        }));
    }

    // ---------------------------------------------------------------- batch

    /// <summary>One Core call and the rows it answers for. <see cref="Operation"/>
    /// receives the candidate passwords snapshotted on the UI thread just
    /// before it is scheduled.</summary>
    protected sealed record Unit<TResult>(IReadOnlyList<ZipItemRow> Rows, Func<IReadOnlyList<string>, TResult> Operation);

    /// <summary>One bucket of the verdict line. <see cref="Plural"/> when
    /// the label changes with the count ("1 needs a password" / "2 need a
    /// password"); null when it does not ("1 extracted" / "2 extracted").</summary>
    protected sealed record TallyClause(string Status, string Label, string? Plural = null);

    /// <summary>Runs one operation per unit, one unit at a time. Extract
    /// and Merge are this method with different units — the duplication the
    /// two batch tools used to carry a copy of each.
    ///
    /// The subclass selects the units, so only runnable rows (Pending or
    /// NeedsPassword) ever arrive here: a row that finished is left exactly
    /// as it is, and re-adding the source (a fresh Pending row) is how a
    /// failed one is retried.
    ///
    /// <paramref name="clauses"/> are matched against each result's own
    /// status string, in order; a status matching none of them counts toward
    /// the LAST clause, which is how "error" and anything unrecognized share
    /// a bucket.</summary>
    protected async Task RunBatchAsync<TResult>(
        IReadOnlyList<Unit<TResult>> units,
        Func<TResult, string> statusOf,
        Action<IReadOnlyList<ZipItemRow>, TResult> apply,
        string progressVerb,
        IReadOnlyList<TallyClause> clauses)
    {
        if (units.Count == 0) return;   // nothing runnable — re-add to retry

        var token = _cts.Token;
        var counts = new int[clauses.Count];

        IsBusy = true;
        try
        {
            for (var i = 0; i < units.Count; i++)
            {
                // Checked BETWEEN units, never mid-unit: a half-written output
                // is worse than a late one.
                if (token.IsCancellationRequested) break;

                var unit = units[i];
                Status = $"{progressVerb} {i + 1} of {units.Count}…";
                var candidates = Candidates();
                var result = await Scheduler.Run(() => unit.Operation(candidates));

                // Tallied from the result's OWN status rather than from the
                // rows after applying it: the apply may be marshalled onto the
                // UI thread and has not necessarily landed yet.
                var status = statusOf(result);
                var slot = -1;
                for (var c = 0; c < clauses.Count; c++)
                    if (clauses[c].Status == status) { slot = c; break; }
                counts[slot >= 0 ? slot : clauses.Count - 1]++;

                ApplyOnUi(unit.Rows, result, apply);
            }
        }
        finally
        {
            IsBusy = false;
        }

        // Clear replaces _cts with a FRESH source rather than merely
        // cancelling this one in place (see ClearCommand) — so if Clear ran
        // while this loop was still going, _cts.Token is no longer even the
        // SAME token this run captured above. That is what tells "cancelled
        // because Clear ran" — which already wrote its own "" and must not
        // have it overwritten with a partial count for rows nobody can see
        // anymore (QC-05) — apart from "cancelled because the window
        // closed" (Cancel() cancels this SAME token in place, no
        // replacement; OnClosed is its only caller, and nobody is around to
        // see the difference either way).
        if (token == _cts.Token)
        {
            var parts = new List<string>();
            for (var i = 0; i < clauses.Count; i++)
            {
                if (counts[i] == 0) continue;
                var label = counts[i] == 1 || clauses[i].Plural is null ? clauses[i].Label : clauses[i].Plural;
                parts.Add($"{counts[i]} {label}");
            }
            Status = string.Join(" · ", parts);
        }

        // Rows leaving Pending during the loop above change each row's OWN
        // StatusKind, not the Rows collection, so the CollectionChanged
        // subscription in the constructor never fires for it. Without this
        // call, a button whose count derives from row status (e.g.
        // ExtractButtonText's RunnableZips) goes stale the instant the batch
        // finishes: CanExecute correctly disables it, but the label still
        // names the pre-run count.
        //
        // Marshalled, NOT called directly: ApplyOnUi above POSTS the last
        // unit's apply rather than running it, so a direct call here reads
        // those rows while they are still Pending and announces a count that
        // is one too high — the same defect one step later. Posting queues
        // this behind every apply the loop issued, so it reads settled rows.
        RunOnUi(OnRowsChanged);
    }

    /// <summary>Marshals onto UiContext when one is set — a raw thread-pool
    /// continuation has no synchronization context of its own to inherit.</summary>
    protected void ApplyOnUi<TResult>(IReadOnlyList<ZipItemRow> rows, TResult result,
        Action<IReadOnlyList<ZipItemRow>, TResult> apply)
    {
        if (UiContext is null) apply(rows, result);
        else UiContext.Post(_ => apply(rows, result), null);
    }

    /// <summary>Marshals a context-free action onto UiContext, for the
    /// one-shot operations that write Status rather than a row.</summary>
    protected void RunOnUi(Action action)
    {
        if (UiContext is null) action();
        else UiContext.Post(_ => action(), null);
    }

    /// <summary>Stops any not-yet-started unit from starting (one already
    /// under way finishes) and any not-yet-started probe from landing.</summary>
    public void Cancel()
    {
        _cts.Cancel();
        _probeCts.Cancel();
    }
}
```

- [ ] **Step 5: Rewrite `ZipExtractViewModel.cs`**

Replace the entire file with:

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Zip and unzip window: one list holding files, folders and
/// archives, and the buttons light from what is in it. Zip folds the whole
/// list into one archive; Extract maps each runnable archive to its own
/// sibling folder. They are inverse operations on the same objects, which is
/// why one list serves both and nobody has to pick a mode.
///
/// Every button carries its own count ("Zip 5 items", "Extract 2 zips"), so a
/// mixed list states each action's scope rather than leaving it to be
/// inferred. A locked archive is probed as it is added (the saved passwords
/// only — see the base class) and, at Extract time, opened with the
/// window's candidates or the prompt; a skipped prompt leaves it runnable.</summary>
public sealed class ZipExtractViewModel : ZipListViewModel
{
    private readonly Func<IReadOnlyList<string>, string?, Zipper.ZipResult> _zipper;
    private readonly Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult> _extractor;
    private readonly Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult> _zipProbe;

    public ZipExtractViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult>? extractor = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null)
        : base(dialogs, savedPasswords, scheduler, uiContext)
    {
        _zipper = zipper ?? Zipper.CreateZip;
        _extractor = extractor ?? Zipper.Extract;
        _zipProbe = zipProbe ?? Zipper.Probe;

        ZipCommand = new AsyncRelayCommand(() => ZipAsync(null), () => Rows.Count > 0);
        ZipAsCommand = new AsyncRelayCommand(ZipWithDialogAsync, () => Rows.Count > 0);
        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => RunnableZips > 0);
    }

    /// <summary>Anything that exists — a PDF is valid input here, just for
    /// the other button.</summary>
    protected override ISet<string>? Extensions => null;

    protected override string IntakeNoun => "item";

    private int RunnableZips => Rows.Count(r => r.IsZip && r.IsRunnable);

    /// <summary>Archives only: a loose file or a folder needs nothing said
    /// about it before Zip folds it in.</summary>
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) =>
        row.IsZip ? FromZipProbe(_zipProbe(row.Path, savedPasswords)) : null;

    protected override void OnRowsChanged()
    {
        Raise(nameof(ZipButtonText));
        Raise(nameof(ExtractButtonText));
        ZipCommand.RaiseCanExecuteChanged();
        ZipAsCommand.RaiseCanExecuteChanged();
        ExtractCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand ZipCommand { get; }
    public AsyncRelayCommand ZipAsCommand { get; }
    public AsyncRelayCommand ExtractCommand { get; }

    /// <summary>Counts the WHOLE list: zipping never excludes anything.</summary>
    public string ZipButtonText => Rows.Count switch
    {
        0 => "Zip",
        1 => "Zip 1 item",
        var n => $"Zip {n} items",
    };

    /// <summary>Counts only the archives a click would actually act on —
    /// pending ones and ones still waiting for a password — so a mixed list
    /// cannot overstate this button's reach.</summary>
    public string ExtractButtonText => RunnableZips switch
    {
        0 => "Extract",
        1 => "Extract 1 zip",
        var n => $"Extract {n} zips",
    };

    /// <summary>The fold: the whole list into one archive, at the default
    /// location Zipper.CreateZip picks or wherever Save-As sent it. A no-op
    /// on an empty list — the buttons are disabled then anyway, this is the
    /// same belt-and-braces guard every other batch command applies.</summary>
    internal async Task ZipAsync(string? outputPath)
    {
        if (Rows.Count == 0) return;
        var paths = Rows.Select(r => r.Path).ToList();
        var itemCount = paths.Count;
        var result = await Scheduler.Run(() => _zipper(paths, outputPath));
        RunOnUi(() => Status = result.Status == "ok"
            ? $"Created {System.IO.Path.GetFileName(result.Output!)} · {itemCount} item{(itemCount == 1 ? "" : "s")}"
            : result.Message);
    }

    /// <summary>Asks where to save, suggesting Zipper.DefaultName's own pick,
    /// then runs the same path with that answer. A cancelled dialog is a
    /// silent no-op: Status is left exactly as it was.</summary>
    internal async Task ZipWithDialogAsync()
    {
        if (Rows.Count == 0) return;
        var suggested = Zipper.DefaultName(Rows.Select(r => r.Path).ToList());
        var path = Dialogs.AskSaveFile("Zip archive (*.zip)|*.zip", suggested);
        if (path is null) return;
        await ZipAsync(path);
    }

    /// <summary>The map: each runnable archive into its own sibling folder,
    /// one unit per row. Loose rows are never passed to the extractor. The
    /// candidates and the prompt are the base class's; the extractor asks
    /// only for what none of the candidates opens.</summary>
    internal Task ExtractAsync() => RunBatchAsync(
        Rows.Where(r => r.IsZip && r.IsRunnable)
            .Select(row => new Unit<Zipper.UnzipResult>(new[] { row },
                candidates => _extractor(row.Path, candidates, AskPassword)))
            .ToList(),
        r => r.Status,
        (rows, r) => rows[0].Apply(r),
        "Extracting",
        new[]
        {
            new TallyClause("ok", "extracted"),
            new TallyClause("needs_password", "needs a password", "need a password"),
            new TallyClause("error", "failed"),
        });
}
```

- [ ] **Step 6: Keep the two neighbours compiling**

In `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs`, the constructor body becomes:

```csharp
        // No saved passwords through this shell: it is deleted in Task 9,
        // when the windows construct their own view models with the list
        // MainWindow hands them.
        ZipExtract = new ZipExtractViewModel(dialogs, Array.Empty<string>(), scheduler, uiContext);
        MergePdfs = new MergePdfsViewModel(dialogs, scheduler, uiContext);
```

In `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` — Task 7 rewrites this file; for now only what the new base demands. The constructor becomes:

```csharp
    public MergePdfsViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<string, PdfMerge.MergeResult>? merger = null)
        : base(dialogs, Array.Empty<string>(), scheduler, uiContext)
```

add the abstract member:

```csharp
    // Task 7 gives this window its real probes; until then a row is left
    // exactly as it was added.
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) => null;
```

and `MergeAsync` becomes unit-shaped:

```csharp
    internal Task MergeAsync() => RunBatchAsync(
        Rows.Where(r => r.IsZip && r.IsRunnable)
            .Select(row => new Unit<PdfMerge.MergeResult>(new[] { row }, _ => _merger(row.Path)))
            .ToList(),
        r => r.Status,
        (rows, r) => rows[0].Apply(r),
        "Merging",
        new[]
        {
            new TallyClause("ok", "merged"),
            new TallyClause("no_pdfs", "had no PDFs"),
            new TallyClause("error", "failed"),
        });
```

In `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`, `MakeVm` becomes `new(new FakeDialogs(), new InlineWorkScheduler(), uiContext: null, merger)` for this task only — Task 7 rewrites the file.

The E2E scenarios construct `ZipToolsViewModel(ctx.Dialogs, SynchronizationContext.Current, new InlineScheduler())` — that signature is unchanged, so nothing under `tools/` moves in this task.

- [ ] **Step 7: Run the three suites, then the full check**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipItemRowTests|FullyQualifiedName~ZipExtractViewModelTests|FullyQualifiedName~ZipListClearAndRemoveTests|FullyQualifiedName~MergePdfsViewModelTests" -v minimal`
Expected: `Failed: 0`.

Then the full check. Two suites deserve a look if anything fails: `E2EHarnessTests` does not run the zip scenarios, but the smoke tool must still build (`dotnet build tools/OrdoSort.Smoke` is part of the solution build), and the registries (`AutoFitColumnTests`, `DataGridNoteColourTests`, `DataGridSelectionContrastTests`, `WindowOverflowTests`) still construct `ZipToolsViewModel(new FakeDialogs())`, which still compiles.

- [ ] **Step 8: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs tests/OrdoSort.Wpf.Tests/ZipListClearAndRemoveTests.cs tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs
git commit -m "feat(zip): the list runs units, owns the passwords, and probes what is dropped on it

Three things the shared base class grows. Units: a batch is a list of one
Core call plus the rows it answers for — a zip row is a unit of one, and
the loose PDFs the Merge window will take are one unit of many, because
they become one document. Passwords: the typed-this-window list (most
recent first) ahead of the saved one, snapshotted on the UI thread before
each unit is scheduled, and the ask callback that crosses to the UI thread
with Send so the worker genuinely waits on the person — pinned by a
context that records HOW it was handed the call, because the 2026-08-19
merge shipped a marshalling gap every test hid with uiContext: null. The
probe on add: four at a time, saved passwords only so 'a saved password
opens this' is exactly true, a token replaced on Clear so a verdict never
lands on a row nobody can see.

NeedsPassword is the one new row state, and it is runnable: the next
Extract asks again. Extract counts runnable zips, tries the candidates,
and asks for the rest."
```

---

### Task 7: `MergePdfsViewModel` — PDFs and zips in, one document per source out

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` (rewrite)
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs:20-25` (one constructor call)
- Test: `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs` (rewrite)

**Interfaces:**
- Consumes: `PdfMerge.MergeZip`, `PdfMerge.MergeFiles`, `PdfMerge.DefaultName`, `PdfMerge.MergeResult.Item` (Task 4); `Zipper.Probe` (Task 2); `Unlock.ProbeReadiness` (existing); `ZipListViewModel`'s `Unit`, `TallyClause`, `Candidates`, `AskPassword`, `FromZipProbe`, `FromPdfProbe`, `ZipItemRow.Mark`, `IsPdf`, `IsRunnable` (Task 6).
- Produces: `MergePdfsViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords, IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null, Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null, Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null, Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null)`; `MergeCommand`, `MergeToCommand`, `MergeButtonText`; `internal Task MergeAsync(string? outputPath)`, `internal Task MergeToAsync()`.

- [ ] **Step 1: Rewrite the tests**

Replace the entire content of `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs` with:

```csharp
using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ZipFile = System.IO.Compression.ZipFile;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Merge PDFs window's view model: PDFs and zips in, one
/// document per source out. Every fact from the tab-era suite is kept
/// (ported onto the new seams) and the new shape is pinned on top: units,
/// fail-whole for the loose group, Merge to…, and the two probes.
///
/// InlineWorkScheduler resolves every Scheduler.Run call synchronously, so
/// MergeAsync can be awaited directly and asserted immediately after. Both
/// probes default to "not encrypted": the real ones on a TempDir's one-byte
/// files would call every row unreadable and leave nothing runnable.</summary>
public class MergePdfsViewModelTests
{
    private static MergePdfsViewModel MakeVm(
        IDialogService? dialogs = null,
        IReadOnlyList<string>? savedPasswords = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null) =>
        new(dialogs ?? new FakeDialogs(), savedPasswords ?? Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger, fileMerger,
            zipProbe ?? ((p, _) => new Zipper.ZipProbeResult(p, "not_encrypted")),
            pdfProbe ?? ((p, _) => new Unlock.ProbeResult("not_encrypted", p)));

    private static PdfMerge.MergeResult Ok(string source, string output, int pdfs) =>
        new(source, "ok", Output: output, PdfCount: pdfs);

    // ---- ported: zips, one unit each --------------------------------

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAMergeRun()
    {
        using var dir = new TempDir();
        var ok = dir.File("ok.zip");
        var noPdfs = dir.File("nopdfs.zip");
        var bad = dir.File("bad.zip");
        var locked = dir.File("locked.zip");
        var vm = MakeVm(zipMerger: (path, _, _) =>
            path == ok ? Ok(path, Path.Combine(dir.Path, "ok.pdf"), 2)
            : path == noPdfs ? new PdfMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : path == locked ? new PdfMerge.MergeResult(path, "needs_password", Message: "'x.pdf' inside needs a password", Item: "x.pdf")
            : new PdfMerge.MergeResult(path, "error", Message: "couldn't read 'x.pdf': bad", Item: "x.pdf"));

        await vm.AddPaths(new[] { ok, noPdfs, bad, locked });
        await vm.MergeAsync(null);

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(ZipItemRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "ok.pdf"), okRow.Output);
        Assert.Contains("ok.pdf", okRow.Note);
        Assert.Contains("2 PDFs", okRow.Note);

        var noPdfsRow = Assert.Single(vm.Rows, r => r.Path == noPdfs);
        Assert.Equal(ZipItemRowStatus.NoPdfs, noPdfsRow.StatusKind);
        Assert.Equal("no PDFs inside", noPdfsRow.Note);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(ZipItemRowStatus.Error, badRow.StatusKind);
        Assert.Contains("x.pdf", badRow.Note);

        var lockedRow = Assert.Single(vm.Rows, r => r.Path == locked);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, lockedRow.StatusKind);
        Assert.Equal("'x.pdf' inside needs a password", lockedRow.Note);
        Assert.True(lockedRow.IsRunnable);

        Assert.Equal("1 merged · 1 had no PDFs · 1 needs a password · 1 failed", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingMerges()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1));
        await vm.AddPaths(new[] { dir.File("a.zip"), dir.File("b.zip") });
        await vm.MergeAsync(null);
        Assert.Equal("2 merged", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingFails()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => new PdfMerge.MergeResult(path, "error", Message: "nope"));
        await vm.AddPaths(new[] { dir.File("a.zip") });
        await vm.MergeAsync(null);
        Assert.Equal("1 failed", vm.Status);
    }

    [Fact]
    public async Task MergeButtonTextCountsRunnableRowsOfBothKinds()
    {
        using var dir = new TempDir();
        var vm = MakeVm();
        Assert.Equal("Merge", vm.MergeButtonText);
        Assert.False(vm.MergeCommand.CanExecute(null));

        await vm.AddPaths(new[] { dir.File("a.zip") });
        Assert.Equal("Merge 1 item", vm.MergeButtonText);

        await vm.AddPaths(new[] { dir.File("b.pdf") });
        Assert.Equal("Merge 2 items", vm.MergeButtonText);
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATextFileIsRefusedWithANoteButAPdfIsTaken()
    {
        using var dir = new TempDir();
        var txt = dir.File("notes.txt");
        var pdf = dir.File("scan.pdf");
        var vm = MakeVm();

        await vm.AddPaths(new[] { txt, pdf });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(pdf, row.Path);
        Assert.Equal("pdf", row.Kind);
        Assert.Contains("isn't a PDF or zip", vm.AddNote);
    }

    [Fact]
    public async Task DuplicateReAddSetsAddNoteWithoutAddingADuplicateRow()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Equal("", vm.AddNote);

        await vm.AddPaths(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    [Fact]
    public async Task ACaseOnlyDuplicateIsNotAddedTwice()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var shouty = Path.Combine(dir.Path, "A.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task OnlyRunnableRowsMergeOnASecondRun()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(zipMerger: (path, _, _) =>
        {
            calls.Add(path);
            return Ok(path, path + ".out.pdf", 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);
        Assert.Equal(2, calls.Count);

        var c = dir.File("c.zip");
        await vm.AddPaths(new[] { c });
        calls.Clear();
        await vm.MergeAsync(null);

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task ANeedsPasswordZipIsRunAgainByTheNextMerge()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var calls = 0;
        var vm = MakeVm(zipMerger: (path, _, _) => ++calls == 1
            ? new PdfMerge.MergeResult(path, "needs_password", Message: "needs a password", Item: "a.zip")
            : Ok(path, path + ".out.pdf", 1));

        await vm.AddPaths(new[] { a });
        await vm.MergeAsync(null);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, vm.Rows.Single().StatusKind);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);

        await vm.MergeAsync(null);
        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single().StatusKind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelBetweenUnitsStopsRowsNotYetStarted()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        MergePdfsViewModel vm = null!;
        vm = MakeVm(zipMerger: (path, _, _) =>
        {
            if (path == a) vm.Cancel();
            return Ok(path, path + ".out.pdf", 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);

        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single(r => r.Path == a).StatusKind);
        Assert.Equal(ZipItemRowStatus.Pending, vm.Rows.Single(r => r.Path == b).StatusKind);
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNote()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1));
        await vm.AddPaths(new[] { dir.File("a.zip") });
        await vm.MergeAsync(null);
        Assert.NotEqual("", vm.Status);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Merge", vm.MergeButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.pdf");
        var vm = MakeVm();
        await vm.AddPaths(new[] { a, b });

        vm.RemoveSelected(vm.Rows.Where(r => r.Path == a).ToList());

        Assert.Equal(b, Assert.Single(vm.Rows).Path);
    }

    [Fact]
    public async Task RealZipMergerSmokeTestOnATwoPdfZip()
    {
        using var dir = new TempDir();
        var zipPath = Path.Combine(dir.Path, "real.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddPdf(string entryName)
            {
                using var doc = new PdfDocument();
                doc.AddPage();
                using var ms = new MemoryStream();
                doc.Save(ms, closeStream: false);
                ms.Position = 0;
                using var es = zip.CreateEntry(entryName).Open();
                ms.CopyTo(es);
            }
            AddPdf("a.pdf");
            AddPdf("b.pdf");
        }
        var vm = MakeVm();   // default zipMerger: the real PdfMerge.MergeZip

        await vm.AddPaths(new[] { zipPath });
        await vm.MergeAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "real.pdf"), row.Output);
    }

    // ---- the loose group ----------------------------------------------

    private static string WritePdf(string path, int pages = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task LoosePdfsAreOneUnitAndTheResultLandsOnEveryRow()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        var b = dir.File("b.pdf");
        var calls = new List<IReadOnlyList<string>>();
        var vm = MakeVm(fileMerger: (paths, output, _, _) =>
        {
            calls.Add(paths.ToList());
            return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), paths.Count);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);

        var one = Assert.Single(calls);
        Assert.Equal(new[] { a, b }, one);
        Assert.All(vm.Rows, r =>
        {
            Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind);
            Assert.Equal("→ Job.pdf (2 PDFs)", r.Note);
            Assert.Equal(Path.Combine(dir.Path, "Job.pdf"), r.Output);
        });
        Assert.Equal("1 merged", vm.Status);
        Assert.Equal("Merge", vm.MergeButtonText);
    }

    [Fact]
    public async Task ZipsRunFirstAndTheLooseGroupLast()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var order = new List<string>();
        var vm = MakeVm(
            zipMerger: (path, _, _) => { order.Add("zip"); return Ok(path, path + ".out.pdf", 1); },
            fileMerger: (paths, _, _, _) => { order.Add("group"); return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1); });

        await vm.AddPaths(new[] { pdf, zip });   // the PDF is listed FIRST
        await vm.MergeAsync(null);

        Assert.Equal(new[] { "zip", "group" }, order);
    }

    /// <summary>Fail-whole for the loose group: the culprit takes the
    /// result — runnable NeedsPassword — and every other row is held back
    /// with a note naming it, still Pending, so the next Merge picks them all
    /// up once the culprit is opened or removed.</summary>
    [Fact]
    public async Task AFailedGroupMarksTheCulpritAndHoldsTheOthersBack()
    {
        using var dir = new TempDir();
        var cover = dir.File("cover.pdf");
        var report = dir.File("report.pdf");
        var locked = dir.File("locked.pdf");
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "needs_password", Message: "needs a password", Item: locked));

        await vm.AddPaths(new[] { cover, report, locked });
        await vm.MergeAsync(null);

        var lockedRow = vm.Rows.Single(r => r.Path == locked);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, lockedRow.StatusKind);
        Assert.Equal("needs a password", lockedRow.Note);
        foreach (var held in vm.Rows.Where(r => r.Path != locked))
        {
            Assert.Equal(ZipItemRowStatus.Pending, held.StatusKind);
            Assert.Equal("not merged — locked.pdf needs a password", held.Note);
        }
        Assert.Equal("Merge 3 items", vm.MergeButtonText);
        Assert.Equal("1 needs a password", vm.Status);
    }

    [Fact]
    public async Task AnUnreadableCulpritIsAnErrorAndTheOthersSayCouldntBeRead()
    {
        using var dir = new TempDir();
        var good = dir.File("good.pdf");
        var junk = dir.File("junk.pdf");
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "error", Message: "couldn't read it: not a PDF", Item: junk));

        await vm.AddPaths(new[] { good, junk });
        await vm.MergeAsync(null);

        Assert.Equal(ZipItemRowStatus.Error, vm.Rows.Single(r => r.Path == junk).StatusKind);
        var held = vm.Rows.Single(r => r.Path == good);
        Assert.Equal(ZipItemRowStatus.Pending, held.StatusKind);
        Assert.Equal("not merged — junk.pdf couldn't be read", held.Note);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);   // the Error row is finished; the held one is not
    }

    [Fact]
    public async Task AGroupFailureWithNoCulpritLeavesEveryRowPendingWithTheMessage()
    {
        using var dir = new TempDir();
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "error", Message: "couldn't save the merged PDF: disk full"));

        await vm.AddPaths(new[] { dir.File("a.pdf"), dir.File("b.pdf") });
        await vm.MergeAsync(null);

        Assert.All(vm.Rows, r =>
        {
            Assert.Equal(ZipItemRowStatus.Pending, r.StatusKind);
            Assert.Equal("couldn't save the merged PDF: disk full", r.Note);
        });
    }

    /// <summary>Typed during a zip, remembered for the group: zips run first,
    /// so a password typed for one serves the loose PDFs after it without a
    /// second prompt.</summary>
    [Fact]
    public async Task APasswordTypedForAZipIsACandidateForTheLooseGroup()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var dialogs = new FakeDialogs();
        dialogs.PasswordAnswers.Enqueue("typed");
        IReadOnlyList<string>? groupCandidates = null;
        var vm = MakeVm(dialogs: dialogs,
            zipMerger: (path, _, ask) => ask!(new PasswordRequest("a.zip", null, false)) == "typed"
                ? Ok(path, path + ".out.pdf", 1)
                : new PdfMerge.MergeResult(path, "needs_password", Message: "needs a password", Item: "a.zip"),
            fileMerger: (paths, _, candidates, _) =>
            {
                groupCandidates = candidates.ToList();
                return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1);
            });

        await vm.AddPaths(new[] { zip, pdf });
        await vm.MergeAsync(null);

        Assert.Equal(new[] { "typed" }, groupCandidates);
        Assert.Single(dialogs.PasswordRequests);
    }

    // ---- Merge to… ----------------------------------------------------

    [Fact]
    public async Task MergeToIsEnabledOnlyWhileARunnableLoosePdfIsListed()
    {
        using var dir = new TempDir();
        var vm = MakeVm(
            zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1),
            fileMerger: (paths, _, _, _) => Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1));

        await vm.AddPaths(new[] { dir.File("a.zip") });
        Assert.False(vm.MergeToCommand.CanExecute(null));

        await vm.AddPaths(new[] { dir.File("b.pdf") });
        Assert.True(vm.MergeToCommand.CanExecute(null));

        await vm.MergeAsync(null);
        Assert.False(vm.MergeToCommand.CanExecute(null));   // merged — nothing loose left to send anywhere
    }

    [Fact]
    public async Task MergeToPassesTheChosenPathToTheLooseGroupOnly()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var chosen = Path.Combine(dir.Path, "chosen.pdf");
        string? seenOutput = "not called";
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = chosen },
            zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1),
            fileMerger: (paths, output, _, _) => { seenOutput = output; return Ok(paths[0], output!, 1); });

        await vm.AddPaths(new[] { zip, pdf });
        await vm.MergeToAsync();

        Assert.Equal(chosen, seenOutput);
        Assert.Equal(chosen, vm.Rows.Single(r => r.Path == pdf).Output);
        Assert.Equal(zip + ".out.pdf", vm.Rows.Single(r => r.Path == zip).Output);
    }

    [Fact]
    public async Task MergeToCancelledIsASilentNoOp()
    {
        using var dir = new TempDir();
        var calls = 0;
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = null },
            fileMerger: (paths, _, _, _) => { calls++; return Ok(paths[0], "irrelevant.pdf", 1); });
        await vm.AddPaths(new[] { dir.File("a.pdf") });

        await vm.MergeToAsync();

        Assert.Equal(0, calls);
        Assert.Equal("", vm.Status);
    }

    // ---- the probes on add ---------------------------------------------

    [Fact]
    public async Task EachKindGetsItsOwnProbeAndTheVerdictLandsOnTheRow()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var vm = MakeVm(
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "needs_password"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("ready", p, MatchedIndex: 0));

        await vm.AddPaths(new[] { zip, pdf });

        var zipRow = vm.Rows.Single(r => r.Path == zip);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, zipRow.StatusKind);
        Assert.Equal("needs a password", zipRow.Note);
        var pdfRow = vm.Rows.Single(r => r.Path == pdf);
        Assert.Equal(ZipItemRowStatus.Pending, pdfRow.StatusKind);
        Assert.Equal("a saved password opens this", pdfRow.Note);
        Assert.Equal("Merge 2 items", vm.MergeButtonText);
    }

    [Fact]
    public async Task APdfInUseStaysPendingWithANote()
    {
        using var dir = new TempDir();
        var vm = MakeVm(pdfProbe: (p, _) => new Unlock.ProbeResult("in_use", p, Message: "It's open in another program"));

        await vm.AddPaths(new[] { dir.File("b.pdf") });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);
        Assert.Equal("open in another program", row.Note);
    }

    /// <summary>The real probe and the real merger on real documents — the
    /// difference between this file's scripts and the feature.</summary>
    [Fact]
    public async Task RealFileMergerSmokeTestOnTwoLoosePdfs()
    {
        using var dir = new TempDir();
        var a = WritePdf(Path.Combine(dir.Path, "a.pdf"), 2);
        var b = WritePdf(Path.Combine(dir.Path, "b.pdf"), 3);
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler());

        await vm.AddPaths(new[] { a, b });
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Pending, r.StatusKind));   // the real probe: not encrypted
        await vm.MergeAsync(null);

        var expected = Path.Combine(dir.Path, Path.GetFileName(dir.Path) + ".pdf");
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind));
        Assert.True(File.Exists(expected));
        using var merged = PdfReader.Open(expected, PdfDocumentOpenMode.Import);
        Assert.Equal(5, merged.PageCount);
    }

    /// <summary>The two windows' lists never interact — that separation is
    /// the whole reason Merge PDFs has its own window rather than being a
    /// third button beside Extract.</summary>
    [Fact]
    public async Task ItsListIsIndependentOfTheZipAndUnzipWindow()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var merge = MakeVm();
        var zipExtract = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            extractor: (p, _, _) => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));

        await merge.AddPaths(new[] { zip });
        await zipExtract.AddPaths(new[] { zip });
        await zipExtract.ExtractAsync();

        Assert.Equal(ZipItemRowStatus.Pending, merge.Rows.Single().StatusKind);
        Assert.True(merge.MergeCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MergePdfsViewModelTests" -v minimal`
Expected: build FAILS — no 8-parameter `MergePdfsViewModel` constructor, no `MergeToCommand`, no `MergeAsync(string?)`.

- [ ] **Step 3: Rewrite `MergePdfsViewModel.cs`**

Replace the entire file with:

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Merge PDFs window: drop PDFs and zips, and one document
/// comes out per source — every PDF inside a zip into &lt;zipname&gt;.pdf
/// beside it, and every loose PDF in the list into one file beside the first
/// of them. Its own window and its own list because it is a different job
/// wearing a zip costume — it consumes archives and documents and produces
/// a document — and because a separate list means extracting an archive in
/// the other window has no bearing on merging it here.
///
/// Units (see the base class): each runnable zip row is a unit of one; the
/// runnable loose PDFs are one unit of many, run last. Fail-whole applies
/// per unit: one PDF nobody can open merges nothing from its unit, and the
/// rows it held back say so (<see cref="ApplyToUnit"/>).</summary>
public sealed class MergePdfsViewModel : ZipListViewModel
{
    private readonly Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _zipMerger;
    private readonly Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _fileMerger;
    private readonly Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult> _zipProbe;
    private readonly Func<string, IReadOnlyList<string>, Unlock.ProbeResult> _pdfProbe;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase).</summary>
    private static readonly ISet<string> PdfsAndZips = new HashSet<string> { "pdf", "zip" };

    public MergePdfsViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null)
        : base(dialogs, savedPasswords, scheduler, uiContext)
    {
        _zipMerger = zipMerger ?? PdfMerge.MergeZip;
        _fileMerger = fileMerger ?? PdfMerge.MergeFiles;
        _zipProbe = zipProbe ?? Zipper.Probe;
        _pdfProbe = pdfProbe ?? Unlock.ProbeReadiness;

        MergeCommand = new AsyncRelayCommand(() => MergeAsync(null), () => RunnableRows > 0);
        MergeToCommand = new AsyncRelayCommand(MergeToAsync, () => RunnableLoosePdfs > 0);
    }

    /// <summary>PDFs and archives; anything else is refused by intake with
    /// its usual note — "that isn't a PDF or zip" is the honest answer on a
    /// window that can only merge.</summary>
    protected override ISet<string>? Extensions => PdfsAndZips;

    protected override string IntakeNoun => "PDF or zip";

    private int RunnableRows => Rows.Count(r => r.IsRunnable);
    private int RunnableLoosePdfs => Rows.Count(r => r.IsPdf && r.IsRunnable);

    /// <summary>Zips at archive level, loose PDFs through Unlock's own
    /// probe. PDFs INSIDE a zip are not probed here — that would read every
    /// archive fully twice over a share — and are asked for during the run.</summary>
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) =>
        row.IsZip ? FromZipProbe(_zipProbe(row.Path, savedPasswords))
        : row.IsPdf ? FromPdfProbe(_pdfProbe(row.Path, savedPasswords))
        : null;

    protected override void OnRowsChanged()
    {
        Raise(nameof(MergeButtonText));
        MergeCommand.RaiseCanExecuteChanged();
        MergeToCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand MergeCommand { get; }
    public AsyncRelayCommand MergeToCommand { get; }

    /// <summary>Counts every runnable row — a zip or a loose PDF alike —
    /// matching MergeCommand's own CanExecute.</summary>
    public string MergeButtonText => RunnableRows switch
    {
        0 => "Merge",
        1 => "Merge 1 item",
        var n => $"Merge {n} items",
    };

    /// <summary>Merge to…: a Save-As for the loose-PDF output only — zips
    /// already have a natural name and place. Suggests PdfMerge.DefaultName's
    /// own pick; a cancelled dialog is a silent no-op.</summary>
    internal async Task MergeToAsync()
    {
        var loose = Rows.Where(r => r.IsPdf && r.IsRunnable).Select(r => r.Path).ToList();
        if (loose.Count == 0) return;
        var path = Dialogs.AskSaveFile("PDF (*.pdf)|*.pdf", PdfMerge.DefaultName(loose));
        if (path is null) return;
        await MergeAsync(path);
    }

    /// <summary>Zips first, one unit each, then the loose group as one unit
    /// — runnable rows only. <paramref name="outputPath"/> reaches the loose
    /// group alone. The candidates and the prompt are the base class's; a
    /// merger asks only for what none of the candidates opens.</summary>
    internal Task MergeAsync(string? outputPath)
    {
        var units = new List<Unit<PdfMerge.MergeResult>>();
        foreach (var row in Rows.Where(r => r.IsZip && r.IsRunnable))
        {
            var zipRow = row;
            units.Add(new Unit<PdfMerge.MergeResult>(new[] { zipRow },
                candidates => _zipMerger(zipRow.Path, candidates, AskPassword)));
        }
        var loose = Rows.Where(r => r.IsPdf && r.IsRunnable).ToList();
        if (loose.Count > 0)
        {
            var paths = loose.Select(r => r.Path).ToList();
            units.Add(new Unit<PdfMerge.MergeResult>(loose,
                candidates => _fileMerger(paths, outputPath, candidates, AskPassword)));
        }
        return RunBatchAsync(units, r => r.Status, ApplyToUnit, "Merging",
            new[]
            {
                new TallyClause("ok", "merged"),
                new TallyClause("no_pdfs", "had no PDFs"),
                new TallyClause("needs_password", "needs a password", "need a password"),
                new TallyClause("error", "failed"),
            });
    }

    /// <summary>One result, every row of the unit. A unit of one, or any
    /// success, is the row's own Apply. A failed group is fail-whole: the
    /// culprit (MergeResult.Item, a full path) takes the result — a runnable
    /// NeedsPassword, or an Error — and every other row stays Pending with a
    /// note naming what held it back, so the next run picks them up again
    /// once the culprit is opened or removed. A group failure with no
    /// culprit (the save itself failed) leaves every row Pending with the
    /// message.</summary>
    private static void ApplyToUnit(IReadOnlyList<ZipItemRow> rows, PdfMerge.MergeResult result)
    {
        if (rows.Count == 1 || result.Status == "ok")
        {
            foreach (var row in rows) row.Apply(result);
            return;
        }
        if (result.Item is null)
        {
            foreach (var row in rows) row.Mark(ZipItemRowStatus.Pending, result.Message);
            return;
        }
        var culpritName = System.IO.Path.GetFileName(result.Item);
        var reason = result.Status == "needs_password" ? "needs a password" : "couldn't be read";
        foreach (var row in rows)
        {
            if (string.Equals(row.Path, result.Item, StringComparison.OrdinalIgnoreCase)) row.Apply(result);
            else row.Mark(ZipItemRowStatus.Pending, $"not merged — {culpritName} {reason}");
        }
    }
}
```

In `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs`, the second constructor line becomes:

```csharp
        MergePdfs = new MergePdfsViewModel(dialogs, Array.Empty<string>(), scheduler, uiContext);
```

- [ ] **Step 4: Run the suite to verify it passes, then the full check**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MergePdfsViewModelTests" -v minimal` — expected `Failed: 0`.

Then the full check. The E2E `ZipMergeScenarios` still drives the old tab through `ZipToolsViewModel`, whose constructor signature is unchanged, so the smoke tool still builds; its `EncryptedInside` scenario is not run by the unit suite (Task 10 retargets it).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs
git commit -m "feat(merge): PDFs and zips in, one document per source out

The merge list takes loose PDFs now, not just archives. Each runnable zip
is a unit of one, run first; the runnable loose PDFs are one unit of many,
run last, because they become one document — and a password typed for a
zip is already a candidate by the time the group runs. Merge to… is a
Save-As for that group alone; zips already have a natural name and place.

Fail-whole per unit: the culprit a merge names takes the result — a
runnable NeedsPassword, or an Error — and every row it held back stays
Pending with 'not merged — x needs a password', so the next Merge picks
them all up once x is opened or removed. Both probes land on add: zips at
archive level, loose PDFs through Unlock's own readiness check."
```

---

### Task 8: `MergePdfsWindow`, the Tools entry, and the registries

**Files:**
- Create: `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml`, `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml.cs`
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml:338-340`, `src/OrdoSort.Wpf/MainWindow.xaml.cs:373-375`
- Modify (registries): `tests/OrdoSort.Wpf.Tests/DataGridWindowCoverageTests.cs:68-76,135-146`, `tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs:66-70`, `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs`, `tests/OrdoSort.Wpf.Tests/DataGridSelectionContrastTests.cs`, `tests/OrdoSort.Wpf.Tests/DataGridNoteColourTests.cs`, `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs`
- Test: `tests/OrdoSort.Wpf.Tests/MergePdfsWindowTests.cs`

**Interfaces:**
- Consumes: `MergePdfsViewModel` (Task 7); `PasswordVault.Reveal`, `ShellViewModel.Cfg`, `DataGridColumnCap.Track` (existing).
- Produces: `MergePdfsWindow(MergePdfsViewModel vm)`; `internal void MergePdfsWindow.AcceptDrop(IDataObject data)` — the drop seam Task 9 mirrors on `ZipToolsWindow`; `MainWindow.SavedPasswordsNow()` (private) — Task 9's `OnZipTools` reuses it.

At the end of this task the app offers Merge PDFs in two places — the old tab and the new window. Task 9 removes the tab. Deliberate: no commit on this branch loses the feature.

- [ ] **Step 1: Write the failing test — the regression pin**

Create `tests/OrdoSort.Wpf.Tests/MergePdfsWindowTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The 2026-08-25 spec's regression pin. The defect it closes is
/// "a drop can reach a list you did not aim at", and the fact that makes
/// that impossible is structural: this window holds ZERO TabControls and
/// exactly ONE DataGrid, and a FileDrop lands one row in that one list. A
/// count assertion, not a "the right list got it" assertion — with one list
/// those are the same claim, and the count is the one that keeps failing if
/// a second list is ever reintroduced.</summary>
[Collection(HighlightContrastTests.Name)]
public class MergePdfsWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public MergePdfsWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public void OneListNoTabsAndADroppedZipLandsInIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));
        var window = new MergePdfsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            var content = (DependencyObject)window.Content;
            Assert.Empty(Descendants<TabControl>(content));
            var grid = Assert.Single(Descendants<DataGrid>(content));
            Assert.Same(vm.Rows, grid.ItemsSource);

            // InlineWorkScheduler and no UiContext: AddPaths has completed by
            // the time AcceptDrop returns, so the row count is the assertion.
            window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { zip }));

            Assert.Equal(zip, Assert.Single(vm.Rows).Path);
        }
        finally { window.Close(); }
    });
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MergePdfsWindowTests" -v minimal`
Expected: build FAILS with `The type or namespace name 'MergePdfsWindow' could not be found`.

- [ ] **Step 3: Create `MergePdfsWindow.xaml`**

```xml
<Window x:Class="OrdoSort.Wpf.Windows.MergePdfsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:OrdoSort.Wpf.ViewModels"
        Title="OrdoSort — Merge PDFs" Width="700" Height="520" MinWidth="580" MinHeight="420"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False" AllowDrop="True"
        DragOver="OnDragOver" Drop="OnDrop"
        Style="{StaticResource {x:Type Window}}">
    <!-- One job, one list, window-level drop: the destination is the window
         the drop landed on, so there is no routing decision left to get
         wrong (2026-08-25 spec). -->
    <DockPanel Margin="14">
        <DockPanel DockPanel.Dock="Bottom" Margin="0,10,0,0">
            <Button DockPanel.Dock="Right" Content="Close" Width="96" IsCancel="True" />
            <StackPanel Orientation="Horizontal">
                <Button Command="{Binding MergeCommand}"
                        Style="{StaticResource PrimaryButton}" MinWidth="120" Margin="0,0,8,0"
                        AutomationProperties.Name="{Binding MergeButtonText}">
                    <TextBlock Text="{Binding MergeButtonText}"
                               Style="{StaticResource PrimaryButtonLabel}" />
                </Button>
                <Button Content="Merge to…" Command="{Binding MergeToCommand}" Margin="0,0,10,0" />
                <!-- MaxWidth is load-bearing, not cosmetic: StatusText
                     carries TextWrapping="Wrap" (Theme/Styles.xaml), and a
                     horizontal StackPanel measures every child at infinite
                     width, so without a finite cap the wrap never engages
                     and the line runs off screen — the failure
                     TextWrapCoverageTests exists to catch. -->
                <TextBlock Text="{Binding Status}" VerticalAlignment="Center"
                           Style="{StaticResource StatusText}" MaxWidth="360" />
            </StackPanel>
        </DockPanel>

        <Grid Margin="16,8">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <DockPanel Margin="0,0,0,10">
                <TextBlock DockPanel.Dock="Right" Text="{Binding AddNote}"
                           Style="{StaticResource CaptionText}" VerticalAlignment="Center"
                           MaxWidth="240" TextTrimming="CharacterEllipsis" />
                <!-- WrapPanel, not a horizontal StackPanel: the buttons and
                     the note do not fit this window's 580 MinWidth at the
                     18px font preset, and a StackPanel would push the last
                     button off screen rather than move it to a second row.
                     The 4 bottom margin is the gap that second row needs. -->
                <WrapPanel>
                    <Button Content="Add PDFs or zips…" Click="OnAddFiles" Margin="0,0,6,4" />
                    <Button Content="Remove selected" Click="OnRemoveSelected" Margin="0,0,6,4"
                            IsEnabled="{Binding IsIdle}" />
                    <Button Content="Clear" Command="{Binding ClearCommand}" Margin="0,0,10,4" />
                </WrapPanel>
            </DockPanel>

            <Grid Grid.Row="1">
                <DataGrid x:Name="ItemsGrid" ItemsSource="{Binding Rows}"
                          AutomationProperties.Name="PDFs and archives to merge" AutoGenerateColumns="False"
                          IsReadOnly="True" CanUserAddRows="False" HeadersVisibility="Column"
                          SelectionMode="Extended">
                    <!-- Theme.RowHover so the hover tint stays consistent
                         app-wide; the row ToolTip carries the full path,
                         which no column shows. -->
                    <DataGrid.RowStyle>
                        <Style TargetType="DataGridRow">
                            <Setter Property="Background" Value="Transparent" />
                            <Setter Property="ToolTip" Value="{Binding Path}" />
                            <Style.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter Property="Background"
                                            Value="{DynamicResource Theme.RowHover}" />
                                </Trigger>
                            </Style.Triggers>
                        </Style>
                    </DataGrid.RowStyle>
                    <DataGrid.Columns>
                        <!-- Item: the filler (Width="*"). GridCellTextSelectionAware
                             for the selection-contrast reason ZipToolsWindow.xaml
                             records (Theme.Text on Theme.Accent measured
                             1.26:1-2.14:1 without it). MinWidth is load-bearing
                             arithmetic: DataGridColumnCap computes the Result cap
                             as the viewport minus everyone else's floor, so
                             without one a long message squeezes this column to
                             WPF's 20px default. -->
                        <DataGridTextColumn Header="Item" Binding="{Binding Display}" Width="*" MinWidth="180">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Path}" />
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                        <!-- Kind: "pdf"/"zip", a quiet tag rather than a status
                             — SubtleText, with its own copy of the let-selection-win
                             trigger, for the reason ZipToolsWindow.xaml records. -->
                        <DataGridTextColumn Header="Kind" Binding="{Binding Kind}" Width="Auto">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource SubtleText}">
                                    <Style.Triggers>
                                        <DataTrigger Value="True">
                                            <DataTrigger.Binding>
                                                <Binding Path="IsSelected"
                                                         RelativeSource="{RelativeSource AncestorType=DataGridCell}" />
                                            </DataTrigger.Binding>
                                            <Setter Property="Foreground"
                                                    Value="{DynamicResource Theme.AccentText}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                        <!-- Result: content-sized, capped from the code-behind
                             by DataGridColumnCap. Error is Theme.StatusRed — a
                             genuine failure. NoPdfs and NeedsPassword are
                             Theme.StatusAmber — "needs attention", not done and
                             not broken: nothing failed, there was just nothing to
                             merge, or a password nobody knew yet
                             (status-colour-vocabulary plan, 2026-08-08). Driven
                             off StatusKind, an enum, hence {x:Static}. -->
                        <DataGridTextColumn x:Name="ResultColumn" Header="Result" Binding="{Binding Note}"
                                            Width="Auto">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellText}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Note}" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding StatusKind}"
                                                     Value="{x:Static vm:ZipItemRowStatus.Error}">
                                            <Setter Property="Foreground" Value="{DynamicResource Theme.StatusRed}" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding StatusKind}"
                                                     Value="{x:Static vm:ZipItemRowStatus.NoPdfs}">
                                            <Setter Property="Foreground" Value="{DynamicResource Theme.StatusAmber}" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding StatusKind}"
                                                     Value="{x:Static vm:ZipItemRowStatus.NeedsPassword}">
                                            <Setter Property="Foreground" Value="{DynamicResource Theme.StatusAmber}" />
                                        </DataTrigger>
                                        <!-- Declared last so it wins over the
                                             triggers above once selected:
                                             GridCellText's own copy of this
                                             trigger does not reliably win once a
                                             style adds triggers of its own. -->
                                        <DataTrigger Value="True">
                                            <DataTrigger.Binding>
                                                <Binding Path="IsSelected"
                                                         RelativeSource="{RelativeSource AncestorType=DataGridCell}" />
                                            </DataTrigger.Binding>
                                            <Setter Property="Foreground"
                                                    Value="{DynamicResource Theme.AccentText}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                    </DataGrid.Columns>
                </DataGrid>
                <TextBlock Text="Drag PDFs or zips anywhere on this window, or click Add PDFs or zips…"
                           Style="{StaticResource EmptyStateText}"
                           IsHitTestVisible="False">
                    <TextBlock.Visibility>
                        <Binding Path="Rows.Count" Converter="{StaticResource ZeroToVis}" />
                    </TextBlock.Visibility>
                </TextBlock>
            </Grid>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 4: Create `MergePdfsWindow.xaml.cs`**

```csharp
using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class MergePdfsWindow : Window
{
    private readonly MergePdfsViewModel _vm;

    public MergePdfsWindow(MergePdfsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ItemsGrid, ResultColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PDFs and zip archives (*.pdf;*.zip)|*.pdf;*.zip|PDF files (*.pdf)|*.pdf|Zip archives (*.zip)|*.zip",
            Multiselect = true,
        };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e) => AcceptDrop(e.Data);

    /// <summary>The one list a drop can reach. Internal so the window test
    /// can hand it a DataObject and count the row, without a real drag.</summary>
    internal void AcceptDrop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddPaths(paths);
    }

    /// <summary>A closed window must not keep working invisibly: the work is
    /// async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
```

- [ ] **Step 5: Run the pin to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MergePdfsWindowTests" -v minimal`
Expected: `Passed: 1`.

- [ ] **Step 6: The Tools entry and its handler**

In `src/OrdoSort.Wpf/MainWindow.xaml`, directly after the `_Zip and unzip…` `MenuItem` (the one with `Text="&#xE8B7;"`), inside the Tools menu:

```xml
                <MenuItem Header="Merge _PDFs…" Click="OnMergePdfs">
                    <MenuItem.Icon><TextBlock Style="{StaticResource Icon}" Text="&#xE8A5;" /></MenuItem.Icon>
                </MenuItem>
```

(`P`, not `M`: `M` would collide with `_Match and merge…`, the app's other merge tool and a genuinely confusable neighbour.)

In `src/OrdoSort.Wpf/MainWindow.xaml.cs`, directly after `OnZipTools`:

```csharp
    private void OnMergePdfs(object sender, RoutedEventArgs e) =>
        new Windows.MergePdfsWindow(new MergePdfsViewModel(Dialogs, SavedPasswordsNow(),
            uiContext: SynchronizationContext.Current))
        { Owner = this }.ShowDialog();

    /// <summary>The Unlock tool's saved passwords, revealed, for the two
    /// windows that try them silently before asking anyone — read once as
    /// each window opens, through the same PasswordVault path Unlock uses.
    /// A legacy DPAPI entry this machine cannot decrypt reveals as "" and is
    /// skipped, not reported: Unlock already owns that conversation.</summary>
    private IReadOnlyList<string> SavedPasswordsNow() =>
        Shell.Cfg.SavedPasswords
            .Select(saved => PasswordVault.Reveal(saved.Password))
            .Where(password => password.Length > 0)
            .ToList();
```

- [ ] **Step 7: Register the window in `DataGridWindowCoverageTests` and `DataGridSizingCoverageTests`**

`tests/OrdoSort.Wpf.Tests/DataGridWindowCoverageTests.cs`, `CoveredWindows`: append `"MergePdfsWindow"` to the last line so it reads

```csharp
        "FilenameListWindow", "PageCountsWindow", "ZipToolsWindow", "MergePdfsWindow",
```

and in the comment above it, replace `Zip, Unzip and Merge PDFs from zip became ZipToolsWindow's two tabs on 2026-08-18; its builder takes a tab flag and covers both grids' columns, so one entry here stands for what used to be three.` with `Merge PDFs got its own window on 2026-08-28 (the 2026-08-25 spec: a tab is a weak drop target), so ZipToolsWindow and MergePdfsWindow are two entries with two builders.` In the sanity-check comment near line 135, raise the window-type count by two (PasswordWindow and MergePdfsWindow are both public Window subtypes now) and the DataGrid-declaring count by one, and add `MergePdfsWindow` to the parenthetical list of grid windows.

`tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs`, `SizingCovered`: the second line becomes

```csharp
        "PageCountsWindow", "ZipToolsWindow", "MergePdfsWindow",
```

Both entries are claims that coverage exists; Steps 8–10 make them true.

- [ ] **Step 8: Register the window in `AutoFitColumnTests`**

After `BuildZipToolsWindow` add:

```csharp
    private static MergePdfsWindow BuildMergePdfsWindow(string resultValue, int rowCount = 1)
    {
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new ZipItemRow($@"C:\inbox\f{i}.zip", "zip");
            row.Apply(new PdfMerge.MergeResult(row.Path, "error", Message: resultValue));
            vm.Rows.Add(row);
        }
        return new MergePdfsWindow(vm);
    }
```

After the `ZipToolsMergeTab_AtMinWidthNoHorizontalScrollbar` fact add:

```csharp
    [Fact]
    public void MergePdfs_ShortResultValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildMergePdfsWindow(ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            Assert.True(column.ActualWidth < 100,
                $"Merge PDFs Result column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MergePdfs_LongResultValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildMergePdfsWindow(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            AssertStoppedAtItsCap(win, column, "Merge PDFs Result");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Note");
        }
        finally { win.Close(); }
    });

    /// <summary>Unlike the old Merge tab's grid, this one has a Kind column
    /// competing with the filler, so removing the code-behind's
    /// DataGridColumnCap.Track call genuinely produces a scrollbar here —
    /// the same shape as PageCounts_AtMinWidthNoHorizontalScrollbar.</summary>
    [Fact]
    public void MergePdfs_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMergePdfsWindow(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"Merge PDFs (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });
```

- [ ] **Step 9: Register the window in `DataGridSelectionContrastTests`**

After `BuildZipToolsWindow` add:

```csharp
    private static (MergePdfsWindow win, DataGrid grid) BuildMergePdfsWindow()
    {
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
        var row = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(row.Path, "error",
            Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
        vm.Rows.Add(row);
        var win = new MergePdfsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under MergePdfsWindow");
        Assert.Same(vm.Rows, grid.ItemsSource);
        return (win, grid);
    }
```

After `ZipToolsMergeTabAllColumnsClearContrast` add:

```csharp
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MergePdfsAllColumnsClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildMergePdfsWindow();
        try
        {
            AssertEverySelectedColumnClearsContrast(grid, p, "MergePdfsWindow");
            AssertEveryUnselectedColumnClearsContrast(grid, p, "MergePdfsWindow");
        }
        finally { win.Close(); }
    });
```

- [ ] **Step 10: Register the window in `DataGridNoteColourTests`**

After `ZipToolsNoPdfsResultIsAmberUnlessSelected` add:

```csharp
    // ------------------------------------------------------- Merge PDFs
    //
    // Its own window since 2026-08-28. Three statuses, two colours: Error is
    // Theme.StatusRed (a genuine failure); NoPdfs and NeedsPassword are
    // Theme.StatusAmber — "needs attention", not done and not broken.

    private void AssertMergePdfsResultColour(string schemeKey, bool selected,
        string status, Func<ThemePalette, Rgb> expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
        var row = new ZipItemRow(@"C:\inbox\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(row.Path, status, Message: "some result text here"));
        vm.Rows.Add(row);

        var window = new MergePdfsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under MergePdfsWindow");
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Result");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"Merge PDFs Result selected, {status} ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                var expected = expectedUnselected(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"Merge PDFs Result unselected, {status} ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MergePdfsErrorResultIsRedUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMergePdfsResultColour(schemeKey, selected, "error", p => p.StatusRed));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MergePdfsNoPdfsResultIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMergePdfsResultColour(schemeKey, selected, "no_pdfs", p => p.StatusAmber));

    /// <summary>The new status. Amber, like NoPdfs: a password nobody knew
    /// yet is "needs attention" — the row is still runnable — not a failure.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MergePdfsNeedsPasswordResultIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMergePdfsResultColour(schemeKey, selected, "needs_password", p => p.StatusAmber));
```

- [ ] **Step 11: Register the window in `WindowOverflowTests`**

After the `["ZipToolsWindow"]` entry add:

```csharp
        // A failed zip and a locked PDF: the Result column's messages are the
        // widest thing this grid ever shows.
        ["MergePdfsWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
            var toMerge = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
            toMerge.Apply(new PdfMerge.MergeResult(toMerge.Path, "error",
                Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
            vm.Rows.Add(toMerge);
            var locked = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf", "pdf");
            locked.Mark(ZipItemRowStatus.NeedsPassword, "needs a password");
            vm.Rows.Add(locked);
            return (new MergePdfsWindow(vm), null);
        }, MinExamined: 9999),
```

Then measure: run `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~WindowOverflowTests" -v minimal`, read `the probe examined N elements` from the MergePdfsWindow failure, set `MinExamined` to three quarters of N rounded up with `// N measured` beside it.

- [ ] **Step 12: Run the registries, then the full check**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~AutoFitColumnTests|FullyQualifiedName~DataGridSelectionContrastTests|FullyQualifiedName~DataGridNoteColourTests|FullyQualifiedName~WindowOverflowTests|FullyQualifiedName~AccessibleNameTests|FullyQualifiedName~DataGridWindowCoverageTests|FullyQualifiedName~DataGridSizingCoverageTests|FullyQualifiedName~HeaderLayoutTests" -v minimal`
Expected: `Failed: 0`. (`HeaderLayoutTests` is in the list because the Tools menu grew an item — its counts are of top-level menus, which did not change, so it passes; it is here to prove that.)

Then the full check.

- [ ] **Step 13: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml.cs src/OrdoSort.Wpf/MainWindow.xaml src/OrdoSort.Wpf/MainWindow.xaml.cs tests/OrdoSort.Wpf.Tests/MergePdfsWindowTests.cs tests/OrdoSort.Wpf.Tests/DataGridWindowCoverageTests.cs tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs tests/OrdoSort.Wpf.Tests/DataGridSelectionContrastTests.cs tests/OrdoSort.Wpf.Tests/DataGridNoteColourTests.cs tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs
git commit -m "feat(ui): Merge PDFs, as a window of its own

The 2026-08-25 spec's finding: a zip dropped on the two-tab window landed
in whichever tab was selected, silently, and both tabs accepted it. A tab
is a weak drop target; a window cannot be dropped past. MergePdfsWindow is
that window — one list, PDFs and zips, Merge and Merge to… in the footer —
and Merge PDFs… joins the Tools menu directly below Zip and unzip (P, not
M: M is Match and merge, the app's other merge tool). Both handlers hand
their view model the Unlock tool's saved passwords, revealed once at
open.

Registered in every suite that enumerates windows, so it gets the
auto-fit, selection-contrast, note-colour, overflow and accessible-name
coverage a first-class window gets. The old tab survives until the next
commit removes it: no commit on this branch loses the feature."
```

---

### Task 9: `ZipToolsWindow` loses its tabs; `ZipToolsViewModel` goes

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml` (rewrite), `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs` (rewrite)
- Delete: `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs`
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml.cs` (`OnZipTools`)
- Modify (registries): `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs`, `DataGridSelectionContrastTests.cs`, `DataGridNoteColourTests.cs`, `WindowOverflowTests.cs`, `DataGridWindowCoverageTests.cs` (comments), `DataGridSizingCoverageTests.cs` (comment)
- Modify (E2E): `tools/OrdoSort.Smoke/E2E/Scenarios/ScenarioKit.cs`, `ZipScenarios.cs`, `UnzipScenarios.cs`, `ZipMergeScenarios.cs`
- Test: `tests/OrdoSort.Wpf.Tests/ZipToolsWindowTests.cs` (rewrite)

**Interfaces:**
- Consumes: `ZipExtractViewModel` (Task 6), `MergePdfsWindow` and `MainWindow.SavedPasswordsNow()` (Task 8).
- Produces: `ZipToolsWindow(ZipExtractViewModel vm)`; `internal void ZipToolsWindow.AcceptDrop(IDataObject data)`; `ScenarioKit.Drained(int timeoutMs = 15000)` — the wait every zip scenario uses from here on.

- [ ] **Step 1: Rewrite `ZipToolsWindowTests` — the pin, on this window too**

Replace the entire content of `tests/OrdoSort.Wpf.Tests/ZipToolsWindowTests.cs` with:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The 2026-08-25 spec's regression pin, on the Zip and unzip
/// window: zero TabControls, exactly one DataGrid, and a FileDrop lands one
/// row in that one list. The test this file used to hold —
/// FooterActionsFollowTheSelectedTab — is deleted, not ported: it guarded
/// the footer-swapping machinery the tab split needed, and both the
/// machinery and its guard go together.</summary>
[Collection(HighlightContrastTests.Name)]
public class ZipToolsWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public ZipToolsWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static ZipExtractViewModel QuietVm() =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));

    [Fact]
    public void OneListNoTabsAndADroppedZipLandsInIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var vm = QuietVm();
        var window = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            var content = (DependencyObject)window.Content;
            Assert.Empty(Descendants<TabControl>(content));
            var grid = Assert.Single(Descendants<DataGrid>(content));
            Assert.Same(vm.Rows, grid.ItemsSource);

            window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { zip }));

            Assert.Equal(zip, Assert.Single(vm.Rows).Path);
        }
        finally { window.Close(); }
    });

    /// <summary>All three actions, all showing, all bound to the one view
    /// model — the footer no longer swaps with anything.</summary>
    [Fact]
    public void TheFooterHoldsZipZipToAndExtractForTheOneList() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = QuietVm();
        var window = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            var buttons = Descendants<Button>((DependencyObject)window.Content).ToList();
            // Identified by the command instance rather than by label: the
            // labels are bound and count-dependent ("Zip", "Zip 2 items").
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ZipCommand)).IsVisible);
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ZipAsCommand)).IsVisible);
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ExtractCommand)).IsVisible);
        }
        finally { window.Close(); }
    });
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipToolsWindowTests" -v minimal`
Expected: build FAILS — `ZipToolsWindow` has no constructor taking `ZipExtractViewModel`, and no `AcceptDrop`.

- [ ] **Step 3: De-tab `ZipToolsWindow.xaml`**

Replace the entire file with:

```xml
<Window x:Class="OrdoSort.Wpf.Windows.ZipToolsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:OrdoSort.Wpf.ViewModels"
        Title="OrdoSort — Zip and unzip" Width="700" Height="520" MinWidth="580" MinHeight="420"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False" AllowDrop="True"
        DragOver="OnDragOver" Drop="OnDrop"
        Style="{StaticResource {x:Type Window}}">
    <!-- One job, one list, window-level drop. Merge PDFs was this window's
         second tab until 2026-08-28; a zip dropped on the window landed in
         whichever tab was selected, silently, and both tabs accepted it. A
         tab is a weak drop target; a window cannot be dropped past
         (2026-08-25 spec). -->
    <DockPanel Margin="14">
        <DockPanel DockPanel.Dock="Bottom" Margin="0,10,0,0">
            <Button DockPanel.Dock="Right" Content="Close" Width="96" IsCancel="True" />
            <StackPanel Orientation="Horizontal">
                <Button Command="{Binding ZipCommand}"
                        Style="{StaticResource PrimaryButton}" MinWidth="110" Margin="0,0,8,0"
                        AutomationProperties.Name="{Binding ZipButtonText}">
                    <TextBlock Text="{Binding ZipButtonText}"
                               Style="{StaticResource PrimaryButtonLabel}" />
                </Button>
                <Button Content="Zip to…" Command="{Binding ZipAsCommand}" Margin="0,0,8,0" />
                <Button Command="{Binding ExtractCommand}" MinWidth="120" Margin="0,0,10,0"
                        AutomationProperties.Name="{Binding ExtractButtonText}">
                    <TextBlock Text="{Binding ExtractButtonText}" />
                </Button>
                <!-- MaxWidth is load-bearing, not cosmetic: StatusText
                     carries TextWrapping="Wrap" (Theme/Styles.xaml), and a
                     horizontal StackPanel measures every child at infinite
                     width, so without a finite cap the wrap never engages
                     and the line runs off screen — the failure
                     TextWrapCoverageTests exists to catch. -->
                <TextBlock Text="{Binding Status}" VerticalAlignment="Center"
                           Style="{StaticResource StatusText}" MaxWidth="360" />
            </StackPanel>
        </DockPanel>

        <Grid Margin="16,8">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <DockPanel Margin="0,0,0,10">
                <TextBlock DockPanel.Dock="Right" Text="{Binding AddNote}"
                           Style="{StaticResource CaptionText}" VerticalAlignment="Center"
                           MaxWidth="240" TextTrimming="CharacterEllipsis" />
                <!-- WrapPanel, not a horizontal StackPanel: five buttons and
                     the note do not fit this window's 580 MinWidth at the 18px
                     font preset, and a StackPanel would push the last button
                     off screen rather than move it to a second row. The 4
                     bottom margin is the gap that second row needs. -->
                <WrapPanel>
                    <Button Content="Add files…" Click="OnAddFiles" Margin="0,0,6,4" />
                    <Button Content="Add folder…" Click="OnAddFolder" Margin="0,0,6,4" />
                    <Button Content="Add zips…" Click="OnAddZips" Margin="0,0,6,4" />
                    <Button Content="Remove selected" Click="OnRemoveSelected" Margin="0,0,6,4"
                            IsEnabled="{Binding IsIdle}" />
                    <Button Content="Clear" Command="{Binding ClearCommand}" Margin="0,0,10,4" />
                </WrapPanel>
            </DockPanel>

            <Grid Grid.Row="1">
                <DataGrid x:Name="ItemsGrid" ItemsSource="{Binding Rows}"
                          AutomationProperties.Name="Files, folders and archives to process" AutoGenerateColumns="False"
                          IsReadOnly="True" CanUserAddRows="False" HeadersVisibility="Column"
                          SelectionMode="Extended">
                    <!-- Theme.RowHover so the hover tint stays consistent
                         app-wide; the row ToolTip carries the full path,
                         which no column shows. -->
                    <DataGrid.RowStyle>
                        <Style TargetType="DataGridRow">
                            <Setter Property="Background" Value="Transparent" />
                            <Setter Property="ToolTip" Value="{Binding Path}" />
                            <Style.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter Property="Background"
                                            Value="{DynamicResource Theme.RowHover}" />
                                </Trigger>
                            </Style.Triggers>
                        </Style>
                    </DataGrid.RowStyle>
                    <DataGrid.Columns>
                        <!-- Item: the filler (Width="*") — the added file,
                             folder or archive's own name. No status trigger of
                             its own, so GridCellTextSelectionAware's shared
                             default plus its trailing "let selection win"
                             DataTrigger is the whole style; plain GridCellText
                             carries no such trigger, and measured off-screen
                             without one, Theme.Text on Theme.Accent ran
                             1.26:1-2.14:1 across every scheme, nowhere near the
                             4.5:1 floor. MinWidth is load-bearing arithmetic,
                             not a nicety: DataGridColumnCap computes the Result
                             cap as the viewport minus everyone else's floor, so
                             without one a long error message squeezes this
                             column to WPF's 20px default. See
                             PageCountsWindow.xaml. -->
                        <DataGridTextColumn Header="Item" Binding="{Binding Display}" Width="*" MinWidth="180">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Path}" />
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                        <!-- Kind: content-sized, "file"/"pdf"/"folder"/"zip" — a
                             quiet tag rather than a status, so SubtleText
                             rather than GridCellText. SubtleText's own
                             unconditional Foreground Setter outranks whatever
                             this TextBlock would INHERIT from its ancestor
                             DataGridCell, the Accent/AccentText pair the cell's
                             IsSelected trigger paints included: measured
                             off-screen without the trailing DataTrigger below,
                             Theme.SubtleText on Theme.Accent ran 1.07:1-1.85:1
                             across every scheme — a selected row's own tag
                             unreadable against its own highlight. SubtleText is
                             a different base from GridCellText, so this cannot
                             delegate to GridCellTextSelectionAware and carries
                             its own copy of that trigger. -->
                        <DataGridTextColumn Header="Kind" Binding="{Binding Kind}" Width="Auto">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource SubtleText}">
                                    <Style.Triggers>
                                        <DataTrigger Value="True">
                                            <DataTrigger.Binding>
                                                <Binding Path="IsSelected"
                                                         RelativeSource="{RelativeSource AncestorType=DataGridCell}" />
                                            </DataTrigger.Binding>
                                            <Setter Property="Foreground"
                                                    Value="{DynamicResource Theme.AccentText}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                        <!-- Result: content-sized, capped from the code-behind
                             by DataGridColumnCap. Error is Theme.StatusRed — a
                             genuine failure, not "needs attention", so it must
                             not borrow Theme.StatusAmber. NeedsPassword IS
                             "needs attention" — a password nobody knew yet,
                             and the row is still runnable — so it is amber
                             (status-colour-vocabulary plan, 2026-08-08). NoPdfs
                             is unreachable on this window; only a merge
                             produces it. -->
                        <DataGridTextColumn x:Name="ItemsResultColumn" Header="Result" Binding="{Binding Note}"
                                            Width="Auto">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellText}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Note}" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding StatusKind}"
                                                     Value="{x:Static vm:ZipItemRowStatus.Error}">
                                            <Setter Property="Foreground" Value="{DynamicResource Theme.StatusRed}" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding StatusKind}"
                                                     Value="{x:Static vm:ZipItemRowStatus.NeedsPassword}">
                                            <Setter Property="Foreground" Value="{DynamicResource Theme.StatusAmber}" />
                                        </DataTrigger>
                                        <!-- Declared last so it wins over the
                                             triggers above once selected:
                                             GridCellText's own copy of this
                                             trigger does not reliably win once a
                                             style adds triggers of its own. -->
                                        <DataTrigger Value="True">
                                            <DataTrigger.Binding>
                                                <Binding Path="IsSelected"
                                                         RelativeSource="{RelativeSource AncestorType=DataGridCell}" />
                                            </DataTrigger.Binding>
                                            <Setter Property="Foreground"
                                                    Value="{DynamicResource Theme.AccentText}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                    </DataGrid.Columns>
                </DataGrid>
                <TextBlock Text="Drag files, folders or zips anywhere on this window, or click Add files…"
                           Style="{StaticResource EmptyStateText}"
                           IsHitTestVisible="False">
                    <TextBlock.Visibility>
                        <Binding Path="Rows.Count" Converter="{StaticResource ZeroToVis}" />
                    </TextBlock.Visibility>
                </TextBlock>
            </Grid>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 4: Rewrite `ZipToolsWindow.xaml.cs`**

```csharp
using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class ZipToolsWindow : Window
{
    private readonly ZipExtractViewModel _vm;

    public ZipToolsWindow(ZipExtractViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ItemsGrid, ItemsResultColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(new[] { dlg.FolderName });
    }

    private void OnAddZips(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip archives (*.zip)|*.zip", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e) => AcceptDrop(e.Data);

    /// <summary>The one list a drop can reach. Internal so the window test
    /// can hand it a DataObject and count the row, without a real drag.</summary>
    internal void AcceptDrop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddPaths(paths);
    }

    /// <summary>A closed window must not keep working invisibly: the work is
    /// async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
```

- [ ] **Step 5: Delete `ZipToolsViewModel` and update its last production caller**

```bash
git rm src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs
```

In `src/OrdoSort.Wpf/MainWindow.xaml.cs`, `OnZipTools` becomes:

```csharp
    private void OnZipTools(object sender, RoutedEventArgs e) =>
        new Windows.ZipToolsWindow(new ZipExtractViewModel(Dialogs, SavedPasswordsNow(),
            uiContext: SynchronizationContext.Current))
        { Owner = this }.ShowDialog();
```

- [ ] **Step 6: Run the pin to verify it passes**

Run: `dotnet build src/OrdoSort.Wpf -v quiet` — expected `0 Error(s)` (the test projects and the smoke tool still reference `ZipToolsViewModel`; the next steps fix each). Then run the pin once the registries below compile.

- [ ] **Step 7: Retarget `AutoFitColumnTests`**

Replace `BuildZipToolsWindow(bool mergeTab, string resultValue, int rowCount = 1)` and its doc comment with:

```csharp
    private static ZipToolsWindow BuildZipToolsWindow(string resultValue, int rowCount = 1)
    {
        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new ZipItemRow($@"C:\inbox\f{i}.zip", "zip");
            row.Apply(new Zipper.UnzipResult(row.Path, "error", null, resultValue));
            vm.Rows.Add(row);
        }
        return new ZipToolsWindow(vm);
    }
```

Delete `ShowOffscreenOnTab` and its doc comment entirely. Delete the three `ZipToolsMergeTab_*` facts and their doc comments (Task 8's `MergePdfs_*` facts are that grid on its own window). Rename the three `ZipToolsZipTab_*` facts to `ZipTools_ShortResultValueMeasuresNarrow`, `ZipTools_LongResultValueStopsAtTheCapWithEllipsisAndTooltip`, `ZipTools_AtMinWidthNoHorizontalScrollbar`; in each, `BuildZipToolsWindow(mergeTab: false, X)` becomes `BuildZipToolsWindow(X)`, `ShowOffscreenOnTab(win, mergeTab: false)` becomes `ShowOffscreen(win)`, and `ShowOffscreenOnTab(win, mergeTab: false, win.MinWidth)` becomes `ShowOffscreenAtWidth(win, win.MinWidth)`; the labels lose "tab" (`"Zip and unzip Result"`, `$"Zip and unzip (at MinWidth {win.MinWidth}, {ManyRowCount} rows)"`). In the comment block that begins `// The Merge-PDFs-from-zip and Unzip windows became ZipToolsWindow's two`, replace those seven lines with:

```csharp
    // Merge PDFs got its own window on 2026-08-28 (the 2026-08-25 spec: a
    // tab is a weak drop target). Each window has one grid with one capped
    // Result column, and each has its own facts below.
```

- [ ] **Step 8: Retarget `DataGridSelectionContrastTests`**

Replace `BuildZipToolsWindow(bool mergeTab)` and its doc comment with:

```csharp
    private static (ZipToolsWindow win, DataGrid grid) BuildZipToolsWindow()
    {
        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>());
        var row = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
        row.Apply(new Zipper.UnzipResult(row.Path, "error", null,
            "not a valid zip archive — a long enough exception message to matter"));
        vm.Rows.Add(row);
        var win = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under ZipToolsWindow");
        Assert.Same(vm.Rows, grid.ItemsSource);
        return (win, grid);
    }
```

Delete `ZipToolsMergeTabAllColumnsClearContrast`. Rename `ZipToolsZipTabAllColumnsClearContrast` to `ZipToolsAllColumnsClearContrast`, calling `BuildZipToolsWindow()` with the label `"ZipToolsWindow"`, and drop its "both tabs" doc comment. In the comment block above the builders, replace `Three of them (Zip, Unzip, Merge PDFs from zip) became ZipToolsWindow's two tabs on 2026-08-18; one builder with a tab flag stands for what used to be three.` with `Zip and Unzip became ZipToolsWindow on 2026-08-18; Merge PDFs got its own window on 2026-08-28 (BuildMergePdfsWindow below).`

- [ ] **Step 9: Retarget `DataGridNoteColourTests`**

Delete `PalettesSelectionAndTab`. Replace `AssertZipToolsResultColour(string schemeKey, bool selected, bool mergeTab, string status, Func<ThemePalette, Rgb> expectedUnselected)` and its doc comment with:

```csharp
    /// <summary>Builds the real window with one ZipItemRow driven through its
    /// own internal Apply, reads the Result cell's actual TextBlock, and
    /// asserts against the vocabulary — or, once selected, AccentText
    /// regardless of status (selection wins).</summary>
    private void AssertZipToolsResultColour(string schemeKey, bool selected,
        string status, Func<ThemePalette, Rgb> expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>());
        var row = new ZipItemRow(@"C:\inbox\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(row.Path, status, null, "some result text here"));
        vm.Rows.Add(row);

        var window = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under ZipToolsWindow");
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Result");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"Zip and unzip Result selected, {status} ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                var expected = expectedUnselected(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"Zip and unzip Result unselected, {status} ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void ZipToolsErrorResultIsRedUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertZipToolsResultColour(schemeKey, selected, "error", p => p.StatusRed));

    /// <summary>A locked archive nobody could open is "needs attention" —
    /// the row is still runnable — not a failure: amber, never red.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void ZipToolsNeedsPasswordResultIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertZipToolsResultColour(schemeKey, selected, "needs_password", p => p.StatusAmber));
```

Delete the old `ZipToolsErrorResultIsRedUnlessSelected(string, bool, bool)` theory and `ZipToolsNoPdfsResultIsAmberUnlessSelected` (Task 8's `MergePdfsNoPdfsResultIsAmberUnlessSelected` is that case on its own window). In the section comment, replace the paragraph beginning `// Both of those windows became ZipToolsWindow's two tabs on 2026-08-18,` through `// Merge tab alone.` with:

```csharp
    // Merge PDFs got its own window on 2026-08-28. Each window's Result
    // column is its own XAML declaration with its own trigger set — Zip and
    // unzip carries Error and NeedsPassword, Merge PDFs those two plus
    // NoPdfs — so each is measured on its own window below.
```

- [ ] **Step 10: Retarget `WindowOverflowTests`**

Replace the `["ZipToolsWindow"]` entry and the comments above it with:

```csharp
        // A failed zip, a locked zip and a loose PDF: the Result column's
        // messages are the widest thing this grid ever shows.
        ["ZipToolsWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>());
            var archive = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
            archive.Apply(new Zipper.UnzipResult(archive.Path, "error", null,
                "not a valid zip archive — a long enough exception message to matter"));
            vm.Rows.Add(archive);
            var locked = new ZipItemRow(@"C:\inbox\another-long-enough-filename-to-matter.zip", "zip");
            locked.Mark(ZipItemRowStatus.NeedsPassword, "needs a password");
            vm.Rows.Add(locked);
            vm.Rows.Add(new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf", "pdf"));
            return (new ZipToolsWindow(vm), null);
        }, MinExamined: 9999),
```

Then re-measure `MinExamined` exactly as in Task 8 Step 11 — the old floor of 38 was the sum across two probed tabs and would fail now.

- [ ] **Step 11: Update the two coverage comments**

`tests/OrdoSort.Wpf.Tests/DataGridWindowCoverageTests.cs`: in the sanity-check comment (around line 135), `Zip/Unzip/ZipMerge became ZipToolsWindow's two tabs` becomes `Zip/Unzip became ZipToolsWindow and ZipMerge became MergePdfsWindow`. `tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs:63`: `ZipToolsWindow stands for what used to be ZipMergeWindow and` — rewrite that sentence so `ZipToolsWindow` stands for `ZipWindow`/`UnzipWindow` and `MergePdfsWindow` for `ZipMergeWindow`.

- [ ] **Step 12: Retarget the E2E scenarios**

In `tools/OrdoSort.Smoke/E2E/Scenarios/ScenarioKit.cs`, add after `Settle`:

```csharp
    /// <summary>Wait until everything the run POSTED to the dispatcher has
    /// run. Under InlineScheduler a command's whole body runs synchronously
    /// up to the Posts it issues — the probe's verdict on add, each unit's
    /// Apply, the final OnRowsChanged — and none of those has landed when
    /// Execute returns. A sentinel posted after them sits behind all of
    /// them (DispatcherSynchronizationContext.Post is FIFO within a
    /// priority), so waiting on it is waiting on the row state the
    /// assertions read. This replaces waiting on Rows[0].Note, which the
    /// probe on add now fills BEFORE the run and which would therefore
    /// settle too early — exactly the already-true-predicate trap the class
    /// doc comment above describes.</summary>
    public static bool Drained(int timeoutMs = 15000)
    {
        var done = false;
        SynchronizationContext.Current!.Post(_ => done = true, null);
        return E2EPump.Until(() => done, timeoutMs);
    }
```

In `ZipScenarios.cs`:

```csharp
    private static ZipExtractViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, Array.Empty<string>(), new InlineScheduler(), SynchronizationContext.Current);

    /// <summary>Opens the real window: one list, no tab to select.</summary>
    private static ZipToolsWindow Open(ZipExtractViewModel vm)
    {
        var win = new ZipToolsWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }
```

and in each of the six scenarios replace the three lines

```csharp
        var tools = NewVm(ctx);
        var win = Open(tools);
        var vm = tools.ZipExtract;
```

with

```csharp
        var vm = NewVm(ctx);
        var win = Open(vm);
```

(`EmptySelection` has the same three lines without a fixture above them.) The class doc comment's "Zip &amp; unzip tab" becomes "window".

In `UnzipScenarios.cs`: the same `NewVm` and `Open` as above; `Extract` becomes

```csharp
    /// <summary>Drive one archive through the window and wait for every
    /// result the run posted to land — see ScenarioKit.Drained for why the
    /// old wait on Rows[0].Note settles too early now that the probe on add
    /// fills the note before the run.</summary>
    private static ZipToolsWindow Extract(ScenarioContext ctx, ZipExtractViewModel vm, string zip)
    {
        var win = Open(vm);
        _ = vm.AddPaths(new[] { zip });   // synchronous under InlineScheduler — see ZipScenarios
        ctx.Check("the archive is listed", vm.Rows.Count == 1, $"got {vm.Rows.Count}");
        vm.ExtractCommand.Execute(null);
        ctx.Check("the window applied every result", Drained(), "the dispatcher queue never drained");
        return win;
    }
```

and in each of the five scenarios `var tools = NewVm(ctx); var win = Extract(ctx, tools, zip); var vm = tools.ZipExtract;` becomes `var vm = NewVm(ctx); var win = Extract(ctx, vm, zip);`. The `ZipSlip` scenario's doc comment sentence `ZipFile.ExtractToDirectory throws IOException for this, which ExtractCore turns into an error result` becomes `Zipper's own path guard refuses it (the SharpZipLib move, 2026-08-28), which ExtractCore turns into an error result`.

In `ZipMergeScenarios.cs`:

```csharp
    private static MergePdfsViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, Array.Empty<string>(), new InlineScheduler(), SynchronizationContext.Current);

    /// <summary>Add every source, run the merge, and wait for every result
    /// the run posted to land (ScenarioKit.Drained — the old wait on "every
    /// row left Pending" can no longer end: fail-whole leaves the rows a
    /// culprit held back Pending on purpose).</summary>
    private static MergePdfsWindow Merge(ScenarioContext ctx, MergePdfsViewModel vm, params string[] sources)
    {
        var win = new MergePdfsWindow(vm);
        E2EPump.ShowOffscreen(win);

        _ = vm.AddPaths(sources);   // synchronous under InlineScheduler — see ZipScenarios
        ctx.Check("every source is listed", vm.Rows.Count == sources.Length,
            $"got {vm.Rows.Count} of {sources.Length}");

        vm.MergeCommand.Execute(null);
        ctx.Check("the window applied every result", Drained(), "the dispatcher queue never drained");
        return win;
    }
```

with `using OrdoSort.Smoke.E2E.Scenarios;` replaced by `using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;` if `Drained` does not resolve. In each scenario `var tools = NewVm(ctx); var win = Merge(ctx, tools, …); var vm = tools.MergePdfs;` becomes `var vm = NewVm(ctx); var win = Merge(ctx, vm, …);`. `EncryptedInside`'s expectation moves with the behaviour — replace its doc comment and body with:

```csharp
    /// <summary>An encrypted document inside, and no password anyone knows:
    /// PdfMerge.MergeZip asks (ScriptedDialogs, nothing queued, answers
    /// null — a skip) and fails the WHOLE zip as needs_password, naming the
    /// entry, with no output — fail-whole is unchanged, but the row is
    /// runnable rather than a dead end. Task 10 adds the sibling scenario
    /// where the password is supplied.</summary>
    private static void EncryptedInside(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("reported as needing a password rather than a silent partial success",
            vm.Rows[0].StatusKind == ZipItemRowStatus.NeedsPassword,
            $"status was {vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("the outcome names the entry that could not be read",
            vm.Rows[0].Note.Contains("locked.pdf", StringComparison.Ordinal),
            $"note was \"{vm.Rows[0].Note}\"");
        ctx.Check("a failed merge writes nothing", vm.Rows[0].Output is null,
            $"wrote {vm.Rows[0].Output}");
        ctx.Check("the row is still runnable", vm.Rows[0].IsRunnable, "it was finished");
        ctx.Capture(win);
    }
```

- [ ] **Step 13: Run everything**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipToolsWindowTests|FullyQualifiedName~AutoFitColumnTests|FullyQualifiedName~DataGridSelectionContrastTests|FullyQualifiedName~DataGridNoteColourTests|FullyQualifiedName~WindowOverflowTests|FullyQualifiedName~AccessibleNameTests|FullyQualifiedName~DataGridWindowCoverageTests|FullyQualifiedName~DataGridSizingCoverageTests" -v minimal` — expected `Failed: 0`.

Then the full check (which also builds the smoke tool). Then the three E2E surfaces, from the repo root:

```
e2e.bat zip
e2e.bat unzip
e2e.bat zipmerge
```

Read each run's report: every scenario `PASS`, and `Unconsumed` empty. A `FAIL` naming "the dispatcher queue never drained" means a scenario's view model was built without `SynchronizationContext.Current` — check `NewVm`.

- [ ] **Step 14: Commit**

```bash
git add -A src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests tools/OrdoSort.Smoke
git commit -m "refactor(ui): one list per zip window — the TabControl and its shell go

The 2026-08-25 spec's second half. ZipToolsWindow is what its first tab
was: one list, files and folders and archives, Zip and Zip to… and Extract
in the footer, an Add zips… button beside Add files…, and a window-level
drop that can only land in that list. ZipToolsViewModel — a shell that
held two tab view models and forwarded Cancel — is deleted with the tab.

The registries retarget: one builder per window, the tab-selection dance
gone from every suite that had it, the FooterActionsFollowTheSelectedTab
fact deleted rather than ported because the machinery it guarded is what
was removed, and each window's MinExamined measured afresh — the old floor
of 38 was the sum of two tabs. The E2E scenarios drive the two windows and
wait on a dispatcher sentinel (ScenarioKit.Drained) instead of a row note:
the probe on add now fills the note before the run, so the old wait would
settle too early."
```

---

### Task 10: Three demonstrations the feature did not have

**Files:**
- Modify: `tools/OrdoSort.Smoke/E2E/Fixture.cs` (`EncryptedZip`), `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs` (`PasswordPrompts`), `tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs`
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs`

**Interfaces:**
- Consumes: `ScriptedDialogs.QueuePassword` (Task 5); `ZipItemRow.IsRunnable`, `MergeButtonText`, `ExtractButtonText` (Tasks 6–7); `ScenarioKit.Drained`, the retargeted `Extract`/`Merge` helpers (Task 9).
- Produces: `Fixture.EncryptedZip(string relativePath, string password, params (string entryName, string sourcePath)[] entries)`; `ScriptedDialogs.PasswordPrompts`.

- [ ] **Step 1: Write the failing harness tests**

In `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs`, add `using OrdoSort.Core;` if absent and these two facts after `RawZipPreservesATraversalEntryNameVerbatim`:

```csharp
    /// <summary>The locked-archive fixture is genuinely encrypted: the entry
    /// is flagged, refuses to open without a password, and gives its bytes
    /// back with one — asserted through SharpZipLib directly, the way the
    /// tools read it.</summary>
    [Fact]
    public void EncryptedZipIsActuallyEncrypted()
    {
        using var fx = Fixture.Create("encrypted-zip");
        var source = fx.Text("src/a.txt", "hello");
        var zip = fx.EncryptedZip("archives/locked.zip", "secret", ("a.txt", source));

        using var archive = new ICSharpCode.SharpZipLib.Zip.ZipFile(zip);
        var entry = archive[0];
        Assert.True(entry.IsCrypted);
        Assert.Throws<ICSharpCode.SharpZipLib.Zip.ZipException>(() => archive.GetInputStream(entry));

        archive.Password = "secret";
        using var reader = new StreamReader(archive.GetInputStream(entry));
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void ScriptedDialogsAnswerQueuedPasswordsThenNullAndCountEveryPrompt()
    {
        var dialogs = new ScriptedDialogs().QueuePassword("a", "b");
        var request = new PasswordRequest("x.zip", null, false);

        Assert.Equal("a", dialogs.AskPassword(request));
        Assert.Equal("b", dialogs.AskPassword(request));
        Assert.Null(dialogs.AskPassword(request));   // the queue ran dry: a skip
        Assert.Equal(3, dialogs.PasswordPrompts);
        Assert.Empty(dialogs.Unconsumed);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~E2EHarnessTests" -v minimal`
Expected: build FAILS — `'Fixture' does not contain a definition for 'EncryptedZip'`, `'ScriptedDialogs' does not contain a definition for 'PasswordPrompts'`.

- [ ] **Step 3: The fixture and the counter**

In `tools/OrdoSort.Smoke/E2E/Fixture.cs`, after `RawZip` (fully qualified names — `System.IO.Compression.ZipFile` is already in scope as `ZipFile`, and SharpZipLib's would collide):

```csharp
    /// <summary>A password-protected archive — WinZip AES-256 through
    /// SharpZipLib's writer, the only writer in reach that encrypts —
    /// holding real files. What a colleague's zip tool produces, so what the
    /// prompt has to open.</summary>
    public string EncryptedZip(string relativePath, string password,
        params (string entryName, string sourcePath)[] entries)
    {
        var path = Resolve(relativePath);
        using var fs = File.Create(path);
        using var zos = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(fs) { Password = password };
        foreach (var (name, source) in entries)
        {
            var bytes = File.ReadAllBytes(source);
            zos.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry(name) { Size = bytes.Length, AESKeySize = 256 });
            zos.Write(bytes, 0, bytes.Length);
            zos.CloseEntry();
        }
        return path;
    }
```

In `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs`:

```csharp
    /// <summary>How many times a run reached the prompt — answered or
    /// skipped. A scenario about the prompt asserts this, because "the row
    /// ended up needing a password" is also what a run that never asked
    /// would produce.</summary>
    public int PasswordPrompts { get; private set; }

    public string? AskPassword(PasswordRequest request)
    {
        PasswordPrompts++;
        return _password.Count > 0 ? _password.Dequeue() : null;
    }
```

(replacing the one-line `AskPassword` Task 5 added).

- [ ] **Step 4: Run the harness tests to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~E2EHarnessTests" -v minimal` — expected `Failed: 0`.

- [ ] **Step 5: The Unzip demonstrations**

In `tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs`, add to `All()`:

```csharp
        new Scenario(Surface, "password-protected archive", "clean", LockedArchive),
        new Scenario(Surface, "password-protected archive, prompt skipped", "awkward", LockedArchiveSkipped),
```

and the two scenarios:

```csharp
    /// <summary>The prompt, end to end: the archive is AES-encrypted, no
    /// password is saved, so Extract reaches the prompt and ScriptedDialogs
    /// answers it — through the same Send hop the real window uses, because
    /// uiContext here is the live dispatcher context.</summary>
    private static void LockedArchive(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.EncryptedZip("archives/locked.zip", "secret", ("one.pdf", one));
        ctx.Dialogs.QueuePassword("secret");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("the prompt was reached exactly once", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("extracted", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.FileExists(Path.Combine(ctx.Fx.Root, "archives", "locked", "one.pdf"));
        ctx.Capture(win);
    }

    /// <summary>The same archive, nothing queued: the prompt is skipped, the
    /// row waits for a password and is still runnable, and nothing at all
    /// is written.</summary>
    private static void LockedArchiveSkipped(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.EncryptedZip("archives/locked.zip", "secret", ("one.pdf", one));

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("the prompt was reached", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("the row is waiting for a password, and still runnable",
            vm.Rows[0].StatusKind == ZipItemRowStatus.NeedsPassword && vm.Rows[0].IsRunnable,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("the button still counts it", vm.ExtractButtonText == "Extract 1 zip", vm.ExtractButtonText);
        ctx.Check("nothing was written",
            !Directory.Exists(Path.Combine(ctx.Fx.Root, "archives", "locked")), "an output folder appeared");
        ctx.Capture(win);
    }
```

- [ ] **Step 6: The Merge demonstrations**

In `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs`, add to `All()`:

```csharp
        new Scenario(Surface, "an encrypted PDF inside, password supplied", "clean", EncryptedInsideWithPassword),
        new Scenario(Surface, "loose PDFs merge into one", "clean", LoosePdfs),
        new Scenario(Surface, "a locked loose PDF is skipped", "awkward", LockedLooseSkipped),
```

and the three scenarios:

```csharp
    /// <summary>EncryptedInside's sibling: the same archive, the password
    /// queued. The prompt is reached once, the answer opens the entry, and
    /// both documents contribute a page.</summary>
    private static void EncryptedInsideWithPassword(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));
        ctx.Dialogs.QueuePassword("secret");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("the prompt was reached exactly once", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("merged", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            AssertPageCount(ctx, output, 2, "both documents contributed a page");
        }
        ctx.Capture(win);
    }

    /// <summary>Three loose documents, one output: named after their folder
    /// and placed beside the first, with every row pointing at it.</summary>
    private static void LoosePdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "TWO");
        var c = ctx.Fx.Pdf("src/c.pdf", "THREE");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, a, b, c);

        ctx.Check("every row reports the one document", vm.Rows.All(r => r.StatusKind == ZipItemRowStatus.Ok),
            "rows: " + string.Join(", ", vm.Rows.Select(r => $"{r.Display}:{r.StatusKind}")));
        var expected = Path.Combine(ctx.Fx.Root, "src", "src.pdf");
        ctx.FileExists(expected);
        ctx.Check("every row points at it",
            vm.Rows.All(r => string.Equals(r.Output, expected, StringComparison.OrdinalIgnoreCase)),
            "outputs: " + string.Join(", ", vm.Rows.Select(r => r.Output)));
        AssertPageCount(ctx, expected, 3, "three one-page documents in, three pages out");
        ctx.Capture(win);
    }

    /// <summary>Fail-whole for the loose group, end to end: one locked
    /// document, the prompt skipped, and nothing merges — the locked row
    /// waits for a password, the plain one says what held it back, and both
    /// are still runnable.</summary>
    private static void LockedLooseSkipped(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, a, locked);

        var lockedRow = vm.Rows.Single(r => r.Path == locked);
        var plainRow = vm.Rows.Single(r => r.Path == a);
        ctx.Check("the prompt was reached", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("the locked one is waiting for a password",
            lockedRow.StatusKind == ZipItemRowStatus.NeedsPassword, $"{lockedRow.StatusKind} — {lockedRow.Note}");
        ctx.Check("the plain one was held back, and says why",
            plainRow.StatusKind == ZipItemRowStatus.Pending && plainRow.Note == "not merged — locked.pdf needs a password",
            $"{plainRow.StatusKind} — \"{plainRow.Note}\"");
        ctx.Check("nothing was written", !File.Exists(Path.Combine(ctx.Fx.Root, "src", "src.pdf")),
            "a merged document appeared");
        ctx.Check("both rows are still runnable", vm.MergeButtonText == "Merge 2 items", vm.MergeButtonText);
        ctx.Capture(win);
    }
```

- [ ] **Step 7: Run the surfaces and the full check**

```
e2e.bat unzip
e2e.bat zipmerge
```

Read the reports: seven Unzip scenarios and eight Zip merge scenarios, every one `PASS`, `Unconsumed` empty. Then the full check.

- [ ] **Step 8: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Fixture.cs tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs
git commit -m "test(e2e): the prompt, the loose merge, and fail-whole, driven through the real windows

Five demonstrations the feature did not have: a locked archive extracted
after the prompt is answered, and left runnable after it is skipped; a
locked PDF inside an archive merged after the prompt; three loose PDFs
into one document named after their folder; and a locked loose PDF
skipped, merging nothing and saying why on the row it held back. Every
prompt is counted, because a row that ends up needing a password is also
what a run that never asked would produce."
```

---

### Task 11: The docs catch up, and the last full run

**Files:**
- Modify: `README.md:89,117-118`, `docs/known-flakes.md` (the baseline line)

- [ ] **Step 1: The README's Tools list**

`README.md:89`: `eight utilities` becomes `nine utilities`. Replace the last Tools entry

```markdown
  - *Zip and unzip* — one window, two tabs; the second merges the PDFs held
    inside an archive.
```

with

```markdown
  - *Zip and unzip* — files and folders into one archive, or each archive
    into its own folder beside it. A password-protected zip asks for its
    password instead of failing, after the passwords the app already knows
    have been tried; a skipped one stays runnable.
  - *Merge PDFs* — loose PDFs into one document named after their folder,
    and every PDF inside a zip into one document beside the zip. A locked
    PDF or archive asks for its password; one unopenable document merges
    nothing from its group rather than a document with pages quietly
    missing.
```

- [ ] **Step 2: The known-flakes baseline**

In `docs/known-flakes.md`, the line `**Baseline as of 2026-08-15** (…): **Core 661, Wpf 1738.**` becomes the counts the final run below prints, dated today, with `main`'s SHA replaced by this branch's HEAD once committed (write the branch name and `HEAD` if the SHA is not yet known).

- [ ] **Step 3: The last full check**

Run the full check. Both `Passed!` lines, `Failed: 0`, counts above the baseline at the start of this plan (Core 698, Wpf 1895). Then all three E2E surfaces once more:

```
e2e.bat zip
e2e.bat unzip
e2e.bat zipmerge
```

- [ ] **Step 4: Commit**

```bash
git add README.md docs/known-flakes.md
git commit -m "docs: nine tools, and what the two zip windows now do

The Tools list goes from eight entries to nine: Zip and unzip loses its
'one window, two tabs' clause and gains the password prompt; Merge PDFs
is new. The known-flakes baseline moves to the counts this branch's last
full run printed."
```

---

## Notes for the executor

- **The check is the rebuild.** `dotnet build OrdoSort.sln -t:Rebuild -v minimal` then `dotnet test OrdoSort.sln --no-build -v minimal`. Smart App Control blocks unsigned binaries by hash and a Debug build only moves the hash when the compiler actually runs; `-t:Rebuild` is what makes it run. Never pass `-p:Deterministic=false` on the command line. Always read the two `Passed!` lines — `dotnet test` can exit 0 having run nothing.
- **SharpZipLib reaches the test projects and the smoke tool transitively** through their project references to `OrdoSort.Core`; add no `PackageReference` anywhere else. In any file that also uses `System.IO.Compression.ZipFile`, alias one of the two (`using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;` or `using ZipFile = System.IO.Compression.ZipFile;`) — the names collide.
- **Measure `MinExamined`, never guess it.** Set 9999, run `WindowOverflowTests`, read the count out of the failure message, set three quarters rounded up, note the measured count beside it.
- **The measured facts in the header are the contract.** If SharpZipLib behaves differently from the table (a wrong password not throwing at `GetInputStream`, an AES entry with a non-zero CRC), stop and say so rather than adapting the code silently — the tests in Task 2 are written against those measurements.
- **Scripted probes in view-model tests are not optional.** The real `Zipper.Probe`/`Unlock.ProbeReadiness` on a `TempDir`'s one-byte files reports every row unreadable; every `MakeVm` in Tasks 6–8 defaults the probes to "not encrypted" for that reason, and the one fact per suite that uses the real probe builds a real archive or document.
- **Task order is load-bearing.** Task 8 adds the window before Task 9 removes the tab, so no commit loses Merge PDFs. Tasks 2 and 4 leave a one-line default in the view models (`Array.Empty<string>(), null`) that Tasks 6 and 7 replace — do not thread passwords through the view models early.
- **What is deliberately not here** (the spec's out-of-scope list): creating encrypted zips, saving a password from these windows, drag-reorder, folders as merge sources, an extract destination picker, probing PDFs inside zips on add, renaming `ZipToolsWindow`.
