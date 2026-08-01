using System.Text;
using PdfSharp.Pdf.IO;

/// <summary>Builds a full-size workbench under <c>demo-full\</c>: ten routes, a
/// deep inbox, and a folder per tool stocked with the awkward cases each one is
/// supposed to survive — locked PDFs whose passwords you do and don't have,
/// filenames that need three different rename operations, and a roster
/// engineered so Match &amp; merge lands on every status including the ambiguous
/// and suggested ones that open Review matches.
///
/// Deterministic: same seed, same workbench, so a bug found here is a bug you
/// can hand to someone else. Generated folders are wiped on each run; nothing
/// outside <c>demo-full\</c> is touched.</summary>
public static class DemoWorkbench
{
    private const int Seed = 20260727;

    public static int Run(string[] args)
    {
        var count = 300;
        if (args.Length > 1 && int.TryParse(args[1], out var n))
            count = Math.Clamp(n, 10, 20_000);

        var root = Path.Combine(Directory.GetCurrentDirectory(), "demo-full");
        var rng = new Random(Seed);

        var inbox     = Fresh(root, "inbox");
        var deferred  = Fresh(root, "deferred");
        var locked    = Fresh(root, "locked");
        var rename    = Fresh(root, "rename");
        var merge     = Fresh(root, "merge");
        var routesDir = Fresh(root, "routes");
        var watchDir  = Fresh(root, "watch");

        var routes = BuildRoutes(routesDir);
        var watches = BuildWatchFolders(watchDir);

        var inboxCount = FillInbox(inbox, count, rng);
        FillDeferred(deferred, rng);
        var passwords = FillLocked(locked);
        FillRename(rename, rng);
        var mergeStats = FillMerge(merge);

        File.WriteAllText(Path.Combine(root, "names.txt"),
            string.Join(Environment.NewLine, SeedNames) + Environment.NewLine);

        WriteConfig(root, inbox, deferred, routes, watches, passwords);

        Console.WriteLine($"""

            Workbench ready:  {root}

              inbox        {inboxCount.Matching} filable  ({inboxCount.Ignored} ignored, {inboxCount.Alerting} alerting)
              deferred     {Directory.GetFiles(deferred).Length} set aside, oldest backdated 94 days
              routes       {routes.Count} destinations, Ctrl+1..Ctrl+0
              watch        {watches.Count} monitored folders, {Directory.GetFiles(Path.Combine(watchDir, "failed-transfers")).Length} in failed-transfers
              locked       {Directory.GetFiles(locked).Length} PDFs — passwords: {string.Join(", ", passwords.Select(p => p.Label))} (+2 you do NOT have)
              rename       {Directory.GetFiles(rename).Length} awkward filenames
              merge        {Directory.GetFiles(merge, "*.pdf").Length} PDFs + roster.csv
                           {mergeStats}

            Launch it:  dotnet run --project src\OrdoSort.Wpf -- --config demo-full\config.json
            """);

        return Verify(root, merge, locked, inboxCount.Matching) ? 0 : 1;
    }

