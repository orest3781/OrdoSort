# Box Label Style and Box Labels Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Box numbers readable on a shelf, through a choice of label styles (layout, leading zeros, date bars) stored in the shared `box-labels.json`, set from a new Settings window in `BoxLabels.exe` and a new "Box labels" tab in OrdoSort's Settings.

**Architecture:**
- **Drawing:** `BoxLabels.ComposeDrawing` (Core) takes a `LabelStyle` record instead of a date-style string. The preview, printing and PDF all replay it, so they can't drift apart.
- **Storage:** the style lives in `BoxLabelsDoc` (the shared file), written through `BoxLabelStore.Mutate`.
- **Editing:** one `LabelStyleEditor` control and view model in `OrdoSort.Ui` edits it in both apps.
- **Standalone app:** `BoxLabels.exe` gains a menu bar and a two-tab Settings window. Its General tab (file, theme, text size, font) is saved per PC in `box-labels-app.json`.

**Tech Stack:** C# / .NET 8, WPF, PdfSharp, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-28-box-label-style-design.md`

## Global Constraints

- Label stays 4 × 2 in (288 × 144 pt), 10 per US-letter sheet. `SlotOrigin`, the 0.25 in top and bottom margins and `PerSheet` are unchanged.
- The barcode always encodes the full code (`Code39.Encode(item.Code)`). Its x positions, widths and quiet zones are unchanged. Its height is never under **36 pt** (0.5 in).
- Text never runs past the label width: font size ≤ `(LabelWidthPt - 20) / (text.Length * 0.6)`.
- Nothing is drawn outside the 288 × 144 box. The number text never overlaps a barcode bar or another text.
- Dates are invariant-culture `yyyy-MM-dd`.
- **Standard + leading zeros on + bars must draw exactly today's label.** This is a regression pin.
- Shared-file keys: `label_layout` (`"standard"` | `"big"` | `"huge"`, missing or unknown → `"standard"`), `leading_zeros` (bool, missing → `true`), `date_style` (existing).
- Per-PC keys in `box-labels-app.json`: `ui_font_family` (string, "" = default), `ui_font_size` (0 = default, 6–72). These match OrdoSort's config key names.
- One way to do things: the `string dateStyle` parameters, `LabelMakerViewModel.DateStyle*`, the window's Date bars radios and `LabelStoreBar` are removed, not kept beside the new path.
- No new NuGet packages. Files are CRLF (run `unix2dos -q` after scripted edits). `check.bat` must be green before each commit.
- Plain-language user-facing text; message titles use the host's app title.

## Review Focus

1. **Longest content:** an 8-letter client id with 99 999 999, zeros on, in Big and Huge. The text must still fit the width (tested in Task 1).
2. **Another station changes the style between preview and print.** The sheets must carry the style stored at claim time (tested in Task 3).
3. **A hand-edited or damaged shared file:** `label_layout: "neon"` must print Standard; `leading_zeros: "no"` must be reported as the store's usual "not valid JSON" error, not crash (tested in Task 2).
4. **Box Labels Settings OK while the shared file is locked or unreachable:** General is still saved and applied, and a message names the file that failed (tested in Task 7).
5. **OrdoSort Settings changes `box_labels_file` and the style in one OK:** the style goes to the new file, not the old one (tested in Task 6).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/OrdoSort.Core/BoxLabels.cs` | `LabelStyle`, layout constants, `NumberText`, `NumberFontSize`, `ComposeDrawing(item, style)`, `RenderPdf(…, style)` | 1 |
| `src/OrdoSort.Core/ConfigDocs.cs` | `BoxLabelsDoc.LabelLayout`, `LeadingZeros`, `Style` | 2 |
| `src/OrdoSort.Core/BoxLabelStore.cs` | Normalise `label_layout` on read | 2 |
| `src/OrdoSort.Ui/Views/LabelPreview.cs` | Preview, print document and sheet element take `LabelStyle` | 1, 3 |
| `src/OrdoSort.Ui/ViewModels/LabelMakerViewModel.cs` | `Style` from the store; claims return the stored style; `ReloadStyle()` | 3 |
| `src/OrdoSort.Ui/Windows/LabelMakerWindow.xaml(.cs)` | Remove Date bars radios; menu bar replaces the store bar | 3, 8 |
| `src/OrdoSort.Ui/ViewModels/LabelStyleEditorViewModel.cs` (new) | Edits one `LabelStyle`; knows whether it changed | 4 |
| `src/OrdoSort.Ui/Views/LabelStyleEditor.xaml(.cs)` (new) | Layout radios, zeros box, date radios, live preview | 4 |
| `src/OrdoSort.Ui/Theme/AppFonts.cs` | `Choices`, `DefaultSize`, `SizeProblem`, `Apply` (moved from OrdoSort.Wpf) | 5 |
| `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`, `Windows/SettingsWindow.xaml` | Use `AppFonts`; Box labels tab | 5, 6 |
| `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`, `MainWindow.xaml.cs` | Read the style for Settings; write it after config.json saves | 6 |
| `src/BoxLabels.App/Services/LabelsFileSettings.cs` | Font keys; `ReadFont`, `WriteAppearance` | 7 |
| `src/BoxLabels.App/Services/BoxLabelsSettingsViewModel.cs` (new) | General-tab edits, problems | 7 |
| `src/BoxLabels.App/Services/BoxLabelsSettingsSaver.cs` (new) | Saves General per PC, and the style to the shared file; reports failures | 7 |
| `src/BoxLabels.App/Windows/BoxLabelsSettingsWindow.xaml(.cs)` (new) | Two tabs, OK/Cancel/discard | 8 |
| `src/BoxLabels.App/App.xaml.cs` | Font at start-up, the menu, opening Settings and applying it | 8 |

---

### Task 1: Label styles in the drawing (Core)

**Files:**
- Modify: `src/OrdoSort.Core/BoxLabels.cs` (constants near `:94-107`, `CodeFontSize` `:196`, `ComposeDrawing` `:205-240`, `RenderPdf`/`ComposePdf` `:245-290`)
- Modify (callers, to compile): `src/OrdoSort.Ui/Views/LabelPreview.cs:87,100-146`, `src/OrdoSort.Ui/ViewModels/LabelMakerViewModel.cs` (`RenderPdfTo` `:~792`, `WritePdfForClaim` `:~812`)
- Test: `tests/OrdoSort.Core.Tests/BoxLabelsTests.cs`, `tests/OrdoSort.Wpf.Tests/LabelPrintingTests.cs:50-97`

**Interfaces:**
- Produces:
  - `BoxLabels.LayoutStandard/LayoutBig/LayoutHuge` (string consts), `BoxLabels.NormalizeLayout(string?) → string`
  - `BoxLabels.LabelStyle(string Layout = LayoutStandard, bool LeadingZeros = true, string DateStyle = DateStyleBars)` with `static LabelStyle Default` and `LabelStyle Normalized()`
  - `BoxLabels.NumberText(string code, string layout, bool leadingZeros) → string`, `BoxLabels.NumberFontSize(string text, double maxFont) → double`, `BoxLabels.MinBarcodeHeight = 36`
  - `BoxLabels.ComposeDrawing(Item item, LabelStyle? style = null)`
  - `BoxLabels.RenderPdf(string path, IReadOnlyList<Item> items, LabelStyle? style = null)`, `RenderPdf(Stream, IReadOnlyList<Item>, LabelStyle? style = null)`

- [ ] **Step 1: Write the failing tests** (add to `BoxLabelsTests.cs`)

```csharp
    // ---- label styles (spec 2026-09-28-box-label-style-design) ----------

    private static readonly BoxLabels.Item Plain42 =
        new("ABCD00000042", new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

    /// <summary>The regression pin: the default style draws exactly the
    /// label every existing install prints today.</summary>
    [Fact]
    public void TheDefaultStyleDrawsTodaysLabel()
    {
        var d = BoxLabels.ComposeDrawing(Plain42);

        var code = d.Texts.Single(t => t.Mono);
        Assert.Equal("ABCD 0000 0042", code.Text);
        Assert.Equal((0.0, 24.0, 288.0, 34.0), (code.X, code.Y, code.W, code.H));
        Assert.Equal(BoxLabels.CodeFontSize("ABCD 0000 0042"), code.Size);
        var barcode = d.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).ToList();
        Assert.All(barcode, b => Assert.Equal((66.0, 42.0), (b.Y, b.H)));
        Assert.Equal("CREATED 2026-01-01", d.Texts[0].Text);
    }

    [Theory]
    [InlineData("ABCD00000042", "standard", true, "ABCD 0000 0042")]
    [InlineData("ABCD00000042", "standard", false, "ABCD 42")]
    [InlineData("ABCD00000042", "big", false, "ABCD 42")]
    [InlineData("ABCD00004200", "huge", false, "4200")]
    [InlineData("ABCD00004200", "huge", true, "0000 4200")]
    [InlineData("NOTACODE", "huge", false, "NOTACODE")]   // no 8-digit tail: shown as-is
    public void NumberTextFollowsLayoutAndZeros(string code, string layout, bool zeros, string expected) =>
        Assert.Equal(expected, BoxLabels.NumberText(code, layout, zeros));

    public static TheoryData<string, string, bool, string> EveryStyle()
    {
        var data = new TheoryData<string, string, bool, string>();
        foreach (var code in new[] { "ABCD00000042", "ABCDEFGH99999999", "NGC00004200" })
            foreach (var layout in new[] { BoxLabels.LayoutStandard, BoxLabels.LayoutBig, BoxLabels.LayoutHuge })
                foreach (var zeros in new[] { true, false })
                    foreach (var dates in new[] { BoxLabels.DateStyleBars, BoxLabels.DateStylePlain })
                        data.Add(code, layout, zeros, dates);
        return data;
    }

    [Theory, MemberData(nameof(EveryStyle))]
    public void EveryStyleKeepsTheLabelSafeToPrintAndScan(string code, string layout, bool zeros, string dates)
    {
        var item = new BoxLabels.Item(code, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        var d = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(layout, zeros, dates));
        var standard = BoxLabels.ComposeDrawing(item);
        var barcode = d.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).ToList();
        var number = d.Texts.Single(t => t.Mono);

        // the barcode: same bars across the label as today, never under 0.5 in
        Assert.Equal(standard.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).Select(b => (b.X, b.W)),
            barcode.Select(b => (b.X, b.W)));
        Assert.All(barcode, b => Assert.True(b.H >= BoxLabels.MinBarcodeHeight));
        // inside the label
        Assert.All(d.Bars, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.W <= 288 && b.Y + b.H <= 144));
        Assert.All(d.Texts, t => Assert.True(t.Y >= 0 && t.Y + t.H <= 144));
        // the number fits the width and clears the barcode and the date lines
        Assert.True(number.Size * 0.6 * number.Text.Length <= BoxLabels.LabelWidthPt - 20 + 0.001);
        Assert.All(barcode, b => Assert.True(number.Y + number.H <= b.Y));
        Assert.True(number.Y >= 22 && number.Y + number.H <= 144 - 22);
    }

    [Fact]
    public void HugeMovesTheClientIdIntoTheCreatedLine()
    {
        var item = new BoxLabels.Item("NGC00004200", new DateTime(2026, 9, 28), new DateTime(2033, 9, 26));
        var d = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, LeadingZeros: false));

        Assert.Equal("NGC  ·  CREATED 2026-09-28", d.Texts[0].Text);
        Assert.Equal("4200", d.Texts.Single(t => t.Mono).Text);
        Assert.Equal(72, d.Texts.Single(t => t.Mono).Size);   // capped by height, not width
    }

    [Fact]
    public void BigWithoutZerosPrintsANumberTwiceTodaysSize()
    {
        var d = BoxLabels.ComposeDrawing(Plain42, new BoxLabels.LabelStyle(BoxLabels.LayoutBig, LeadingZeros: false));

        Assert.True(d.Texts.Single(t => t.Mono).Size >= 2 * BoxLabels.CodeFontSize("ABCD 0000 0042") - 0.001);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("neon")]
    public void UnknownOrMissingLayoutIsStandard(string? layout) =>
        Assert.Equal(BoxLabels.LayoutStandard, BoxLabels.NormalizeLayout(layout));
