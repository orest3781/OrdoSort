using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The config keys: exact JSON names, defaults, validation.</summary>
public class ConfigNewKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordonk_" + Guid.NewGuid());

    public ConfigNewKeysTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private Config RoundTrip(Config cfg)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".json");
        Config.Save(cfg, path);
        return Config.Load(path);
    }

    private Config LoadJson(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        return Config.Load(path);
    }

    [Fact]
    public void NewKeysRoundTripWithExactJsonNames()
    {
        var cfg = new Config
        {
            UiFontFamily = "Verdana",
            UiFontSize = 13,
            WordSeparator = "-",
            SavedPasswords = { new SavedPassword { Label = "Payer A", Password = "dpapi:abc" } },
            MergeRoster = @"C:\rosters\august.xlsx",
            MergeColumns = { "Last", "First", "DOB" },
        };
        var path = Path.Combine(_dir, "t.json");
        Config.Save(cfg, path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"ui_font_family\"", json);
        Assert.Contains("\"ui_font_size\"", json);
        Assert.Contains("\"word_separator\"", json);
        Assert.Contains("\"saved_passwords\"", json);
        Assert.Contains("\"merge_roster\"", json);
        Assert.Contains("\"merge_columns\"", json);
        // "unlock_suffix" was retired when the unlock tool stopped having a
        // setting; it must not come back as a key the app writes
        Assert.DoesNotContain("\"unlock_suffix\"", json);
        // the reports feature's nine config keys were retired when the
        // Turn-around time / Production report windows and engines were
        // removed; none of them may come back as keys the app writes
        Assert.DoesNotContain("\"tat_report_folder\"", json);
        Assert.DoesNotContain("\"tat_headers\"", json);
        Assert.DoesNotContain("\"tat_threshold_days\"", json);
        Assert.DoesNotContain("\"tat_ignored_sources\"", json);
        Assert.DoesNotContain("\"reports_upload_folder\"", json);
        Assert.DoesNotContain("\"production_csv_folder\"", json);
        Assert.DoesNotContain("\"production_group_columns\"", json);
        Assert.DoesNotContain("\"production_sum_columns\"", json);
        Assert.DoesNotContain("\"production_datetime_column\"", json);

        var back = Config.Load(path);
        Assert.Equal("Verdana", back.UiFontFamily);
        Assert.Equal(13, back.UiFontSize);
        Assert.Equal("-", back.WordSeparator);
        var pw = Assert.Single(back.SavedPasswords);
        Assert.Equal("Payer A", pw.Label);
        Assert.Equal("dpapi:abc", pw.Password);
        Assert.Equal(@"C:\rosters\august.xlsx", back.MergeRoster);
        Assert.Equal(new[] { "Last", "First", "DOB" }, back.MergeColumns);
    }

    [Fact]
    public void DefaultsAreBenign()
    {
        var cfg = RoundTrip(new Config());
        Assert.Equal("", cfg.UiFontFamily);
        Assert.Equal(0, cfg.UiFontSize);
        Assert.Equal("", cfg.WordSeparator);
        Assert.Empty(cfg.SavedPasswords);
        Assert.Equal("", cfg.MergeRoster);
        Assert.Empty(cfg.MergeColumns);
    }

    [Fact]
    public void SoundSettingsRoundTripWithSaneDefaults()
    {
        var cfg = RoundTrip(new Config());
        Assert.True(cfg.Sounds.Enabled);
        Assert.Equal("", cfg.Sounds.NewAlert);      // "" = built-in OrdoSort sound
        Assert.Equal("none", cfg.Sounds.Filed);
        Assert.Equal("none", cfg.Sounds.SetAside);

        cfg.Sounds.Enabled = false;
        cfg.Sounds.NewAlert = @"C:\sounds\uh-oh.wav";
        var back = RoundTrip(cfg);
        Assert.False(back.Sounds.Enabled);
        Assert.Equal(@"C:\sounds\uh-oh.wav", back.Sounds.NewAlert);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(73)]
    [InlineData(-1)]
    public void FontSizeOutsideRangeIsRejected(int size)
    {
        var ex = Assert.Throws<ConfigException>(() =>
            LoadJson($"{{ \"ui_font_size\": {size} }}"));
        Assert.Contains("ui_font_size", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(72)]
    public void FontSizeZeroOrInRangeIsAccepted(int size) =>
        Assert.Equal(size, LoadJson($"{{ \"ui_font_size\": {size} }}").UiFontSize);

    [Fact]
    public void SeparatorContainingASpaceIsRejected()
    {
        // a space in the separator would re-trigger substitution forever
        var ex = Assert.Throws<ConfigException>(() =>
            LoadJson("{ \"word_separator\": \" - \" }"));
        Assert.Contains("word_separator", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("_")]
    public void SaneSeparatorsAreAccepted(string sep) =>
        Assert.Equal(sep, LoadJson($"{{ \"word_separator\": \"{sep}\" }}").WordSeparator);

    [Fact]
    public void PollSecondsDefaultsTo15AndRoundTrips()
    {
        Assert.Equal(15, RoundTrip(new Config()).PollSeconds);
        Assert.Equal(5, RoundTrip(new Config { PollSeconds = 5 }).PollSeconds);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(0)]
    public void PollSecondsOutOfRangeIsRejected(int secs)
    {
        var ex = Assert.Throws<ConfigException>(() =>
            LoadJson($"{{ \"poll_seconds\": {secs} }}"));
        Assert.Contains("poll_seconds", ex.Message);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("light")]
    [InlineData("dark")]
    public void ThemeAcceptsTheThreeModes(string mode) =>
        Assert.Equal(mode, LoadJson($"{{ \"theme\": \"{mode}\" }}").Theme);

    [Fact]
    public void ThemeDefaultsToAutoAndRejectsGarbage()
    {
        Assert.Equal("auto", RoundTrip(new Config()).Theme);
        var ex = Assert.Throws<ConfigException>(() => LoadJson("{ \"theme\": \"blue\" }"));
        Assert.Contains("theme", ex.Message);
    }

    // Named theme schemes (2026-08-08): "theme" also accepts a
    // Config.SchemeKeys entry, additive alongside the legacy auto/light/dark
    // trio — see Config.SchemeKeys' own doc comment for why this list is
    // hand-mirrored from OrdoSort.Wpf.Theme.ThemePalette.Schemes rather than
    // referenced directly (Core cannot reference Wpf).
    [Theory]
    [InlineData("paper")]
    [InlineData("graphite")]
    [InlineData("ledger")]
    [InlineData("microfilm")]
    [InlineData("manila")]
    [InlineData("carbon")]
    [InlineData("blueprint")]
    public void ThemeAcceptsSchemeKeys(string scheme) =>
        Assert.Equal(scheme, LoadJson($"{{ \"theme\": \"{scheme}\" }}").Theme);

    // Validation is case-SENSITIVE today for auto/light/dark (the pattern
    // match `cfg.Theme is not ("auto" or "light" or "dark")` is an ordinal
    // string comparison) — "AUTO" already throws before this change, and
    // scheme keys join that exact same check, so they inherit the same
    // case-sensitivity rather than gaining their own case-insensitive rule.
    [Theory]
    [InlineData("AUTO")]
    [InlineData("Light")]
    [InlineData("DARK")]
    [InlineData("PAPER")]
    [InlineData("Graphite")]
    public void ThemeRejectsCaseVariantsJustLikeItAlwaysHas(string mode)
    {
        var ex = Assert.Throws<ConfigException>(() => LoadJson($"{{ \"theme\": \"{mode}\" }}"));
        Assert.Contains("theme", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    public void ThemeStillRejectsBlankAndUnknownValues(string mode)
    {
        var ex = Assert.Throws<ConfigException>(() => LoadJson($"{{ \"theme\": \"{mode}\" }}"));
        Assert.Contains("theme", ex.Message);
    }

    [Fact]
    public void ThemeDarkRoundTripsUnchanged() =>
        Assert.Equal("dark", RoundTrip(new Config { Theme = "dark" }).Theme);

    [Fact]
    public void ThemePaperRoundTripsUnchanged() =>
        Assert.Equal("paper", RoundTrip(new Config { Theme = "paper" }).Theme);
}
