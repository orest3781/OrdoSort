/// <summary>Regenerate the demo: 5 sample documents in demo\inbox, empty route and
/// set-aside folders, and a ready-to-use demo\config.json. Self-contained —
/// used by reset.bat. The demo folder is resolved relative to the
/// current directory (reset.bat cd's to the project root first).</summary>
public static class DemoReset
{
    public static int Run()
    {
        var demo = Path.Combine(Directory.GetCurrentDirectory(), "demo");
        var inbox = Path.Combine(demo, "inbox");
        var invoices = Path.Combine(demo, "invoices");
        var statements = Path.Combine(demo, "statements");
        var deferred = Path.Combine(demo, "deferred");
        var failed = Path.Combine(demo, "failed");   // a monitored folder for the dashboard

        // wipe the generated folders, recreate empty
        foreach (var d in new[] { inbox, invoices, statements, deferred, failed })
        {
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
            Directory.CreateDirectory(d);
        }

        // seed the monitored folder so a dashboard tile shows (one file trips
        // the "URGENT" alert -> the tile flashes)
        MinimalPdf.Write(Path.Combine(failed, "retry_00842.pdf"), "FAILED TRANSFER 00842");
        MinimalPdf.Write(Path.Combine(failed, "URGENT_00843.pdf"), "URGENT FAILED TRANSFER 00843");

        var names = new[] { "SMITH JOHN", "GARCIA MARIA", "O'BRIEN PATRICK", "MULLER HANS", "TANAKA YUKI" };
        for (var i = 0; i < names.Length; i++)
            MinimalPdf.Write(
                Path.Combine(inbox, $"2024{i + 1:00}15--{1000000000 + i}.pdf"),
                $"MEDICAL REVIEW   -   {names[i]}   -   Document {i + 1} of {names.Length}");

        var cfg = new OrdoSort.Core.Config
        {
            Inbox = inbox.Replace('\\', '/'),
            Deferred = deferred.Replace('\\', '/'),
            HistoryDb = "history.sqlite",
            NamingMode = "insert",
            Sort = "size_desc",
            UppercaseNames = true,
            EnterCommits = true,
            MonitorTitle = "Needs attention",
            FlashAlerts = true,
            AlertTexts = { "URGENT" },
            WatchFolders =
            {
                new OrdoSort.Core.WatchFolder { Label = "Failed transfers",
                    Path = failed.Replace('\\', '/'), Recursive = false,
                    Filetypes = "pdf", Color = "#c0392b" },
            },
            Routes =
            {
                new OrdoSort.Core.Route { Label = "Invoices",
                    Path = invoices.Replace('\\', '/'), Hotkey = "Ctrl+1",
                    AppendSuffix = true, Suffix = "_INVOICE", Color = "#2e7d32" },
                new OrdoSort.Core.Route { Label = "Statements",
                    Path = statements.Replace('\\', '/'), Hotkey = "Ctrl+2",
                    AppendSuffix = false, Suffix = "", Color = "#1565c0" },
            },
        };
        OrdoSort.Core.Config.Save(cfg, Path.Combine(demo, "config.json"));

        Console.WriteLine($"Demo reset: {names.Length} sample documents in {inbox}");
        Console.WriteLine("Run  run.bat  to launch against it.");
        return 0;
    }
}