```

In the same file, the two date-style facts (`:176-230`) now pass a style. Replace `BoxLabels.ComposeDrawing(item, BoxLabels.DateStyleBars)` with `BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: BoxLabels.DateStyleBars))`, and the same for `DateStylePlain`. In `LabelPrintingTests.cs:54,95`, `BoxLabels.ComposeDrawing(item, dateStyle)` becomes `BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: dateStyle))`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~BoxLabelsTests"`
Expected: build errors: `LabelStyle`, `NumberText`, `LayoutHuge`, `MinBarcodeHeight` are not defined.

- [ ] **Step 3: Implement in `BoxLabels.cs`**

After `NormalizeDateStyle` (`:107`) add:

```csharp
    // label_layout (owner request 2026-09-28: box numbers readable on a
    // shelf). "standard" is the only layout labels had before; "big" gives
    // the number line the height the barcode gave up; "huge" prints only the
    // running number, the client id moving into the CREATED line.
    public const string LayoutStandard = "standard";
    public const string LayoutBig = "big";
    public const string LayoutHuge = "huge";

    /// <summary>Unknown or missing layout → "standard", so a store written
    /// before this setting prints exactly as it always did.</summary>
    public static string NormalizeLayout(string? layout) =>
        layout is LayoutBig or LayoutHuge ? layout : LayoutStandard;

    /// <summary>How every label in a store prints. Kept in box-labels.json, so
    /// every station and both apps print alike.</summary>
    public sealed record LabelStyle(string Layout = LayoutStandard, bool LeadingZeros = true,
        string DateStyle = DateStyleBars)
    {
        public static LabelStyle Default { get; } = new();

        /// <summary>This style with unknown values replaced by the defaults.</summary>
        public LabelStyle Normalized() =>
            this with { Layout = NormalizeLayout(Layout), DateStyle = NormalizeDateStyle(DateStyle) };
    }

    /// <summary>The printed number: "ABCD 0000 0042" (zeros on) or "ABCD 42"
    /// (off); Huge prints the running number alone ("0000 0042" / "42"). A
    /// code without an 8-digit tail is printed as it is. The barcode always
    /// carries the whole code; this is only what a person reads.</summary>
    public static string NumberText(string code, string layout, bool leadingZeros)
    {
        if (code.Length <= 8 || code[^8..].Any(c => c is < '0' or > '9')) return code;
        var digits = code[^8..];
        var trimmed = digits.TrimStart('0');
        var running = leadingZeros ? $"{digits[..4]} {digits[4..]}" : (trimmed.Length > 0 ? trimmed : "0");
        return NormalizeLayout(layout) == LayoutHuge ? running : $"{code[..^8]} {running}";
    }
```

Replace `CodeFontSize` (`:196-197`) and add the large-layout geometry:

```csharp
    public static double CodeFontSize(string display) => NumberFontSize(display, 26.0);

    /// <summary>The largest size up to <paramref name="maxFont"/> at which
    /// <paramref name="text"/> fits the label width. Consolas is monospaced
    /// (advance ≈ 0.6 em), so every renderer agrees.</summary>
    public static double NumberFontSize(string text, double maxFont) =>
        Math.Min(maxFont, (LabelWidthPt - 20) / (Math.Max(1, text.Length) * 0.6));

    /// <summary>0.5 in: the shortest barcode a hand scanner reads reliably.</summary>
    public const double MinBarcodeHeight = 36;

    // Big and Huge: the number takes 24–78 pt, the barcode 84–120 pt (the
    // 36 pt floor), leaving 6 pt of clear air between them (an angled scan
    // sweep must not catch digit strokes) and 2 pt above the DESTROY line.
    private const double LargeTextTop = BarH + 2, LargeTextHeight = 54;
    private const double LargeBarcodeTop = 84;
    private const double LargeNumberMaxFont = 72;
```

Replace `ComposeDrawing` (`:205-240`) with:

```csharp
    public static LabelDrawing ComposeDrawing(Item item, LabelStyle? style = null)
    {
        var s = (style ?? LabelStyle.Default).Normalized();
        const double w = LabelWidthPt, h = LabelHeightPt;
        var plainDates = s.DateStyle == DateStylePlain;
        var standard = s.Layout == LayoutStandard;
        var bars = new List<BarRect>();
        if (!plainDates)
        {
            bars.Add(new(0, 0, w, BarH));            // CREATED bar
            bars.Add(new(0, h - BarH, w, BarH));      // DESTROY bar
        }
        var number = NumberText(item.Code, s.Layout, s.LeadingZeros);
        // Invariant: these strings are printed on a physical label (and the
        // in-app preview shows exactly what prints) — the date shape can't
        // depend on the printing station's Windows locale.
        var created = $"CREATED {item.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (s.Layout == LayoutHuge && item.Code.Length > 8)
            created = $"{item.Code[..^8]}  ·  {created}";   // Huge's number line has no room for it
        var texts = new List<TextRun>
        {
            new(created, 0, 0, w, BarH, 12, Mono: false, White: !plainDates),
            standard
                ? new(number, 0, BarH + 2, w, 34, CodeFontSize(number), Mono: true, White: false)
                : new(number, 0, LargeTextTop, w, LargeTextHeight,
                    NumberFontSize(number, LargeNumberMaxFont), Mono: true, White: false),
            new($"DESTROY AFTER {item.Destroy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
                0, h - BarH, w, BarH, 12, Mono: false, White: !plainDates),
        };

        // 3:1 wide:narrow ratio; the bar field spans the label minus quiet
        // zones (≥10 narrow units each side keeps hand scanners happy). The
        // 0.18" side insets keep long client ids above ~12 mil bars. Only the
        // height changes with the layout, never below MinBarcodeHeight.
        var (barTop, barHeight) = standard ? (66.0, 42.0) : (LargeBarcodeTop, MinBarcodeHeight);
        var elements = Code39.Encode(item.Code);
        var units = elements.Sum(e => e.Wide ? 3 : 1) + 20;
        var printable = 72 * (4.0 - 0.36);
        var narrow = printable / units;
        var cursor = (w - printable) / 2 + narrow * 10;
        foreach (var e in elements)
        {
            var barW = narrow * (e.Wide ? 3 : 1);
            if (e.Bar) bars.Add(new BarRect(cursor, barTop, barW, barHeight));
            cursor += barW;
        }

        return new LabelDrawing(bars, texts, new BarRect(0, 0, w, h));
    }
```

Update the doc comment above `ComposeDrawing`. Its last sentence becomes: "`style` sets the layout, the leading zeros and the date bars; null is today's label."

In `RenderPdf` (both overloads) and `ComposePdf`, change `string dateStyle = DateStyleBars` to `LabelStyle? style = null` and pass `style` through (`ComposeDrawing(items[i], style)`).

