using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrdoSort.Smoke.E2E;

/// <summary>One scenario's outcome, as the report renders it.
/// <paramref name="CaptureNote"/> is set when the scenario nominated a
/// window (<c>ctx.Capture(win)</c>) but <see cref="Capture"/> still came
/// back with no image — a machine without a desktop session, say. That is
/// worth a visible note on the row, not a failed assertion: rasterization
/// is evidence, not the thing under test, so it must not turn an otherwise
/// passing scenario red.</summary>
public sealed record ScenarioResult(
    string Surface, string Name, string Kind, bool Passed,
    IReadOnlyList<Assertion> Assertions, string? Error,
    string? ScreenshotFile, long ElapsedMs, string? CaptureNote = null);

/// <summary>Writes the run's evidence: a self-contained report.html with the
/// screenshots inlined as data: URIs (so it can be mailed or attached to a CI
/// run and still render), the same content as report.md for pasting into a
/// PR, and the PNGs as loose files for reuse in docs.</summary>
public static class Evidence
{
    public static string NewRunDirectory()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "evidence", stamp);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Rasterize a live window. Returns the PNG's file name, or null
    /// if it could not be captured — a missing screenshot is a note in the
    /// report, never a failed scenario.</summary>
    public static string? Capture(Window win, string outDir, string fileStem)
    {
        try
        {
            win.UpdateLayout();
            var w = (int)Math.Ceiling(win.ActualWidth);
            var h = (int)Math.Ceiling(win.ActualHeight);
            if (w <= 0 || h <= 0) return null;

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(win);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));

            var file = fileStem + ".png";
            using var fs = File.Create(Path.Combine(outDir, file));
            enc.Save(fs);
            return file;
        }
        catch { return null; }
    }

    public static void Write(string outDir, IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "report.html"), Html(outDir, results, duration),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "report.md"), Markdown(results, duration),
            new UTF8Encoding(false));
    }

    /// <summary>General-purpose HTML escaper: safe for both text-node
    /// content AND double-quoted attribute values (e.g. the img alt="..."
    /// below). Escaping only &amp;/&lt;/&gt; is enough for text nodes but
    /// NOT for an attribute value — an unescaped `"` in an attribute lets the
    /// interpolated string break out of it and inject markup/attributes of
    /// its own. `&amp;` is replaced first so the entities this method itself
    /// introduces are not double-escaped.</summary>
    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Html(string outDir, IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var surfaces = results.Select(r => r.Surface).Distinct().ToList();

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>OrdoSort &mdash; end-to-end evidence</title><style>");
        sb.Append(":root{--bg:#fff;--fg:#1a1a1a;--muted:#666;--line:#e3e3e3;--ok:#0a7d33;--bad:#b3261e;--card:#fafafa}");
        sb.Append("@media(prefers-color-scheme:dark){:root{--bg:#161616;--fg:#ececec;--muted:#a0a0a0;--line:#333;--ok:#5cc47c;--bad:#f2857c;--card:#1e1e1e}}");
        sb.Append("*{box-sizing:border-box}");
        sb.Append("body{margin:0;padding:2rem 1.25rem;background:var(--bg);color:var(--fg);font:15px/1.55 -apple-system,Segoe UI,system-ui,sans-serif}");
        sb.Append("main{max-width:60rem;margin:0 auto}h1{font-size:1.5rem;margin:0 0 .25rem}");
        sb.Append("h2{font-size:1.1rem;margin:2.5rem 0 .75rem;padding-bottom:.35rem;border-bottom:1px solid var(--line)}");
        sb.Append(".sum{color:var(--muted);margin:0 0 2rem}");
        sb.Append(".sc{border:1px solid var(--line);border-radius:8px;padding:1rem;margin:0 0 1rem;background:var(--card)}");
        sb.Append(".hd{display:flex;gap:.6rem;align-items:baseline;flex-wrap:wrap}.nm{font-weight:600}");
        sb.Append(".kind{color:var(--muted);font-size:.85rem}.v{font-weight:600}.pass{color:var(--ok)}.fail{color:var(--bad)}");
        sb.Append("ul{margin:.75rem 0 0;padding-left:1.25rem}li{margin:.15rem 0}li.no{color:var(--bad)}.det{color:var(--muted)}");
        sb.Append(".err{color:var(--bad);font-family:ui-monospace,Consolas,monospace;font-size:.85rem;margin-top:.5rem;white-space:pre-wrap}");
        sb.Append(".note{color:var(--muted);font-style:italic;font-size:.85rem;margin-top:.5rem}");
        sb.Append("img{max-width:100%;height:auto;margin-top:.85rem;border:1px solid var(--line);border-radius:6px;display:block}");
        sb.Append("</style></head><body><main>");

        sb.Append("<h1>OrdoSort &mdash; end-to-end evidence</h1><p class=\"sum\">");
        sb.Append($"{results.Count} scenarios across {surfaces.Count} surfaces &middot; ");
        sb.Append(failed == 0
            ? "<span class=\"v pass\">all passed</span>"
            : $"<span class=\"v fail\">{failed} failed</span>, {passed} passed");
        sb.Append($" &middot; {duration.TotalSeconds:F1}s &middot; {Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))}</p>");

        foreach (var surface in surfaces)
        {
            sb.Append($"<h2>{Esc(surface)}</h2>");
            foreach (var r in results.Where(x => x.Surface == surface))
            {
                sb.Append("<div class=\"sc\"><div class=\"hd\">");
                sb.Append($"<span class=\"nm\">{Esc(r.Name)}</span>");
                sb.Append($"<span class=\"kind\">{Esc(r.Kind)} &middot; {r.ElapsedMs}ms</span>");
                sb.Append(r.Passed ? "<span class=\"v pass\">PASS</span>" : "<span class=\"v fail\">FAIL</span>");
                sb.Append("</div><ul>");
                foreach (var a in r.Assertions)
                {
                    sb.Append(a.Passed ? "<li>" : "<li class=\"no\">");
                    sb.Append(Esc(a.Description));
                    if (!a.Passed && a.Detail is not null)
                        sb.Append($" <span class=\"det\">&mdash; {Esc(a.Detail)}</span>");
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
                if (r.Error is not null) sb.Append($"<div class=\"err\">{Esc(r.Error)}</div>");
                if (r.CaptureNote is not null) sb.Append($"<div class=\"note\">{Esc(r.CaptureNote)}</div>");

                if (r.ScreenshotFile is not null)
                {
                    var png = Path.Combine(outDir, r.ScreenshotFile);
                    if (File.Exists(png))
                    {
                        var b64 = Convert.ToBase64String(File.ReadAllBytes(png));
                        sb.Append($"<img alt=\"{Esc(r.Name)}\" src=\"data:image/png;base64,{b64}\">");
                    }
                }
                sb.Append("</div>");
            }
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Markdown(IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var sb = new StringBuilder();

        sb.AppendLine("# OrdoSort — end-to-end evidence").AppendLine();
        sb.AppendLine($"{results.Count} scenarios across {results.Select(r => r.Surface).Distinct().Count()} surfaces · "
            + (failed == 0 ? "all passed" : $"**{failed} failed**, {passed} passed")
            + $" · {duration.TotalSeconds:F1}s").AppendLine();

        foreach (var surface in results.Select(r => r.Surface).Distinct())
        {
            sb.AppendLine($"## {surface}").AppendLine();
            foreach (var r in results.Where(x => x.Surface == surface))
            {
                sb.AppendLine($"### {r.Name} — {(r.Passed ? "PASS" : "FAIL")} _({r.Kind}, {r.ElapsedMs}ms)_").AppendLine();
                foreach (var a in r.Assertions)
                    sb.AppendLine($"- {(a.Passed ? "[x]" : "[ ]")} {a.Description}"
                        + (!a.Passed && a.Detail is not null ? $" — {a.Detail}" : ""));
                if (r.Error is not null) sb.AppendLine().AppendLine("```").AppendLine(r.Error).AppendLine("```");
                if (r.CaptureNote is not null) sb.AppendLine().AppendLine($"_{r.CaptureNote}_");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
