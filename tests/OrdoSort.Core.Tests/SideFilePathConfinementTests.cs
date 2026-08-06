using System.Text.Json;

namespace OrdoSort.Core.Tests;

/// <summary>Side files (destinations_file, monitored_folders_file, alerts_file,
/// box_labels_file) must stay beside config.json — 2026-08 audit finding
/// 4.2[A]: an unconfined rooted path let anyone who can edit config.json on
/// a shared config overwrite an arbitrary file on every OTHER station's
/// local disk at that station's next Save. WRITE refuses any path that
/// resolves outside the config directory, with no exception. READ keeps
/// loading an already-configured fully-qualified absolute path (the
/// Settings "Data files" Browse... buttons have always been able to
/// produce one — see task-1-report.md), but refuses a `..` traversal or a
/// Windows rooted-without-drive path the same as a write, since the UI
/// never produces those and a malicious shared config could otherwise use
/// them to make a victim station read an arbitrary local file.
///
/// Every "escaping" fixture below stays inside a disposable temp directory
/// this process fully owns — never a real system path like
/// C:\Windows\Temp — so a test proving a write is REFUSED can never
/// accidentally leave (or fail to clean up) a stray file somewhere on the
/// real machine if confinement regresses.</summary>
public class SideFilePathConfinementTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoconfine_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    // A `..` traversal and a Windows rooted-without-drive path both resolve
    // relative to this process's own current drive/profile (never a
    // protected system folder), so they're safe to use as literals.
    public static IEnumerable<object[]> RelativeEscapes()
    {
        yield return new object[] { @"..\..\evil.json" };
        yield return new object[] { @"\evil.json" };
    }

    // ================= WRITE: Save =================

    [Theory]
    [MemberData(nameof(RelativeEscapes))]
    public void SaveRefusesAnEscapingDestinationsFile(string escaping)
    {
        var cfg = new Config { DestinationsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(RelativeEscapes))]
    public void SaveRefusesAnEscapingMonitoredFoldersFile(string escaping)
    {
        var cfg = new Config { MonitoredFoldersFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("monitored_folders_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(RelativeEscapes))]
    public void SaveRefusesAnEscapingAlertsFile(string escaping)
    {
        var cfg = new Config { AlertsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("alerts_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(RelativeEscapes))]
    public void SaveRefusesAnEscapingBoxLabelsFile(string escaping)
    {
        var cfg = new Config { BoxLabelsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(RelativeEscapes))]
    public void TrySaveReportsAnEscapingDestinationsFileInsteadOfThrowing(string escaping)
    {
        var cfg = new Config { DestinationsFile = escaping };
        var ok = Config.TrySave(cfg, ConfigPath, out var error);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
    }

    [Fact]
    public void SaveRefusesAnAbsolutePathOutsideTheConfigDirectory()
    {
        // The brief's own example (an absolute path like C:\Windows\Temp\
        // evil.json) is illustrative — this test proves the same escape
        // using a directory the test process fully owns (a sibling of the
        // real config dir, not a shared system path) so the assertion that
        // nothing landed there is airtight and needs no cleanup of a real
        // machine location.
        var outsideDir = Directory.CreateTempSubdirectory("ordoconfine_abs_").FullName;
        try
        {
            var outsideFile = Path.Combine(outsideDir, "evil.json");
            var cfg = new Config { DestinationsFile = outsideFile };
            var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
            Assert.Contains("destinations_file", ex.Message);
            Assert.Contains(outsideFile, ex.Message);
            Assert.False(File.Exists(outsideFile));
        }
        finally { try { Directory.Delete(outsideDir, true); } catch { } }
    }

    [Fact]
    public void TrySaveStillSavesTheOtherFilesWhenOneKeyEscapes()
    {
        // Each side file is attempted independently (TrySave's documented
        // contract) — a bad destinations_file must not block alerts.json,
        // monitored-folders.json, or the main config from saving, and must
        // not actually write anything at the escaping location.
        var outsideDir = Directory.CreateTempSubdirectory("ordoconfine_abs_").FullName;
        try
        {
            var outsideFile = Path.Combine(outsideDir, "evil.json");
            var cfg = new Config { DestinationsFile = outsideFile };
            var ok = Config.TrySave(cfg, ConfigPath, out var error);
            Assert.False(ok);
            Assert.Contains("destinations_file", error);
            Assert.True(File.Exists(ConfigPath));
            Assert.True(File.Exists(Path.Combine(_dir, "alerts.json")));
            Assert.True(File.Exists(Path.Combine(_dir, "monitored-folders.json")));
            Assert.False(File.Exists(outsideFile));
        }
        finally { try { Directory.Delete(outsideDir, true); } catch { } }
    }

    // ---- confinement is not over-tight: legitimate relative paths still work ----

    [Fact]
    public void SaveAcceptsAPlainFilename()
    {
        var cfg = new Config { DestinationsFile = "my-destinations.json" };
        Config.Save(cfg, ConfigPath);
        Assert.True(File.Exists(Path.Combine(_dir, "my-destinations.json")));
    }

    [Fact]
    public void SaveAcceptsANestedRelativePath()
    {
        // As in ConfigSplitTests.RelativeSectionPathResolvesBesideConfig,
        // the subfolder is pre-created: Save (like ResolveBeside before it)
        // resolves a nested relative path but was never responsible for
        // vivifying arbitrary subdirectory trees, on write any more than on
        // read — that is orthogonal to confinement, which is what this test
        // is proving still permits the nested path.
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        var cfg = new Config { DestinationsFile = @"data\destinations.json" };
        Config.Save(cfg, ConfigPath);
        Assert.True(File.Exists(Path.Combine(_dir, "data", "destinations.json")));
    }

    // ================= READ: Load / ReadDoc =================

    [Fact]
    public void LoadStillReadsAnExistingAbsoluteDestinationsFile()
    {
        // Step 1 finding: the Settings "Data files" Browse... buttons use
        // Microsoft.Win32.OpenFileDialog for all four keys and always hand
        // back a full path with no relativizing — an absolute side-file
        // path is a real, UI-reachable, already-shipped capability. Load
        // must keep reading a file that's already sitting there — genuinely
        // OUTSIDE the config directory, not merely a nested subfolder — or
        // an existing station's data silently vanishes out from under it.
        var otherDir = Directory.CreateTempSubdirectory("ordoconfine_abs_").FullName;
        try
        {
            var absoluteDests = Path.Combine(otherDir, "abs-destinations.json");
            File.WriteAllText(absoluteDests, """{"routes":[{"label":"OUTSIDE","path":"C:/x"}]}""");

            var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = absoluteDests });
            File.WriteAllText(ConfigPath, configJson);

            var cfg = Config.Load(ConfigPath);
            Assert.Equal("OUTSIDE", Assert.Single(cfg.Routes).Label);
        }
        finally { try { Directory.Delete(otherDir, true); } catch { } }
    }

    [Fact]
    public void LoadRefusesATraversalDestinationsFile()
    {
        var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = @"..\..\evil.json" });
        File.WriteAllText(ConfigPath, configJson);
        var ex = Assert.Throws<ConfigException>(() => Config.Load(ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
    }

    [Fact]
    public void LoadRefusesARootedWithoutDriveDestinationsFile()
    {
        var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = @"\evil.json" });
        File.WriteAllText(ConfigPath, configJson);
        var ex = Assert.Throws<ConfigException>(() => Config.Load(ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
    }

    [Fact]
    public void LoadStillAcceptsAPlainFilenameAndANestedRelativePath()
    {
        File.WriteAllText(Path.Combine(_dir, "plain.json"),
            """{"routes":[{"label":"PLAIN","path":"C:/p"}]}""");
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = "plain.json" }));
        Assert.Equal("PLAIN", Assert.Single(Config.Load(ConfigPath).Routes).Label);

        var sub = Directory.CreateDirectory(Path.Combine(_dir, "nested")).FullName;
        File.WriteAllText(Path.Combine(sub, "dests.json"),
            """{"routes":[{"label":"NESTED","path":"C:/n"}]}""");
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = @"nested\dests.json" }));
        Assert.Equal("NESTED", Assert.Single(Config.Load(ConfigPath).Routes).Label);
    }

    // ================= the classic prefix hole =================

    [Fact]
    public void ASiblingDirectoryThatSharesTheConfigDirsNameAsAPrefixDoesNotCount()
    {
        // C:\...\ordoconfine_XXXX-evil\x.json starts with the STRING
        // C:\...\ordoconfine_XXXX (the real config dir) but is not inside
        // it — a naive StartsWith on the raw path string would wrongly
        // accept this. Build that sibling next to the real config dir and
        // point a side-file key at a file inside it.
        var evilSibling = _dir + "-evil";
        Directory.CreateDirectory(evilSibling);
        try
        {
            var evilFile = Path.Combine(evilSibling, "x.json");
            var cfg = new Config { DestinationsFile = evilFile };
            var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
            Assert.Contains("destinations_file", ex.Message);
            Assert.False(File.Exists(evilFile));
        }
        finally { try { Directory.Delete(evilSibling, true); } catch { } }
    }
}