Callers, kept compiling until Task 3 replaces them:
- `LabelPreview.cs:87`: `BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: DateStyle))`
- `LabelPreview.cs:146`: `BoxLabels.ComposeDrawing(_items[i], new BoxLabels.LabelStyle(DateStyle: _dateStyle))`
- `LabelMakerViewModel.RenderPdfTo`: its type becomes `Action<Stream, IReadOnlyList<BoxLabels.Item>, BoxLabels.LabelStyle?>` with default `BoxLabels.RenderPdf`. The call at `:812` becomes `RenderPdfTo(output, RebuildFromClaim(b, start), new BoxLabels.LabelStyle(DateStyle: dateStyle))`. Update any test that assigns `RenderPdfTo` (`grep -rn "RenderPdfTo" tests`) to the three-parameter shape.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~BoxLabels"`, then `dotnet build tests/OrdoSort.Wpf.Tests -c Debug --no-incremental` and `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~Label"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/BoxLabels.cs src/OrdoSort.Ui/Views/LabelPreview.cs src/OrdoSort.Ui/ViewModels/LabelMakerViewModel.cs tests/OrdoSort.Core.Tests/BoxLabelsTests.cs tests/OrdoSort.Wpf.Tests/LabelPrintingTests.cs
git commit -m "feat(labels): Standard, Big and Huge layouts and leading zeros in the label drawing"
```

The message body says why: shelf readability, the barcode floor, today's label pinned.

---

### Task 2: The style in the shared file (Core)

**Files:**
- Modify: `src/OrdoSort.Core/ConfigDocs.cs:8-13`, `src/OrdoSort.Core/BoxLabelStore.cs:193`
- Test: `tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs` (beside `DateStyleRoundTripsAndDefaultsToBars` `:144`)

**Interfaces:**
- Consumes: `BoxLabels.LabelStyle`, `BoxLabels.NormalizeLayout` (Task 1)
- Produces: `BoxLabelsDoc.LabelLayout` (`label_layout`), `BoxLabelsDoc.LeadingZeros` (`leading_zeros`), `BoxLabelsDoc.Style` (`[JsonIgnore]`, get/set, normalised)

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void AStoreWrittenBeforeLabelStylesReadsAsTodaysLabel()
    {
        var p = NewPath();
        File.WriteAllText(p, "{ \"label_clients\": [], \"date_style\": \"plain\" }");

        Assert.Equal(new BoxLabels.LabelStyle(DateStyle: "plain"), BoxLabelStore.Read(p).Style);
    }

    [Fact]
    public void TheLabelStyleRoundTripsThroughTheStore()
    {
        var p = NewPath();
        var style = new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, LeadingZeros: false, BoxLabels.DateStylePlain);

        BoxLabelStore.Mutate(p, d => { d.Style = style; return 0; });

        Assert.Equal(style, BoxLabelStore.Read(p).Style);
        var json = File.ReadAllText(p);
        Assert.Contains("\"label_layout\": \"huge\"", json);
        Assert.Contains("\"leading_zeros\": false", json);
    }

    [Fact]
    public void AnUnknownLayoutInTheFilePrintsAsStandard()
    {
        var p = NewPath();
        File.WriteAllText(p, "{ \"label_clients\": [], \"label_layout\": \"neon\" }");

        Assert.Equal(BoxLabels.LayoutStandard, BoxLabelStore.Read(p).Style.Layout);
    }

    [Fact]
    public void ALeadingZerosValueThatIsNotTrueOrFalseIsReportedNotThrownRaw()
    {
        var p = NewPath();
        File.WriteAllText(p, "{ \"label_clients\": [], \"leading_zeros\": \"no\" }");

        var ex = Assert.Throws<ConfigException>(() => BoxLabelStore.Read(p));
        Assert.Contains("not valid JSON", ex.Message);
    }
```

`NewPath()` is the file's existing temp-path helper (see `:144`). If it's named differently, use that name.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~BoxLabelStoreTests"`
Expected: build errors: `BoxLabelsDoc.Style` is not defined.

- [ ] **Step 3: Implement**

In `ConfigDocs.cs`, after `DateStyle`:

```csharp
    [JsonPropertyName("label_layout")] public string LabelLayout { get; set; } = BoxLabels.LayoutStandard;
    [JsonPropertyName("leading_zeros")] public bool LeadingZeros { get; set; } = true;

    /// <summary>The three label-style keys as one value, normalised: every
    /// station reads the same style from here, and writes it back the same way.</summary>
    [JsonIgnore]
    public BoxLabels.LabelStyle Style
    {
        get => new BoxLabels.LabelStyle(LabelLayout, LeadingZeros, DateStyle).Normalized();
        set
        {
            var s = value.Normalized();
            LabelLayout = s.Layout;
            LeadingZeros = s.LeadingZeros;
            DateStyle = s.DateStyle;
        }
    }
```

In `BoxLabelStore.cs:193`, after `doc.DateStyle = …`, add `doc.LabelLayout = BoxLabels.NormalizeLayout(doc.LabelLayout);`.

If `Read` has its own normalisation line, apply the same there.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~BoxLabel|FullyQualifiedName~Config"`
Expected: PASS. That includes the existing unknown-keys round-trip tests.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/ConfigDocs.cs src/OrdoSort.Core/BoxLabelStore.cs tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs
git commit -m "feat(labels): label_layout and leading_zeros stored in box-labels.json"
```

---

### Task 3: The labels window prints the stored style

**Files:**
- Modify: `src/OrdoSort.Ui/ViewModels/LabelMakerViewModel.cs`
  - ctor `:166`; `DateStyle*` `:348-384` (removed)
  - `ClaimNumbersCore` `:481`; callers `:633`, `:695`, `:803`
  - `SavePdfCoreAsync` `:728-741`; `WritePdfForClaim` `:796-815`
- Modify: `src/OrdoSort.Ui/Views/LabelPreview.cs` (`DateStyle` DP → `Style` DP; `BuildDocument`, `LabelSheetElement` take `LabelStyle`)
- Modify: `src/OrdoSort.Ui/Windows/LabelMakerWindow.xaml:305-334` (remove the Date bars row; preview binds `Style`), `LabelMakerWindow.xaml.cs:114-117`
- Test: `tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs` (replace `:1468-1496`), `tests/OrdoSort.Wpf.Tests/LabelMakerOverflowTests.cs:155-225` (the date-style scroll check goes)

**Interfaces:**
- Consumes: `BoxLabelsDoc.Style` (Task 2), `ComposeDrawing(item, LabelStyle?)` (Task 1)
- Produces:
  - `LabelMakerViewModel.Style` (`BoxLabels.LabelStyle`, raises `PropertyChanged`) and `LabelMakerViewModel.ReloadStyle()`
  - `LabelPreviewControl.Style` (DP, `BoxLabels.LabelStyle`)
  - `LabelPrinting.BuildDocument(items, BoxLabels.LabelStyle? style = null)`

- [ ] **Step 1: Write the failing tests** (replace the two DateStyle facts at `:1468-1496`)

```csharp
    [Fact]
    public void TheStyleIsReadFromTheStoreWhenTheWindowOpens()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 1 });
        var style = new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, false, BoxLabels.DateStylePlain);
        BoxLabelStore.Mutate(path, d => { d.Style = style; return 0; });

        Assert.Equal(style, Vm(path).Style);
    }

    /// <summary>Review focus 2: another station changes the style between
    /// this window opening and the print. The sheets carry the style stored
    /// when the numbers were claimed.</summary>
    [Fact]
    public void PrintingUsesTheStyleStoredWhenTheNumbersAreClaimed()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 1 });
        var vm = Vm(path);
        var changed = new BoxLabels.LabelStyle(BoxLabels.LayoutBig, false);
        BoxLabelStore.Mutate(path, d => { d.Style = changed; return 0; });
        BoxLabels.LabelStyle? printedWith = null;
        vm.LabelCountText = "1";
        vm.PrintSheets = (_, _) => { printedWith = vm.Style; return true; };

        vm.Print();

        Assert.Equal(changed, printedWith);
    }

    [Fact]
    public void SavingAPdfUsesTheStyleStoredWhenTheNumbersAreClaimed()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 1 });
        var vm = Vm(path);
        var changed = new BoxLabels.LabelStyle(BoxLabels.LayoutHuge);
        BoxLabelStore.Mutate(path, d => { d.Style = changed; return 0; });
        BoxLabels.LabelStyle? renderedWith = null;
        vm.RenderPdfTo = (_, _, style) => renderedWith = style;
        vm.LabelCountText = "1";
        _dialogs.NextSaveFile = Path.Combine(_dir, "styled.pdf");

        vm.SavePdf();

        Assert.Equal(changed, renderedWith);
    }

    [Fact]
    public void ReloadStylePicksUpAChangeMadeInSettings()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 1 });
        var vm = Vm(path);
        BoxLabelStore.Mutate(path, d => { d.Style = new(BoxLabels.LayoutBig); return 0; });

        vm.ReloadStyle();

        Assert.Equal(BoxLabels.LayoutBig, vm.Style.Layout);
    }
```

In `LabelMakerOverflowTests.cs`, delete `FindDateStyleChoice` and its use (`:161-170`, `:217-225`). The radios no longer exist, and the rest of that test stays.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Debug --no-incremental`
Expected: build errors: `LabelMakerViewModel.Style` and `ReloadStyle` are not defined.

- [ ] **Step 3: Implement**

`LabelMakerViewModel.cs`: delete the `DateStyle`, `DateStyleBars` and `DateStylePlain` properties and `_dateStyle` (`:348-384`). Add in their place:

```csharp
    // Label style is set in Settings (BoxLabels.exe's, or OrdoSort's Box
    // labels tab), never here: it belongs to the shared store, so every
    // station prints alike. Read at open, re-read with every claim, and on
    // ReloadStyle after Settings changed it.
    private BoxLabels.LabelStyle _style = BoxLabels.LabelStyle.Default;
    public BoxLabels.LabelStyle Style { get => _style; private set => Set(ref _style, value); }

    /// <summary>Re-read the style after Settings changed it. A store that
    /// can't be read keeps the style shown and says why.</summary>
    public void ReloadStyle()
    {
        try { Style = BoxLabelStore.Read(_boxLabelsPath).Style; }
        catch (ConfigException ex) { _dialogs.Warn(ex.Message, _appTitle); }
    }
```

- **Ctor (`:166`):** `_dateStyle = …` becomes `_style = doc.Style;`.
- **`ClaimNumbersCore`:** returns `(long Start, BoxLabels.LabelStyle Style)`. Its lambda ends `return (s, doc.Style);` instead of `return s;`, and its doc comment adds: "and the style stored at that moment".
- **Callers:**
  - **`PrintCoreAsync` (`:633`):** `(start, var claimedStyle) = await …;`, then `Style = claimedStyle;` straight after the `finally`, before `PrintSheets` runs. The window's `PrintSheets` reads `vm.Style`.
  - **`ClaimCopiesAsync` (`:695`):** take `.Start`.
  - **`WritePdfForClaim` (`:803`):** `var (start, style) = ClaimNumbersCore(…)` and `RenderPdfTo(output, RebuildFromClaim(b, start), style)`. Drop the method's `string dateStyle` parameter, and in `SavePdfCoreAsync` drop `var dateStyle = _dateStyle;` and that argument.

`LabelPreview.cs`: replace the `DateStyle` DP with:

```csharp
    public static readonly DependencyProperty StyleProperty = DependencyProperty.Register(
        nameof(Style), typeof(BoxLabels.LabelStyle), typeof(LabelPreviewControl),
        new FrameworkPropertyMetadata(BoxLabels.LabelStyle.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The label style the card is drawn in: the stored one in the
    /// labels window, the one being edited in Settings.</summary>
    public new BoxLabels.LabelStyle Style
    {
        get => (BoxLabels.LabelStyle)GetValue(StyleProperty);
        set => SetValue(StyleProperty, value);
    }
```