    /// <summary>Run the app's own logic over what was just written. A workbench
    /// that merely has files in it is worthless — these are the claims the
    /// summary above makes, checked rather than asserted.</summary>
    private static bool Verify(string root, string merge, string locked, int expectedFilable)
    {
        var ok = true;
        void Check(string what, bool pass, string detail = "")
        {
            Console.WriteLine($"  {(pass ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!pass) ok = false;
        }

        Console.WriteLine("\nSelf-check:");

        // 1. the config the app will actually load
        OrdoSort.Core.Config cfg;
        try
        {
            cfg = OrdoSort.Core.Config.Load(Path.Combine(root, "config.json"));
            Check("config.json loads and validates", true, $"{cfg.Routes.Count} routes");
        }
        catch (Exception ex)
        {
            Check("config.json loads and validates", false, ex.Message);
            return false;
        }

        // 2. every destination is real and writable
        var badRoutes = cfg.Routes
            .Select(r => (r.Label, Problem: OrdoSort.Core.Config.ValidateRoute(r)))
            .Where(x => x.Problem.Length > 0).ToList();
        Check("all 10 destinations are writable", badRoutes.Count == 0,
            badRoutes.Count == 0 ? "" : string.Join("; ", badRoutes.Select(b => $"{b.Label}: {b.Problem}")));

        // 3. the inbox scan agrees with what was generated
        var scan = OrdoSort.Core.Scanner.Scan(cfg.Inbox, cfg.Sort, cfg.NamingMode);
        Check("inbox scan matches", scan.Error.Length == 0 && scan.Count == expectedFilable,
            $"{scan.Count} filable, {scan.IgnoredCount} ignored{(scan.Error.Length > 0 ? " · " + scan.Error : "")}");

        // full replace should see strictly more (it takes every PDF)
        var replaceScan = OrdoSort.Core.Scanner.Scan(cfg.Inbox, cfg.Sort, "replace");
        Check("full replace sees more than insert", replaceScan.Count > scan.Count,
            $"{replaceScan.Count} vs {scan.Count}");

        // 4. Match & merge lands on every status, including the ambiguous ones
        try
        {
            var roster = OrdoSort.Core.MatchMerge.LoadRoster(
                Path.Combine(merge, "roster.csv"),
                cfg.MergeHeaders["first"], cfg.MergeHeaders["last"], cfg.MergeHeaders["control"]);
            var results = OrdoSort.Core.MatchMerge.MatchFiles(
                Directory.GetFiles(merge, "*.pdf").OrderBy(f => f), roster);
            var by = results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
            int Got(string s) => by.TryGetValue(s, out var c) ? c : 0;

            Check("merge: 8 clean matches", Got("merge") == 8, $"got {Got("merge")}");
            Check("merge: 5 ambiguous (opens Review matches)", Got("ambiguous") == 5, $"got {Got("ambiguous")}");
            Check("merge: 3 suggested (token pass)", Got("suggested") == 3, $"got {Got("suggested")}");
            Check("merge: 3 already merged", Got("already") == 3, $"got {Got("already")}");
            Check("merge: 2 unmatched", Got("no_match") == 2, $"got {Got("no_match")}");
            Check("merge: 2 unnamed", Got("no_name") == 2, $"got {Got("no_name")}");
        }
        catch (Exception ex)
        {
            Check("merge roster loads", false, ex.Message);
        }

        // 5. the locked PDFs are genuinely encrypted, and the saved passwords work
        var probe = Path.Combine(Path.GetTempPath(), "wb_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var sample = Directory.GetFiles(locked, "northgate_*.pdf").First();
            var good = OrdoSort.Core.Unlock.UnlockPdf(sample, "northgate2024", probe, "_open");
            Check("a saved password unlocks its PDFs", good.Status == "ok", good.Status + " " + good.Message);

            var bad = OrdoSort.Core.Unlock.UnlockPdf(sample, "wrong-password", probe, "_nope");
            Check("a wrong password is refused", bad.Status == "wrong_password", bad.Status);

            var open = Directory.GetFiles(locked, "already_open_*.pdf").First();
            var plain = OrdoSort.Core.Unlock.UnlockPdf(open, "anything", probe, "_x");
            Check("an unencrypted PDF reports as such", plain.Status == "not_encrypted", plain.Status);
        }
        catch (Exception ex)
        {
            Check("unlock probe", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(probe, true); } catch { /* best effort */ }
        }

        Console.WriteLine(ok
            ? "\nAll checks passed — the workbench behaves the way the summary says.\n"
            : "\nSOME CHECKS FAILED — the workbench is not what the summary claims.\n");
        return ok;
    }

    // ---------------------------------------------------------------- routes
    private sealed record RouteSpec(string Label, string Path, string Hotkey,
        string Color, string Suffix, string? Mode);

    private static List<RouteSpec> BuildRoutes(string routesDir)
    {
        // ten real filing destinations; hotkeys run Ctrl+1..Ctrl+9 then Ctrl+0
        var spec = new (string Label, string Color, string Suffix, string? Mode)[]
        {
            ("Invoices",       "#2E7D32", "_INV",  null),
            ("Statements",     "#1565C0", "",      null),
            ("Correspondence", "#6A1B9A", "",      null),
            ("Medical records","#00838F", "_MED",  null),
            ("Legal",          "#4E342E", "",      null),
            ("Insurance",      "#AD1457", "",      null),
            ("Remittance",     "#EF6C00", "_REM",  null),
            ("Contracts",      "#37474F", "",      null),
            // per-route override: this one renames the whole filename
            ("Tax",            "#827717", "_TAX",  "replace"),
            ("Archive",        "#455A64", "",      null),
        };
        var routes = new List<RouteSpec>();
        for (var i = 0; i < spec.Length; i++)
        {
            var (label, color, suffix, mode) = spec[i];
            var dir = Path.Combine(routesDir,
                $"{i + 1:00}-{label.ToLowerInvariant().Replace(' ', '-')}");
            Directory.CreateDirectory(dir);
            routes.Add(new RouteSpec(label, dir, $"Ctrl+{(i + 1) % 10}", color, suffix, mode));
        }
        return routes;
    }

    private sealed record WatchSpec(string Label, string Path, string Color,
        string Filetypes, bool Recursive, string Section);

    private static List<WatchSpec> BuildWatchFolders(string watchDir)
    {
        var failed = Path.Combine(watchDir, "failed-transfers");
        var rejected = Path.Combine(watchDir, "rejected");
        var scanner = Path.Combine(watchDir, "scanner-out");
        var retries = Path.Combine(failed, "retries");
        foreach (var d in new[] { failed, rejected, scanner, retries })
            Directory.CreateDirectory(d);

        // three alerting, one of them down a subfolder so the tile names it
        MinimalPdf.Write(Path.Combine(failed, "URGENT_transfer_44120.pdf"), "URGENT 44120");
        MinimalPdf.Write(Path.Combine(failed, "transfer_44121.pdf"), "44121");
        MinimalPdf.Write(Path.Combine(retries, "RUSH_retry_44122.pdf"), "RUSH 44122");
        MinimalPdf.Write(Path.Combine(rejected, "STAT_rejected_9981.pdf"), "STAT 9981");
        MinimalPdf.Write(Path.Combine(rejected, "rejected_9982.pdf"), "9982");
        // scanner-out stays empty: an "all quiet" tile in All mode

        return new()
        {
            new("Failed transfers", failed, "#C0392B", "pdf", true, "Failed queues"),
            new("Rejected", rejected, "#EF6C00", "pdf", false, "Scanner queues"),
            new("Scanner out", scanner, "#455A64", "pdf", false, ""),
        };
    }

    // ----------------------------------------------------------------- inbox
    private sealed record InboxCount(int Matching, int Ignored, int Alerting);

    private static InboxCount FillInbox(string inbox, int count, Random rng)
    {
        int matching = 0, ignored = 0, alerting = 0;
        var start = new DateTime(2024, 1, 2);

        for (var i = 0; i < count; i++)
        {
            var date = start.AddDays(rng.Next(0, 420));
            var id = 1_000_000_000L + rng.Next(0, 99_999_999);

            // one in twelve trips an alert term; one in nine is unfilable
            var alert = i % 12 == 0;
            var junk = i % 9 == 4;

            string name;
            if (junk)
            {
                // no "--", so insert mode ignores it (full replace would take it)
                name = i % 18 == 4
                    ? $"scanner_notes_{i:0000}.txt"
                    : $"cover_sheet_{i:0000}.pdf";
                ignored++;
            }
            else
            {
                var tag = alert ? (i % 24 == 0 ? "URGENT_" : "RUSH_") : "";
                name = $"{tag}{date:yyyyMMdd}--{id}.pdf";
                matching++;
                if (alert) alerting++;
            }

            var path = Path.Combine(inbox, name);
            // vary the body so size sorting has something to sort by
            var body = new string('.', rng.Next(20, 900));
            if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(path, $"scanner notes {i}\n{body}");
            else
                MinimalPdf.Write(path, $"DOCUMENT {id} - {date:yyyy-MM-dd} {body}");

            File.SetLastWriteTime(path, date.AddMinutes(rng.Next(0, 1440)));
        }
        return new InboxCount(matching, ignored, alerting);
    }

    private static void FillDeferred(string deferred, Random rng)
    {
        // backdated so the set-aside banner shows a real "oldest N days"
        var ages = new[] { 94, 61, 30, 12, 5, 2, 1 };
        foreach (var age in ages)
        {
            var when = DateTime.Now.AddDays(-age);
            var path = Path.Combine(deferred, $"{when:yyyyMMdd}--{2_000_000_000L + rng.Next(0, 9_999_999)}.pdf");
            MinimalPdf.Write(path, $"SET ASIDE {age} days ago");
            File.SetLastWriteTime(path, when);
        }
    }

    // ---------------------------------------------------------------- locked
    private sealed record PasswordSpec(string Label, string Password);

    private static List<PasswordSpec> FillLocked(string locked)
    {
        // two you will have saved in Settings, two you will not
        var known = new List<PasswordSpec>
        {
            new("Northgate Clinic", "northgate2024"),
            new("Payer A", "letmein"),
        };
        var unknown = new[] { "s3cr3t-not-saved", "forgotten-passphrase" };

        var n = 0;
        foreach (var p in known)
            for (var i = 0; i < 6; i++)
                WriteLocked(Path.Combine(locked, $"{p.Label.Split(' ')[0].ToLowerInvariant()}_{++n:000}.pdf"),
                    p.Password, $"LOCKED {p.Label} {n}");

        foreach (var pw in unknown)
            for (var i = 0; i < 3; i++)
                WriteLocked(Path.Combine(locked, $"unknown_{++n:000}.pdf"), pw, $"LOCKED unknown {n}");

        // and two that are not encrypted at all — the "isn't password-protected"
        // path deserves exercising too
        for (var i = 0; i < 2; i++)
            MinimalPdf.Write(Path.Combine(locked, $"already_open_{++n:000}.pdf"), $"NOT LOCKED {n}");

        return known;
    }

    private static void WriteLocked(string path, string password, string text)
    {
        // build a plain page first, then re-save it encrypted: no font resolver
        // needed, and the page content stays identical to every other demo PDF
        var tmp = Path.Combine(Path.GetTempPath(), $"wb_{Guid.NewGuid():N}.pdf");
        try
        {
            MinimalPdf.Write(tmp, text);
            using var doc = PdfReader.Open(tmp, PdfDocumentOpenMode.Modify);
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
            doc.Save(path);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------- rename
    private static void FillRename(string rename, Random rng)
    {
        // 1. review-stem files: LAST_FIRST_M_D_YYYY — Bulk rename can rebuild
        //    these into YYYYMMDD-LAST-FIRST with the received-date field
        foreach (var (last, first) in ReviewPeople)
            MinimalPdf.Write(
                Path.Combine(rename, $"{last}_{first}_{rng.Next(1, 13)}_{rng.Next(1, 28)}_2024.pdf"),
                $"REVIEW {last} {first}");

        // 2. a junk token running through a batch — find/replace
        for (var i = 1; i <= 14; i++)
            MinimalPdf.Write(Path.Combine(rename, $"scan0{i:00}_COPY_FINAL.pdf"), $"SCAN {i}");

        // 3. mixed case and stray spaces — the case and affix operations
        foreach (var n in new[]
                 {
                     "Quarterly Report q1", "quarterly report Q2",
                     "QUARTERLY REPORT Q3", "Quarterly  Report  q4",
                 })
            MinimalPdf.Write(Path.Combine(rename, $"{n}.pdf"), n);

        // 4. a pair that will collide once renamed — proves the (2) counter
        MinimalPdf.Write(Path.Combine(rename, "duplicate_COPY_FINAL.pdf"), "DUP A");
        MinimalPdf.Write(Path.Combine(rename, "duplicate_COPY_FINAL_2.pdf"), "DUP B");
    }

    // ----------------------------------------------------------------- merge
    private static string FillMerge(string merge)
    {
        // Roster rows. Two people share a name with DIFFERENT control ids —
        // that is what MatchFiles calls "ambiguous" and what opens Review matches.
        var rows = new List<(string First, string Last, string Control, string Dob)>
        {
            ("JOHN",    "SMITH",    "5500011", "1975-03-02"),
            ("JOHN",    "SMITH",    "5500012", "1988-11-19"),   // ambiguous pair
            ("MARIA",   "GARCIA",   "5500020", "1969-07-30"),
            ("MARIA",   "GARCIA",   "5500021", "1991-01-08"),   // ambiguous pair
            ("MARIA",   "GARCIA",   "5500022", "2001-05-14"),   // three-way
            ("PATRICK", "OBRIEN",   "5500030", "1980-09-09"),
            ("YUKI",    "TANAKA",   "5500040", "1972-12-01"),
            ("HANS",    "MULLER",   "5500050", "1965-06-21"),
            ("EMMA",    "WILSON",   "5500060", "1994-02-17"),
            ("FRANK",   "EVANS",    "5500070", "1958-08-05"),
            ("SARAH",   "MILLER",   "5500080", "1986-04-23"),
            ("ADAM",    "BROWN",    "5500090", "1977-10-11"),
            ("MARY",    "SMITH JONES", "5500100", "1983-01-30"),
            ("NOAH",    "PATEL",    "5500110", "1990-06-06"),   // inverted-name PDF, no exact-order match
            ("MARIA",   "DE LA CRUZ", "5500120", "1995-03-11"),  // multi-segment surname
            ("MICHAEL", "TORRES",   "5500130", "1982-07-19"),   // near-miss first name
        };

        var csv = new StringBuilder();
        csv.AppendLine("First Name,Last Name,Control ID,Date of Birth");
        foreach (var r in rows)
            csv.AppendLine($"{r.First},{r.Last},{r.Control},{r.Dob}");
        File.WriteAllText(Path.Combine(merge, "roster.csv"),
            csv.ToString(), new UTF8Encoding(true));   // Excel-style BOM

        void Pdf(string stem) => MinimalPdf.Write(Path.Combine(merge, stem + ".pdf"), stem);

        // clean single matches -> "merge"
        Pdf("20240301-OBRIEN-PATRICK");
        Pdf("20240302-TANAKA-YUKI");
        Pdf("20240303-MULLER-HANS");
        Pdf("20240304-WILSON-EMMA");
        Pdf("20240305-EVANS-FRANK");
        Pdf("20240306-MILLER-SARAH");
        Pdf("20240307-BROWN-ADAM");
        Pdf("20240308-SMITH JONES-MARY");     // two-word surname

        // ambiguous -> Review matches has to choose
        Pdf("20240310-SMITH-JOHN");
        Pdf("20240311-SMITH-JOHN");
        Pdf("20240312-GARCIA-MARIA");
        Pdf("20240313-GARCIA-MARIA");
        Pdf("20240314-GARCIA-MARIA");

        // suggested -> the token pass reaches Review matches too, but never
        // merges by itself; the exact lookup misses each of these on purpose
        Pdf("20240309-NOAH-PATEL");            // inverted: <FIRST>-<LAST> for a LAST/FIRST roster row
        Pdf("20240315-CRUZ-MARIA");            // multi-segment: roster holds "DE LA CRUZ", file carries only "CRUZ"
        Pdf("20240316-TORRES-MICHEAL");        // near-miss: MICHEAL is one letter from roster's MICHAEL

        // already merged -> "already", must be left alone
        Pdf("20240320-EVANS-FRANK-5500070");
        Pdf("20240321-TANAKA-YUKI-5500040");
        // the token-path already-merged guard: the merged form of the
        // NOAH-PATEL suggested case above, distinct file — the exact lookup
        // still misses (inverted order), so this exercises the token pass's
        // own already-merged detection, not the exact path's
        Pdf("20240309-NOAH-PATEL-5500110");

        // nobody by that name -> "no_match"
        Pdf("20240330-NGUYEN-LINH");
        Pdf("20240331-KOWALSKI-PIOTR");

        // unparseable stem -> "no_name"
        Pdf("scan_00412");
        Pdf("batch7_page_003");

        return "8 clean · 5 ambiguous · 3 suggested (Review matches) · 3 already merged · 2 unmatched · 2 unnamed";
    }

    // ------------------------------------------------------------------ misc
    private static readonly string[] ReviewPeople_Raw =
    {
        "SMITH_JOHN", "GARCIA_MARIA", "OBRIEN_PATRICK", "TANAKA_YUKI",
        "MULLER_HANS", "WILSON_EMMA", "EVANS_FRANK", "VAN_DYKE_ANNA",
    };

    private static IEnumerable<(string Last, string First)> ReviewPeople =>
        ReviewPeople_Raw.Select(p =>
        {
            var i = p.LastIndexOf('_');
            return (p[..i], p[(i + 1)..]);
        });

    private static readonly string[] SeedNames =
    {
        "# names.txt — completer seeds, one per line",
        "SMITH JOHN", "GARCIA MARIA", "OBRIEN PATRICK", "TANAKA YUKI",
        "MULLER HANS", "WILSON EMMA", "EVANS FRANK", "MILLER SARAH",
        "BROWN ADAM", "SMITH JONES MARY", "PATEL NOAH", "VAN DYKE ANNA",
        "NORTHGATE CLINIC", "RIVERSIDE LABS", "MERIDIAN INSURANCE",
    };

    private static string Fresh(string root, string name)
    {
        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteConfig(string root, string inbox, string deferred,
        List<RouteSpec> routes, List<WatchSpec> watches, List<PasswordSpec> passwords)
    {
        static string P(string p) => p.Replace('\\', '/');

        var cfg = new OrdoSort.Core.Config
        {
            Inbox = P(inbox),
            Deferred = P(deferred),
            NamesFile = "names.txt",
            HistoryDb = "history.sqlite",
            NamingMode = "insert",
            Sort = "size_desc",
            EnterCommits = true,
            UppercaseNames = true,
            WordSeparator = "-",
            Theme = "auto",
            PollSeconds = 15,
            MonitorTitle = "Needs attention",
            FlashAlerts = true,
            TileVisibility = "active",
            AlertTexts = { "URGENT", "RUSH", "STAT" },
            Routes = routes.Select(r => new OrdoSort.Core.Route
            {
                Label = r.Label,
                Path = P(r.Path),
                Hotkey = r.Hotkey,
                AppendSuffix = r.Suffix.Length > 0,
                Suffix = r.Suffix,
                Color = r.Color,
                NamingMode = r.Mode,
            }).ToList(),
            WatchFolders = watches.Select(w => new OrdoSort.Core.WatchFolder
            {
                Label = w.Label,
                Path = P(w.Path),
                Recursive = w.Recursive,
                Filetypes = w.Filetypes,
                Color = w.Color,
                Section = w.Section,
            }).ToList(),
            // headers as written into merge/roster.csv
            MergeHeaders = new Dictionary<string, string>
            {
                ["first"] = "First Name",
                ["last"] = "Last Name",
                ["control"] = "Control ID",
            },
            // plaintext here on purpose: the app re-protects these with DPAPI
            // the next time saved passwords change in Unlock's Manage saved…
            // dialog or save banner, which is itself worth watching
            SavedPasswords = passwords.Select(p => new OrdoSort.Core.SavedPassword
            {
                Label = p.Label,
                Password = p.Password,
            }).ToList(),
            LabelClients =
            {
                new OrdoSort.Core.LabelClient { Id = "NGC", DestroyDays = 2555, NextNumber = 1L },
                new OrdoSort.Core.LabelClient { Id = "RIVLAB", DestroyDays = 730, NextNumber = 4200L },
                new OrdoSort.Core.LabelClient { Id = "MERID", DestroyDays = 90, NextNumber = 1L },
            },
            Sounds = new OrdoSort.Core.SoundSettings
            {
                Enabled = true,
                NewAlert = "",
                Filed = "none",
                SetAside = "none",
                Error = "",
            },
        };

        // Config.Save only bootstraps box-labels.json when it's missing (it's
        // the exclusive-writer's file, protected from being clobbered by an
        // ordinary settings save) — a from-scratch workbench regen must still
        // start the counters at the seeded values, so delete any leftover
        // file here rather than relying on Save to do that job.
        var boxLabels = Path.Combine(root, cfg.BoxLabelsFile);
        if (File.Exists(boxLabels)) File.Delete(boxLabels);
        OrdoSort.Core.Config.Save(cfg, Path.Combine(root, "config.json"));
    }
}