`new` hides `FrameworkElement.Style`. If that clash is awkward in XAML, name it `LabelStyle` instead and use that name everywhere below.

`OnRender` calls `BoxLabels.ComposeDrawing(item, Style)`. `BuildDocument(items, BoxLabels.LabelStyle? style = null)` and `LabelSheetElement(items, BoxLabels.LabelStyle? style = null)` store it and pass it to `ComposeDrawing`.

`LabelMakerWindow.xaml`:
- Delete the "Date bars" label and both `RadioButton GroupName="DateStyle"` (`:305-318`), and their grid row.
- `<views:LabelPreviewControl … DateStyle="{Binding DateStyle}"` becomes `Style="{Binding Style}"` (or `LabelStyle=` per the note above).

`LabelMakerWindow.xaml.cs:114,117`: `vm.DateStyle` becomes `vm.Style`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Debug --no-incremental` then `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~Label|FullyQualifiedName~BoxLabels|FullyQualifiedName~WindowOverflow|FullyQualifiedName~AccessibleName"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Ui tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs tests/OrdoSort.Wpf.Tests/LabelMakerOverflowTests.cs
git commit -m "feat(labels): the labels window prints the stored style; date bars leave the window"
```

Before committing, run `git status` and confirm only these files changed. Stage them by name.

---

### Task 4: The shared label style editor

**Files:**
- Create: `src/OrdoSort.Ui/ViewModels/LabelStyleEditorViewModel.cs`, `src/OrdoSort.Ui/Views/LabelStyleEditor.xaml`, `LabelStyleEditor.xaml.cs`
- Test: `tests/OrdoSort.Wpf.Tests/LabelStyleEditorTests.cs`

**Interfaces:**
- Consumes: `BoxLabels.LabelStyle` (Task 1), `LabelPreviewControl.Style` (Task 3)
- Produces: `LabelStyleEditorViewModel(BoxLabels.LabelStyle original)` with:
  - `bool LayoutStandard/LayoutBig/LayoutHuge`, `bool LeadingZeros`, `bool DatesBars/DatesPlain`
  - `BoxLabels.LabelStyle Style`, `bool IsChanged`
  - `BoxLabels.Item SampleItem` (code `ABCD00004200`)

  and `LabelStyleEditor` (a UserControl whose DataContext is that view model).

- [ ] **Step 1: Write the failing tests**

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class LabelStyleEditorTests
{
    [Fact]
    public void TheEditorStartsFromTheStoredStyleAndIsUnchanged()
    {
        var vm = new LabelStyleEditorViewModel(new BoxLabels.LabelStyle(BoxLabels.LayoutBig, false));

        Assert.True(vm.LayoutBig);
        Assert.False(vm.LeadingZeros);
        Assert.True(vm.DatesBars);
        Assert.False(vm.IsChanged);
    }

    [Fact]
    public void PickingHugeAndPlainDatesChangesTheStyleAndSaysSo()
    {
        var vm = new LabelStyleEditorViewModel(BoxLabels.LabelStyle.Default);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.LayoutHuge = true;
        vm.DatesPlain = true;

        Assert.Equal(new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, true, BoxLabels.DateStylePlain), vm.Style);
        Assert.True(vm.IsChanged);
        Assert.Contains(nameof(vm.Style), raised);   // the live preview redraws
    }

    [Fact]
    public void PickingTheOriginalStyleAgainIsNotAChange()
    {
        var vm = new LabelStyleEditorViewModel(BoxLabels.LabelStyle.Default);
        vm.LayoutBig = true;
        vm.LayoutStandard = true;

        Assert.False(vm.IsChanged);
    }
}
```

The class is in namespace `OrdoSort.Wpf.ViewModels`, the `OrdoSort.Ui` assembly's convention (see `LabelMakerViewModel`).

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Debug --no-incremental`
Expected: build error: `LabelStyleEditorViewModel` is not defined.

- [ ] **Step 3: Implement**

`LabelStyleEditorViewModel.cs`. Use the same `ObservableObject` base as `LabelMakerViewModel`:

```csharp
using OrdoSort.Core;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Edits one label style for a Settings page: Box Labels' Label
/// style tab and OrdoSort's Box labels tab. Radio pairs bind to the bool
/// properties; the preview binds <see cref="Style"/>.</summary>
public sealed class LabelStyleEditorViewModel : ObservableObject
{
    private readonly BoxLabels.LabelStyle _original;
    private BoxLabels.LabelStyle _style;

    public LabelStyleEditorViewModel(BoxLabels.LabelStyle original)
    {
        _original = original.Normalized();
        _style = _original;
    }

    public BoxLabels.LabelStyle Style => _style;
    public bool IsChanged => _style != _original;

    /// <summary>What the preview card shows: a realistic mid-range number.</summary>
    public BoxLabels.Item SampleItem { get; } =
        new("ABCD00004200", new DateTime(2026, 9, 28), new DateTime(2033, 9, 26));

    public bool LayoutStandard { get => _style.Layout == BoxLabels.LayoutStandard; set { if (value) Change(_style with { Layout = BoxLabels.LayoutStandard }); } }
    public bool LayoutBig { get => _style.Layout == BoxLabels.LayoutBig; set { if (value) Change(_style with { Layout = BoxLabels.LayoutBig }); } }
    public bool LayoutHuge { get => _style.Layout == BoxLabels.LayoutHuge; set { if (value) Change(_style with { Layout = BoxLabels.LayoutHuge }); } }
    public bool LeadingZeros { get => _style.LeadingZeros; set => Change(_style with { LeadingZeros = value }); }
    public bool DatesBars { get => _style.DateStyle == BoxLabels.DateStyleBars; set { if (value) Change(_style with { DateStyle = BoxLabels.DateStyleBars }); } }
    public bool DatesPlain { get => _style.DateStyle == BoxLabels.DateStylePlain; set { if (value) Change(_style with { DateStyle = BoxLabels.DateStylePlain }); } }

    private void Change(BoxLabels.LabelStyle next)
    {
        if (next == _style) return;
        _style = next;
        foreach (var name in new[] { nameof(Style), nameof(IsChanged), nameof(LayoutStandard), nameof(LayoutBig),
                     nameof(LayoutHuge), nameof(LeadingZeros), nameof(DatesBars), nameof(DatesPlain) })
            Raise(name);
    }
}
```

`LabelStyleEditor.xaml`. Use the app's `SectionHeader`, `FieldLabel` and `CaptionText` styles, and `views:LabelPreviewControl`:

```xml
<UserControl x:Class="OrdoSort.Wpf.Views.LabelStyleEditor"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:OrdoSort.Wpf.Views">
    <!-- Shared by BoxLabels.exe's Label style tab and OrdoSort's Box labels
         tab: one editor, one preview, so the two can't offer different
         choices. The style is kept in the shared box-labels.json. -->
    <StackPanel>
        <TextBlock Text="Layout" Style="{StaticResource SectionHeaderFirst}" />
        <RadioButton GroupName="LabelLayout" Content="Standard (today's label)" IsChecked="{Binding LayoutStandard}" />
        <RadioButton GroupName="LabelLayout" Content="Big number" IsChecked="{Binding LayoutBig}" Margin="0,4,0,0" />
        <RadioButton GroupName="LabelLayout" Content="Huge running number" IsChecked="{Binding LayoutHuge}" Margin="0,4,0,0" />
        <CheckBox Content="Show leading zeros (0000 4200)" IsChecked="{Binding LeadingZeros}" Margin="0,10,0,0" />
        <TextBlock Text="Date lines" Style="{StaticResource SectionHeader}" />
        <RadioButton GroupName="LabelDates" Content="Black bars (white text)" IsChecked="{Binding DatesBars}" />
        <RadioButton GroupName="LabelDates" Content="Plain (black text)" IsChecked="{Binding DatesPlain}" Margin="0,4,0,0" />
        <TextBlock Text="Preview" Style="{StaticResource SectionHeader}" />
        <Border Background="White" BorderBrush="{DynamicResource Theme.Border}" BorderThickness="1"
                CornerRadius="4" Padding="8" HorizontalAlignment="Left">
            <views:LabelPreviewControl Item="{Binding SampleItem}" Style="{Binding Style}"
                                       Width="288" Height="144" AutomationProperties.Name="Label preview" />
        </Border>
        <TextBlock Style="{StaticResource CaptionText}" TextWrapping="Wrap" Margin="0,6,0,0"
                   Text="Every station prints this style. The barcode always holds the full box number." />
    </StackPanel>
</UserControl>
```

Use `LabelStyle=` if Task 3 used that name. `LabelStyleEditor.xaml.cs` holds only `public LabelStyleEditor() => InitializeComponent();` in `namespace OrdoSort.Wpf.Views`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~LabelStyleEditorTests"` (after `dotnet build … --no-incremental`)
Expected: PASS (3).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Ui/ViewModels/LabelStyleEditorViewModel.cs src/OrdoSort.Ui/Views/LabelStyleEditor.xaml src/OrdoSort.Ui/Views/LabelStyleEditor.xaml.cs tests/OrdoSort.Wpf.Tests/LabelStyleEditorTests.cs
git commit -m "feat(labels): one label style editor with a live preview, for both Settings windows"
```

---

### Task 5: Text size and font choices become shared

**Files:**
- Modify: `src/OrdoSort.Ui/Theme/AppFonts.cs`
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs:538-547` (the `FontChoices` array goes), `:2296-2298`, `:2343`
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml:1521`, `src/OrdoSort.Wpf/App.xaml.cs:118-122`
- Modify: `tests/OrdoSort.Wpf.Tests/ContentTemplateSetterTests.cs:394-396`
- Test: `tests/OrdoSort.Wpf.Tests/AppFontsTests.cs`

**Interfaces:**
- Produces: `AppFonts.Choices` (`KeyValuePair<string,string>[]`, same entries as today), `AppFonts.DefaultSize` (14.0), `AppFonts.SizeProblem(string text) → string` ("" when fine), `AppFonts.Apply(Application app, string family, int size)`

- [ ] **Step 1: Write the failing tests**

```csharp
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

public class AppFontsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("6", "")]
    [InlineData("72", "")]
    [InlineData("5", "Base text size must be a number from 6 to 72 (or blank for the default).")]
    [InlineData("big", "Base text size must be a number from 6 to 72 (or blank for the default).")]
    public void TextSizeRulesAreTheSameInBothApps(string text, string problem) =>
        Assert.Equal(problem, AppFonts.SizeProblem(text));

    [Fact]
    public void TheDefaultFontIsTheFirstChoice() => Assert.Equal("", AppFonts.Choices[0].Key);
}
```

- [ ] **Step 2: Run them and watch them fail**

Expected: build error: `AppFonts.SizeProblem` and `AppFonts.Choices` are not defined.

- [ ] **Step 3: Implement**

Move the `FontChoices` array from `SettingsViewModel.cs:538-547` into `AppFonts.cs`, unchanged, as `public static readonly KeyValuePair<string, string>[] Choices`. Then add:

```csharp
    /// <summary>The app text size when none is set.</summary>
    public const double DefaultSize = 14.0;

    /// <summary>"" when <paramref name="text"/> is a usable text size (blank =
    /// default, or 6–72), else what to tell the user. Shared by OrdoSort's and
    /// Box Labels' Settings so the rule and its wording can't differ.</summary>
    public static string SizeProblem(string text) =>
        text.Trim().Length == 0 || (int.TryParse(text.Trim(), out var n) && n is >= 6 and <= 72)
            ? ""
            : "Base text size must be a number from 6 to 72 (or blank for the default).";

    /// <summary>Put a family and size into the resources every window's style
    /// reads (AppFontFamily, AppFontSize). Size 0 is the default.</summary>
    public static void Apply(Application app, string family, int size)
    {
        app.Resources["AppFontFamily"] = Create(family);
        app.Resources["AppFontSize"] = size == 0 ? DefaultSize : size;
    }
```

Add `using System.Windows;` if it's missing.

- `SettingsViewModel`:
  - The inline size check at `:2296-2298` becomes `if (AppFonts.SizeProblem(UiFontSizeText) is { Length: > 0 } sizeProblem) errors.Add(sizeProblem);`.
  - `FontChoices` at `:2343` becomes `AppFonts.Choices`.
  - Add `using OrdoSort.Wpf.Theme;` if it's missing.
- `SettingsWindow.xaml:1521`: `ItemsSource="{x:Static theme:AppFonts.Choices}"`. Declare `xmlns:theme="clr-namespace:OrdoSort.Wpf.Theme;assembly=OrdoSort.Ui"` on the Window if it isn't there.
- `App.ApplyFont` body becomes `Theme.AppFonts.Apply(app, cfg.UiFontFamily, cfg.UiFontSize);`.
- `ContentTemplateSetterTests.cs:394-396`: `SettingsViewModel.FontChoices` becomes `AppFonts.Choices`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~AppFonts|FullyQualifiedName~SettingsViewModel|FullyQualifiedName~ContentTemplateSetter|FullyQualifiedName~Font"` (after rebuild)
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Ui/Theme/AppFonts.cs src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs src/OrdoSort.Wpf/Windows/SettingsWindow.xaml src/OrdoSort.Wpf/App.xaml.cs tests/OrdoSort.Wpf.Tests/ContentTemplateSetterTests.cs tests/OrdoSort.Wpf.Tests/AppFontsTests.cs
git commit -m "refactor(fonts): text size and font choices move to OrdoSort.Ui so Box Labels can use them"
```

---

### Task 6: OrdoSort's Box labels tab

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`: ctor `:640` gains an optional `BoxLabels.LabelStyle? labelStyle = null, string labelStyleProblem = ""`; add `LabelStyle` (editor) and `LabelStyleResult`
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml`: a new `TabItem` before Data files (`:1640`)
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`:
  - add `LabelStyleForSettingsAsync()` beside `FreshConfigForSettingsAsync` (`:347`)
  - `ApplySettings`/`ApplySettingsAsync`/`ApplySettingsCoreAsync` gain `BoxLabels.LabelStyle? labelStyle = null`, written after `_cfg = cfg;` (`:~2172`)
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml.cs:337-362` (`OnSettings`)
- Modify: `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs`: the SettingsWindow probe's `MinExamined` may need re-measuring
- Test: `tests/OrdoSort.Wpf.Tests/SettingsLabelStyleTests.cs`

**Interfaces:**
- Consumes: `LabelStyleEditorViewModel`, `LabelStyleEditor` (Task 4), `BoxLabelsDoc.Style` (Task 2)
- Produces:
  - `SettingsViewModel.LabelStyle` (`LabelStyleEditorViewModel?`, null when the store couldn't be read) and `SettingsViewModel.LabelStyleResult` (`BoxLabels.LabelStyle?`, null when unchanged)
  - `ShellViewModel.LabelStyleForSettingsAsync() → Task<(BoxLabels.LabelStyle? Style, string Problem)>`
  - `ShellViewModel.ApplySettingsAsync(Config cfg, BoxLabels.LabelStyle? labelStyle = null)`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class SettingsLabelStyleTests
{
    public SettingsLabelStyleTests() => SynchronizationContext.SetSynchronizationContext(null);

    private static Config Copy(Config c) => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(c))!;

    [Fact]
    public void AnUnchangedStyleIsNotWritten()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: BoxLabels.LabelStyle.Default);

        Assert.Null(vm.LabelStyleResult);
    }

    [Fact]
    public void AChangedStyleIsTheResult()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: BoxLabels.LabelStyle.Default);
        vm.LabelStyle!.LayoutHuge = true;

        Assert.Equal(BoxLabels.LayoutHuge, vm.LabelStyleResult!.Layout);
    }

    [Fact]
    public void AStoreThatCouldNotBeReadLeavesTheTabWithAReasonAndNothingToSave()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: null,
            labelStyleProblem: "the box-labels file is locked");

        Assert.Null(vm.LabelStyle);
        Assert.Equal("the box-labels file is locked", vm.LabelStyleProblem);
        Assert.Null(vm.LabelStyleResult);
    }

    [Fact]
    public async Task ApplyingWritesTheStyleToTheSharedFile()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var style = new BoxLabels.LabelStyle(BoxLabels.LayoutBig, false);

        await fx.Shell.ApplySettingsAsync(Copy(fx.Cfg), style);

        Assert.Equal(style, BoxLabelStore.Read(fx.Shell.BoxLabelsPath).Style);
        Assert.Empty(fx.Dialogs.Warnings);
    }

    /// <summary>Review focus 5: the file and the style changed in one OK.</summary>
    [Fact]
    public async Task TheStyleGoesToTheNewFileWhenTheFileChangedToo()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var edited = Copy(fx.Cfg);
        edited.BoxLabelsFile = Path.Combine(fx.Dir, "other-labels.json");

        await fx.Shell.ApplySettingsAsync(edited, new BoxLabels.LabelStyle(BoxLabels.LayoutHuge));

        Assert.Equal(BoxLabels.LayoutHuge, BoxLabelStore.Read(edited.BoxLabelsFile).Style.Layout);
    }

    [Fact]
    public async Task AStyleThatCannotBeWrittenIsReportedAndTheOtherSettingsStay()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var edited = Copy(fx.Cfg);
        edited.UppercaseNames = !fx.Cfg.UppercaseNames;
        Directory.CreateDirectory(fx.Shell.BoxLabelsPath);   // a folder where the file should be

        await fx.Shell.ApplySettingsAsync(edited, new BoxLabels.LabelStyle(BoxLabels.LayoutBig));

        // config.json's own save also reports the unwritable labels file
        // (its first-run bootstrap write), so look for ours among them
        Assert.Contains(fx.Dialogs.Warnings, w => w.Message.Contains("Label style not saved"));
        Assert.Equal(edited.UppercaseNames, fx.Shell.Cfg.UppercaseNames);
    }
}
```

`UppercaseNames` stands for any plain config flag; use one that exists on `Config` (check with `grep -n "public bool" src/OrdoSort.Core/Config.cs`). `ShellViewModel.BoxLabelsPath` is `internal`, and the test assembly sees internals.

- [ ] **Step 2: Run them and watch them fail**

Expected: build errors: the `labelStyle` ctor parameter, `LabelStyleResult` and the `ApplySettingsAsync` overload are not defined.

- [ ] **Step 3: Implement**

`SettingsViewModel`:
- Ctor parameters, added last, optional: `BoxLabels.LabelStyle? labelStyle = null, string labelStyleProblem = ""`. In the ctor:

  ```csharp
          LabelStyle = labelStyle is { } s ? new LabelStyleEditorViewModel(s) : null;
          LabelStyleProblem = labelStyleProblem;
  ```

- Properties:

  ```csharp
      /// <summary>The Box labels tab's editor; null when box-labels.json
      /// couldn't be read (the tab then shows <see cref="LabelStyleProblem"/>).</summary>
      public LabelStyleEditorViewModel? LabelStyle { get; }
      public string LabelStyleProblem { get; }
      public bool HasLabelStyle => LabelStyle is not null;
      /// <summary>The style to write to the shared file on OK, or null to leave it.</summary>
      public BoxLabels.LabelStyle? LabelStyleResult => LabelStyle is { IsChanged: true } e ? e.Style : null;
  ```

`SettingsWindow.xaml`, a new tab before Data files:

```xml
            <!-- ============================================== Box labels -->
            <TabItem Header="_Box labels" AutomationProperties.Name="Box labels">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="16,8">
                        <views:LabelStyleEditor DataContext="{Binding LabelStyle}"
                                                Visibility="{Binding DataContext.HasLabelStyle,
                                                    RelativeSource={RelativeSource AncestorType=TabItem},
                                                    Converter={StaticResource BoolToVis}}" />
                        <TextBlock Text="{Binding LabelStyleProblem}" Style="{StaticResource NoteText}"
                                   TextWrapping="Wrap" />
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
```

Declare `xmlns:views="clr-namespace:OrdoSort.Wpf.Views;assembly=OrdoSort.Ui"` if it's missing.

`ShellViewModel`:

```csharp
    /// <summary>The label style for the Settings window's Box labels tab, read
    /// off the UI thread (the file may be on a share). A store that can't be
    /// read gives no style and the reason, and the tab says so.</summary>
    internal Task<(BoxLabels.LabelStyle? Style, string Problem)> LabelStyleForSettingsAsync()
    {
        var path = BoxLabelsPath;
        return _scheduler.Run(() =>
        {
            try { return ((BoxLabels.LabelStyle?)BoxLabelStore.Read(path).Style, ""); }
            catch (ConfigException ex) { return ((BoxLabels.LabelStyle?)null, ex.Message); }
        });
    }
```

Thread `BoxLabels.LabelStyle? labelStyle = null` through `ApplySettings(Config cfg, …)`, `ApplySettingsAsync` and `ApplySettingsCoreAsync`. At the end of `ApplySettingsCoreAsync`, after `_cfg = cfg;` (so `BoxLabelsPath` names the new file) and before `SettingsApplied?.Invoke();`:

```csharp
        // The label style lives in the shared box-labels.json, not config.json,
        // so it is written on its own once config.json has saved; a failure
        // here leaves the other settings in use and says so.
        if (labelStyle is { } style)
        {
            var labelsPath = BoxLabelsPath;
            try
            {
                await _scheduler.Run(() => BoxLabelStore.Mutate(labelsPath, d => { d.Style = style; return 0; }));
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                _dialogs.Warn($"Label style not saved: {ex.Message}\n\nYour other settings were saved.",
                    "OrdoSort — label style not saved");
            }
        }
```

`MainWindow.OnSettings` (`:349-361`): read the style after the config:

```csharp
        Config fresh;
        (BoxLabels.LabelStyle? Style, string Problem) labelStyle;
        try
        {
            fresh = await Shell.FreshConfigForSettingsAsync();
            labelStyle = await Shell.LabelStyleForSettingsAsync();
        }
        finally { _openingSettings = false; }
```

Pass `labelStyle: labelStyle.Style, labelStyleProblem: labelStyle.Problem` to `new SettingsViewModel(…)`, and apply with `Shell.ApplySettings(cfg, vm.LabelStyleResult);`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsLabelStyleTests|FullyQualifiedName~ApplySettings|FullyQualifiedName~SettingsViewModel|FullyQualifiedName~WindowOverflow|FullyQualifiedName~AccessibleName"` (after rebuild)
Expected: PASS. If the Settings `MinExamined` floor in `WindowOverflowTests` fails because the window gained elements, raise it to three quarters of the new measured count and note it in the commit.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs src/OrdoSort.Wpf/Windows/SettingsWindow.xaml src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs src/OrdoSort.Wpf/MainWindow.xaml.cs tests/OrdoSort.Wpf.Tests/SettingsLabelStyleTests.cs tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs
git commit -m "feat(settings): a Box labels tab sets the shared label style"
```

---

### Task 7: Box Labels settings: model and saving

**Files:**
- Modify: `src/BoxLabels.App/Services/LabelsFileSettings.cs` (`LabelsFileDoc` keys; `ReadFont`, `WriteAppearance`)
- Create: `src/BoxLabels.App/Services/BoxLabelsSettingsViewModel.cs`, `src/BoxLabels.App/Services/BoxLabelsSettingsSaver.cs`
- Test: `tests/OrdoSort.Wpf.Tests/BoxLabelsAppSettingsTests.cs` (add), `tests/OrdoSort.Wpf.Tests/BoxLabelsSettingsSaverTests.cs` (new)

**Interfaces:**
- Consumes: `AppFonts.Choices/SizeProblem` (Task 5), `LabelStyleEditorViewModel` (Task 4), `BoxLabelsDoc.Style` (Task 2)
- Produces:
  - `LabelsFileSettings.ReadFont(string settingsPath) → (string Family, int Size)`, `LabelsFileSettings.WriteAppearance(string settingsPath, string theme, string fontFamily, int fontSize)`
  - `BoxLabelsSettingsViewModel(string labelsFile, string theme, string fontFamily, int fontSize, BoxLabels.LabelStyle? style, string styleProblem)` with:
    - `LabelsFile` (settable), `ThemeAuto/ThemeLight/ThemeDark`, `Theme`
    - `UiFontFamily`, `UiFontSizeText`, `UiFontSize`
    - `LabelStyleEditorViewModel? LabelStyle`, `string LabelStyleProblem`, `IReadOnlyList<string> Problems()`
  - `BoxLabelsSettingsSaver.Save(string settingsPath, BoxLabelsSettingsViewModel vm) → BoxLabelsSettingsSaver.Outcome(bool AppearanceSaved, bool StyleSaved, IReadOnlyList<string> Failures)`

- [ ] **Step 1: Write the failing tests**

In `BoxLabelsAppSettingsTests.cs`:

```csharp
    [Fact]
    public void TextSizeAndFontAreRememberedWithoutLosingTheFileOrTheme()
    {
        LabelsFileSettings.Write(SettingsPath, @"\\server\records\box-labels.json");
        LabelsFileSettings.WriteAppearance(SettingsPath, "dark", "Tahoma", 18);

        Assert.Equal(("Tahoma", 18), LabelsFileSettings.ReadFont(SettingsPath));
        Assert.Equal("dark", LabelsFileSettings.ReadTheme(SettingsPath));
        Assert.Equal(@"\\server\records\box-labels.json", LabelsFileSettings.Read(SettingsPath));
    }

    [Fact]
    public void AnOutOfRangeRememberedTextSizeIsTheDefault()
    {
        File.WriteAllText(SettingsPath, "{ \"ui_font_size\": 400 }");

        Assert.Equal(("", 0), LabelsFileSettings.ReadFont(SettingsPath));
    }
```

`BoxLabelsSettingsSaverTests.cs`:

```csharp
using BoxLabelsApp.Services;
using OrdoSort.Core;
using OrdoSort.TestSupport;

namespace OrdoSort.Wpf.Tests;

public class BoxLabelsSettingsSaverTests
{
    private static BoxLabelsSettingsViewModel Vm(string labelsFile) =>
        new(labelsFile, "auto", "", 0, BoxLabels.LabelStyle.Default, "");

    [Fact]
    public void OkSavesAppearanceBesideTheExeAndTheStyleToTheSharedFile()
    {
        using var dir = new TempDir();
        var settings = LabelsFileSettings.PathIn(dir.Path);
        var labels = Path.Combine(dir.Path, "box-labels.json");
        var vm = Vm(labels);
        vm.ThemeDark = true;
        vm.UiFontSizeText = "16";
        vm.LabelStyle!.LayoutHuge = true;

        var outcome = BoxLabelsSettingsSaver.Save(settings, vm);

        Assert.Empty(outcome.Failures);
        Assert.Equal("dark", LabelsFileSettings.ReadTheme(settings));
        Assert.Equal(("", 16), LabelsFileSettings.ReadFont(settings));
        Assert.Equal(BoxLabels.LayoutHuge, BoxLabelStore.Read(labels).Style.Layout);
    }

    /// <summary>Review focus 4: the shared file is unreachable.</summary>
    [Fact]
    public void AStyleThatCannotBeSavedIsReportedAndTheAppearanceStillSaves()
    {
        using var dir = new TempDir();
        var settings = LabelsFileSettings.PathIn(dir.Path);
        var labels = dir.Dir("box-labels.json");   // a folder where the file should be
        var vm = Vm(labels);
        vm.ThemeLight = true;
        vm.LabelStyle!.LayoutBig = true;

        var outcome = BoxLabelsSettingsSaver.Save(settings, vm);

        Assert.True(outcome.AppearanceSaved);
        Assert.False(outcome.StyleSaved);
        Assert.Contains(outcome.Failures, f => f.Contains("Label style not saved"));
        Assert.Equal("light", LabelsFileSettings.ReadTheme(settings));
    }

    [Fact]
    public void AnUnchangedStyleIsNotWritten()
    {
        using var dir = new TempDir();
        var labels = Path.Combine(dir.Path, "box-labels.json");

        BoxLabelsSettingsSaver.Save(LabelsFileSettings.PathIn(dir.Path), Vm(labels));

        Assert.False(File.Exists(labels));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("big")]
    public void ABadTextSizeIsAProblem(string size)
    {
        var vm = Vm("x.json");
        vm.UiFontSizeText = size;

        Assert.Contains(AppFonts.SizeProblem(size), vm.Problems());
    }
}
```

`AppFonts` needs `using OrdoSort.Wpf.Theme;`.

- [ ] **Step 2: Run them and watch them fail**

Expected: build errors: `WriteAppearance`, `ReadFont`, `BoxLabelsSettingsViewModel` and `BoxLabelsSettingsSaver` are not defined.

- [ ] **Step 3: Implement**

`LabelsFileDoc` (in `LabelsFileSettings.cs`), after `Theme`:

```csharp
    /// <summary>The same keys OrdoSort's config.json uses: "" = the default
    /// font, 0 = the default size.</summary>
    [JsonPropertyName("ui_font_family")] public string UiFontFamily { get; set; } = "";
    [JsonPropertyName("ui_font_size")] public int UiFontSize { get; set; }
```

`LabelsFileSettings`:

```csharp
    /// <summary>The remembered font and text size, or the defaults ("" and 0)
    /// when missing, damaged or out of range — a convenience setting, like
    /// the theme.</summary>
    public static (string Family, int Size) ReadFont(string settingsPath)
    {
        var doc = ReadDoc(settingsPath);
        var size = doc.UiFontSize is >= 6 and <= 72 ? doc.UiFontSize : 0;
        return (doc.UiFontFamily?.Trim() ?? "", size);
    }

    /// <summary>Remember the General tab's theme, font and size together.
    /// Throws on an unwritable location, like <see cref="Write"/>; the
    /// remembered labels file is kept.</summary>
    public static void WriteAppearance(string settingsPath, string theme, string fontFamily, int fontSize)
    {
        if (theme is not ("auto" or "light" or "dark"))
            throw new ArgumentException($"theme must be auto, light or dark, got \"{theme}\"", nameof(theme));
        var doc = ReadDoc(settingsPath);
        doc.Theme = theme;
        doc.UiFontFamily = fontFamily;
        doc.UiFontSize = fontSize;
        WriteDoc(settingsPath, doc);
    }
```

`WriteTheme` stays, and the store bar's theme switch still uses it until Task 8 removes that caller. If nothing else calls `WriteTheme` after Task 8, delete it there.

`BoxLabelsSettingsViewModel.cs`:

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;

namespace BoxLabelsApp.Services;

/// <summary>What Box Labels' Settings window edits: the General tab (per
/// PC) and the Label style tab (the shared file).</summary>
public sealed class BoxLabelsSettingsViewModel : ObservableObject
{
    public BoxLabelsSettingsViewModel(string labelsFile, string theme, string fontFamily, int fontSize,
        BoxLabels.LabelStyle? style, string styleProblem)
    {
        _labelsFile = labelsFile;
        _theme = theme is "light" or "dark" ? theme : "auto";
        _uiFontFamily = fontFamily;
        _uiFontSizeText = fontSize == 0 ? "" : fontSize.ToString();
        LabelStyle = style is { } s ? new LabelStyleEditorViewModel(s) : null;
        LabelStyleProblem = styleProblem;
    }

    private string _labelsFile;
    /// <summary>Set by Change… in the window; the host switches to it on OK.</summary>
    public string LabelsFile { get => _labelsFile; set => Set(ref _labelsFile, value); }

    private string _theme;
    public string Theme => _theme;
    public bool ThemeAuto { get => _theme == "auto"; set { if (value) SetTheme("auto"); } }
    public bool ThemeLight { get => _theme == "light"; set { if (value) SetTheme("light"); } }
    public bool ThemeDark { get => _theme == "dark"; set { if (value) SetTheme("dark"); } }
    private void SetTheme(string t)
    {
        if (!Set(ref _theme, t, nameof(Theme))) return;
        Raise(nameof(ThemeAuto)); Raise(nameof(ThemeLight)); Raise(nameof(ThemeDark));
    }

    private string _uiFontFamily;
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }
    private string _uiFontSizeText;
    public string UiFontSizeText { get => _uiFontSizeText; set => Set(ref _uiFontSizeText, value); }
    /// <summary>The size to save; only meaningful once <see cref="Problems"/> is empty.</summary>
    public int UiFontSize => int.TryParse(UiFontSizeText.Trim(), out var n) ? n : 0;

    public LabelStyleEditorViewModel? LabelStyle { get; }
    public string LabelStyleProblem { get; }
    public bool HasLabelStyle => LabelStyle is not null;

    /// <summary>What blocks OK, in words for the user.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (AppFonts.SizeProblem(UiFontSizeText) is { Length: > 0 } size) problems.Add(size);
        if (UiFontFamily.Length > 0 && !AppFonts.Choices.Any(f => f.Key == UiFontFamily))
            problems.Add($"\"{UiFontFamily}\" is not one of the app fonts — pick one from the list.");
        return problems;
    }
}
```

Check `ObservableObject.Set`'s signature (`grep -n "protected bool Set" -r src/OrdoSort.Ui`). If it takes no property name, use `Set(ref _theme, t)` and raise `Theme` by hand.

`BoxLabelsSettingsSaver.cs`:

```csharp
using OrdoSort.Core;

namespace BoxLabelsApp.Services;

/// <summary>Saves what Settings' OK accepted: General beside the exe, the
/// label style (only if changed) to the shared file that will be in use.
/// Each half is tried on its own and reported; nothing here throws.</summary>
public static class BoxLabelsSettingsSaver
{
    public sealed record Outcome(bool AppearanceSaved, bool StyleSaved, IReadOnlyList<string> Failures);

    public static Outcome Save(string settingsPath, BoxLabelsSettingsViewModel vm)
    {
        var failures = new List<string>();
        var appearanceSaved = true;
        try
        {
            LabelsFileSettings.WriteAppearance(settingsPath, vm.Theme, vm.UiFontFamily, vm.UiFontSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            appearanceSaved = false;
            failures.Add($"Theme and text settings not saved ({settingsPath}): {ex.Message}");
        }

        var styleSaved = true;
        if (vm.LabelStyle is { IsChanged: true } edited)
        {
            try
            {
                BoxLabelStore.Mutate(vm.LabelsFile, d => { d.Style = edited.Style; return 0; });
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                styleSaved = false;
                failures.Add($"Label style not saved ({vm.LabelsFile}): {ex.Message}");
            }
        }
        return new Outcome(appearanceSaved, styleSaved, failures);
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~BoxLabelsAppSettings|FullyQualifiedName~BoxLabelsSettingsSaver"` (after rebuild)
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BoxLabels.App/Services/LabelsFileSettings.cs src/BoxLabels.App/Services/BoxLabelsSettingsViewModel.cs src/BoxLabels.App/Services/BoxLabelsSettingsSaver.cs tests/OrdoSort.Wpf.Tests/BoxLabelsAppSettingsTests.cs tests/OrdoSort.Wpf.Tests/BoxLabelsSettingsSaverTests.cs
git commit -m "feat(boxlabels): settings model and saver: appearance per PC, label style to the shared file"
```

---

### Task 8: Box Labels: the menu bar and the Settings window

**Files:**
- Create: `src/BoxLabels.App/Windows/BoxLabelsSettingsWindow.xaml`, `BoxLabelsSettingsWindow.xaml.cs`
- Modify: `src/OrdoSort.Ui/Windows/LabelMakerWindow.xaml:14-60` (the `StoreBar` border becomes a menu plus a read-only path line), `LabelMakerWindow.xaml.cs`
  - `LabelStoreBar` record → `StandaloneMenu(string LabelsFile, Action ChangeFile, Action OpenSettings)`
  - the ctor parameter `storeBar` → `standaloneMenu`
- Modify: `src/BoxLabels.App/App.xaml.cs`
  - apply font at start-up after `ThemeManager.Start`
  - `ShowLabelMaker` passes `StandaloneMenu`
  - `OpenSettings()`
  - `ChangeStoreFile` split so Settings can switch to a chosen file
  - `SetTheme` goes
- Modify: `tests/OrdoSort.Wpf.Tests/LabelStoreBarTests.cs`, renamed to `StandaloneMenuTests.cs`; `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs` (registry entry `BoxLabelsSettingsWindow`)
- Test: `tests/OrdoSort.Wpf.Tests/StandaloneMenuTests.cs`, `tests/OrdoSort.Wpf.Tests/BoxLabelsSettingsWindowTests.cs`

**Interfaces:**
- Consumes: `BoxLabelsSettingsViewModel`, `BoxLabelsSettingsSaver` (Task 7), `LabelStyleEditor` (Task 4), `AppFonts.Apply/Choices` (Task 5), `LabelMakerViewModel.ReloadStyle` (Task 3)
- Produces:
  - `StandaloneMenu(string LabelsFile, Action ChangeFile, Action OpenSettings)`
  - `LabelMakerWindow(vm, windowTitle, previewTitle, bool standalone = false, StandaloneMenu? standaloneMenu = null)`
  - `BoxLabelsSettingsWindow(BoxLabelsSettingsViewModel vm, Func<string?> pickLabelsFile)`, with `ShowDialog() == true` on OK

- [ ] **Step 1: Write the failing tests**

`StandaloneMenuTests.cs`: keep `LabelStoreBarTests`' fixture and window-building helpers, but build with `standaloneMenu: new StandaloneMenu(path, change, settings)`. Keep its path-shown and Change-file tests, retargeted to the menu, and replace the theme-switch tests with:

```csharp
    [Fact]
    public void TheMenuOffersChangeFileAndSettingsAndTheFileIsShown() => _fx.Invoke(() =>
    {
        var changed = 0; var settings = 0;
        var window = Build(new StandaloneMenu(@"\\server\records\box-labels.json", () => changed++, () => settings++));
        try
        {
            window.Show(); Settle(window);
            var items = Descendants<MenuItem>(window).ToList();
            items.Single(i => (string)i.Header == "Change labels _file…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items.Single(i => (string)i.Header == "_Settings…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal((1, 1), (changed, settings));
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text == @"\\server\records\box-labels.json");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OrdoSortsLabelsWindowHasNoMenu() => _fx.Invoke(() =>
    {
        var window = Build(null);
        try
        {
            window.Show(); Settle(window);
            Assert.DoesNotContain(Descendants<Menu>(window), m => m.IsVisible);
        }
        finally { window.Close(); }
    });
```

`Build(StandaloneMenu?)` is the renamed version of the old test file's window builder. Keep its own view-model setup.

`BoxLabelsSettingsWindowTests.cs` (UiTest, same collection as the other window tests):

```csharp
    [Fact]
    public void CancelChangesNothingAndOkWithABadSizeIsRefused() => _fx.Invoke(() =>
    {
        var vm = new BoxLabelsSettingsViewModel("x.json", "auto", "", 0, BoxLabels.LabelStyle.Default, "");
        var window = new BoxLabelsSettingsWindow(vm, () => null) { Left = -20000, ShowActivated = false };
        window.Show(); Settle(window);
        vm.UiFontSizeText = "5";

        Assert.False(window.TryAccept());               // refused, and says why
        Assert.Contains("6 to 72", window.ProblemText);
        window.Close();
    });

    [Fact]
    public void ChangeFilePutsThePickedFileIntoTheSettings() => _fx.Invoke(() =>
    {
        var vm = new BoxLabelsSettingsViewModel("x.json", "auto", "", 0, BoxLabels.LabelStyle.Default, "");
        var window = new BoxLabelsSettingsWindow(vm, () => @"\\server\new.json") { Left = -20000, ShowActivated = false };
        window.Show(); Settle(window);

        window.ChangeFileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(@"\\server\new.json", vm.LabelsFile);
        window.Close();
    });
```

In `WindowOverflowTests.Registry()` add:

```csharp
        ["BoxLabelsSettingsWindow"] = new(520, 620, 520, 640, () =>
            (new BoxLabelsApp.Windows.BoxLabelsSettingsWindow(
                new BoxLabelsApp.Services.BoxLabelsSettingsViewModel(
                    @"\\server\records\a-long-enough-share-name\box-labels.json", "auto", "", 0,
                    BoxLabels.LabelStyle.Default, ""), () => null), null),
            MinExamined: 10, ProbeEveryTab: true),
```

Set `MinExamined` to three quarters of the count the first run reports.

- [ ] **Step 2: Run them and watch them fail**

Expected: build errors: `StandaloneMenu` and `BoxLabelsSettingsWindow` are not defined.

- [ ] **Step 3: Implement**

`LabelMakerWindow.xaml`: replace the `StoreBar` border's contents (`:22-60`). Keep `x:Name="StoreBar"`, `DockPanel.Dock="Top"` and `Visibility="Collapsed"`, and put inside:

```xml
            <DockPanel>
                <Menu DockPanel.Dock="Top" Padding="4,2">
                    <MenuItem Header="_File">
                        <MenuItem x:Name="ChangeFileMenuItem" Header="Change labels _file…" />
                        <Separator />
                        <MenuItem x:Name="ExitMenuItem" Header="E_xit" />
                    </MenuItem>
                    <MenuItem x:Name="SettingsMenuItem" Header="_Settings…" InputGestureText="Ctrl+," />
                </Menu>
                <TextBlock x:Name="StorePathText" Style="{StaticResource SubtleText}" Margin="14,4,14,0"
                           TextTrimming="CharacterEllipsis" AutomationProperties.Name="Labels file in use" />
            </DockPanel>
```

`LabelMakerWindow.xaml.cs`:
- Replace the `LabelStoreBar` record and its doc with:

  ```csharp
  /// <summary>BoxLabels.exe's menu: change the shared labels file, open Settings.
  /// Null in OrdoSort, whose own Settings and Data files tab own these.</summary>
  public sealed record StandaloneMenu(string LabelsFile, Action ChangeFile, Action OpenSettings);
  ```

- In the ctor, replace the `storeBar` block with:

  ```csharp
          if (standaloneMenu is not null)
          {
              StoreBar.Visibility = Visibility.Visible;
              StorePathText.Text = standaloneMenu.LabelsFile;
              StorePathText.ToolTip = standaloneMenu.LabelsFile;
              ChangeFileMenuItem.Click += (_, _) => standaloneMenu.ChangeFile();
              ExitMenuItem.Click += (_, _) => Close();
              SettingsMenuItem.Click += (_, _) => standaloneMenu.OpenSettings();
              InputBindings.Add(new KeyBinding(new RelayCommand(standaloneMenu.OpenSettings),
                  Key.OemComma, ModifierKeys.Control));
          }
  ```

  Keep the existing height-compensation code that followed. Delete the theme-switch XAML and code.
- `using System.Windows.Input;` and the Ui assembly's `RelayCommand` namespace, if missing.

`BoxLabelsSettingsWindow.xaml` (in `BoxLabels.App/Windows`, `x:Class="BoxLabelsApp.Windows.BoxLabelsSettingsWindow"`). It has a `TabControl` with two tabs and a bottom OK/Cancel row, mirroring OrdoSort's Settings footer:

- **General:**
  - "Labels file": a read-only `TextBox` bound to `LabelsFile`, plus a `Button x:Name="ChangeFileButton" Content="Change…"`.
  - "Theme": three radios bound to `ThemeAuto/ThemeLight/ThemeDark`.
  - "Font": a `ComboBox` with `ItemsSource="{x:Static theme:AppFonts.Choices}"`, `SelectedValuePath="Key"` and `SelectedValue="{Binding UiFontFamily}"`, and `ItemTemplate="{StaticResource FontChoiceTemplate}"` if that resource is in `OrdoSort.Ui`'s Styles.
  - "Text size": a `TextBox` bound to `UiFontSizeText` with the caption "6–72 · blank = default".
- **Label style:**
  - `<views:LabelStyleEditor DataContext="{Binding LabelStyle}" />`, visible when `HasLabelStyle`.
  - A `NoteText` block bound to `LabelStyleProblem`.
- **Footer:**
  - A `TextBlock x:Name="ProblemLine"` in `NoteText` style.
  - `Button Content="OK" IsDefault="True" Click="OnOk" Style="{StaticResource PrimaryButton}"` and `Button Content="Cancel" IsCancel="True"`.

`BoxLabelsSettingsWindow.xaml.cs`:

```csharp
public partial class BoxLabelsSettingsWindow : Window
{
    private readonly BoxLabelsSettingsViewModel _vm;

    public BoxLabelsSettingsWindow(BoxLabelsSettingsViewModel vm, Func<string?> pickLabelsFile)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ChangeFileButton.Click += (_, _) => { if (pickLabelsFile() is { } picked) vm.LabelsFile = picked; };
    }

    internal string ProblemText => ProblemLine.Text;

    /// <summary>OK's check: refuses while anything is wrong, saying what.</summary>
    internal bool TryAccept()
    {
        var problems = _vm.Problems();
        ProblemLine.Text = string.Join("\n", problems);
        return problems.Count == 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryAccept()) DialogResult = true;
    }
}
```

`App.xaml.cs`:
- **Start-up:** after `ThemeManager.Start(…)` and the `AppFontFamily` line, add

  ```csharp
          var (family, size) = LabelsFileSettings.ReadFont(_settingsPath);
          AppFonts.Apply(this, family, size);
  ```

- **`ShowLabelMaker`:** pass `standaloneMenu: new StandaloneMenu(_labelsFile, ChangeStoreFile, OpenSettings)` and keep `vm` in a field `_labelMaker`, so Settings can call `ReloadStyle()`.
- **`ChangeStoreFile`:** split into `ChangeStoreFile()`, which picks, and `SwitchTo(string chosen)`, which holds the existing close / remember / reopen body from `var old = MainWindow;` on. `ChangeStoreFile` becomes `if (_chooser.PickAndCheck() is { } chosen) SwitchTo(chosen);`, with the same-file early return moved into `SwitchTo`.
- **`OpenSettings`:**

```csharp
    /// <summary>File → Settings…: edit, then save each half and apply what saved.
    /// The label style is read for the file that will be in use.</summary>
    private void OpenSettings()
    {
        var (family, size) = LabelsFileSettings.ReadFont(_settingsPath);
        BoxLabels.LabelStyle? style = null;
        var styleProblem = "";
        try { style = BoxLabelStore.Read(_labelsFile).Style; }
        catch (ConfigException ex) { styleProblem = ex.Message; }
        var vm = new BoxLabelsSettingsViewModel(_labelsFile, ThemeManager.Mode, family, size, style, styleProblem);
        var window = new BoxLabelsSettingsWindow(vm, () => _chooser.PickAndCheck()) { Owner = MainWindow };
        if (window.ShowDialog() != true) return;

        var outcome = BoxLabelsSettingsSaver.Save(_settingsPath, vm);
        if (outcome.AppearanceSaved)
        {
            ThemeManager.SetMode(this, vm.Theme);
            AppFonts.Apply(this, vm.UiFontFamily, vm.UiFontSize);
        }
        if (outcome.Failures.Count > 0)
            _dialogs.Warn(string.Join("\n\n", outcome.Failures), Title);
        if (!string.Equals(vm.LabelsFile, _labelsFile, StringComparison.OrdinalIgnoreCase))
            SwitchTo(vm.LabelsFile);          // the new window reads the style fresh
        else if (outcome.StyleSaved)
            _labelMaker?.ReloadStyle();
    }
```

Here the style write goes to `vm.LabelsFile`, the file that will be in use; the saver already does this. Delete `SetTheme` (the store bar is gone), and `LabelsFileSettings.WriteTheme` if nothing calls it now (`grep -rn WriteTheme src tests`); update its tests to `WriteAppearance`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --no-build --filter "FullyQualifiedName~StandaloneMenu|FullyQualifiedName~BoxLabels|FullyQualifiedName~WindowOverflow|FullyQualifiedName~AccessibleName|FullyQualifiedName~LabelMaker"` (after rebuild)
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Ui/Windows/LabelMakerWindow.xaml src/OrdoSort.Ui/Windows/LabelMakerWindow.xaml.cs src/BoxLabels.App tests/OrdoSort.Wpf.Tests/StandaloneMenuTests.cs tests/OrdoSort.Wpf.Tests/BoxLabelsSettingsWindowTests.cs tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs tests/OrdoSort.Wpf.Tests/BoxLabelsAppSettingsTests.cs
git rm tests/OrdoSort.Wpf.Tests/LabelStoreBarTests.cs
git commit -m "feat(boxlabels): a File/Settings menu and a Settings window replace the store bar"
```

---

### Task 9: Full checks, docs and the owner's printed check

**Files:**
- Modify: `README.md` (the Box Labels section: Settings, label styles, where each setting is saved), `STATUS.md` (Now row), `docs/ux-audit-2026-09-28.md` (no changes unless an item was touched)

- [ ] **Step 1:** `check.bat`. Expected: green (format, Release build, Core and Wpf everyday tests).
- [ ] **Step 2:** `check.bat integration`, then `scripts\e2e.bat`. Expected: 27/27 and 48/48. If the Box labels E2E scenarios read the Date bars radio, retarget them to the stored style, with the reason in the commit.
- [ ] **Step 3:** Render check with a temporary harness test (never committed, as in `ZzRenderScratch` on 2026-09-28):
  - `BoxLabelsSettingsWindow`, both tabs, light and dark;
  - OrdoSort's Box labels tab;
  - one PDF per layout, via `BoxLabels.RenderPdf(path, items, style)`.

  Look at each picture before deleting the harness.
- [ ] **Step 4:** Update README and STATUS, and commit (`docs: label styles and Box Labels Settings`).
- [ ] **Step 5:** Ask the owner for the printed check: one sheet per layout at 100% scale, scanned with their scanner and read from shelf distance. Record the result in STATUS.

## Self-review notes

**Spec coverage:**

| Spec section | Task |
|---|---|
| Label styles and their rules | 1 |
| Storage | 2 |
| The labels window | 3 |
| Shared pieces | 4, 5 |
| OrdoSort's tab | 6 |
| Box Labels' General and saving | 7 |
| The menu and Settings window | 8 |
| Testing table and success criteria | 1–9; the printed check in 9 |

**Deliberate deviation from the spec:**
- **Spec:** "the preview updates when the window regains focus after Settings closes."
- **Plan:** Box Labels calls `ReloadStyle()` straight after Settings OK. OrdoSort's labels window can't be open while its Settings is, and it reads the style when it opens.
- **Why:** same result for the user, with no focus-event timing to test.
