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
/// Every "escaping" fixture below is built to resolve inside a disposable
/// root this process fully owns — NEVER a real, pre-existing user-machine
/// path — even when the guard being tested is disabled. That is not just a
/// tidiness goal: an earlier version of this file used real-looking
/// literals (`..\..\evil.json`, `\evil.json`) that, under a manual
/// guard-disabled teeth-proof run, actually landed at `S:\evil.json` and
/// `C:\Users\<user>\AppData\Local\evil.json` — confirmed on disk. See the
/// drive-root comment below for why a rooted-without-drive escape in
/// particular forces this design, not just a nested %TEMP% subfolder.</summary>
public class SideFilePathConfinementTests : IDisposable
{
    // A Windows rooted-without-drive path (`\name\...`) is resolved by
    // Path.GetFullPath against the CURRENT DRIVE'S ROOT, never against any
    // nested directory — so there is no literal of that FORM that can be
    // made to land inside an ordinary %TEMP%-nested folder. The only way to
    // keep such a literal fully inside space this test owns is to make the
    // owned root itself a direct child of the drive root, and spell the
    // literal as "\<that child's own folder name>\evil.json" — which
    // Path.GetFullPath resolves right back to DriveRoot\<name>\evil.json,
    // i.e. squarely inside the owned root. A `..` traversal is pointed at
    // the SAME root, one level above the config directory, so both
    // escaping forms land in one place this test both owns and disposes.
    private static readonly string DriveRoot = Path.GetPathRoot(Directory.GetCurrentDirectory())!;

    private readonly string _testRoot =
        Path.Combine(DriveRoot, "ordoconfine_root_" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;

    public SideFilePathConfinementTests() =>
        _dir = Directory.CreateDirectory(Path.Combine(_testRoot, "configdir")).FullName;

    public void Dispose() { try { Directory.Delete(_testRoot, true); } catch { } }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    // A separate subfolder of _testRoot for the "existing absolute path"
    // tests below — outside _dir (the config directory) but still inside
    // the one root this instance disposes.
    private string OutsideDir => Directory.CreateDirectory(Path.Combine(_testRoot, "outside")).FullName;

    // _dir == _testRoot\configdir\, so one level up is _testRoot itself:
    // outside the config directory, still inside the owned root.
    private const string TraversalEscape = @"..\evil.json";

    // Resolves (Path.GetFullPath, against the current drive's root) to
    // DriveRoot\<_testRoot's folder name>\evil.json == _testRoot\evil.json.
    private string RootedWithoutDriveEscape => $@"\{Path.GetFileName(_testRoot)}\evil.json";

    public static IEnumerable<object[]> EscapeKinds()
    {
        yield return new object[] { "traversal" };
        yield return new object[] { "rooted-without-drive" };
    }

    private string Escape(string kind) => kind switch
    {
        "traversal" => TraversalEscape,
        "rooted-without-drive" => RootedWithoutDriveEscape,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ================= WRITE: Save =================

    [Theory]
    [MemberData(nameof(EscapeKinds))]
    public void SaveRefusesAnEscapingDestinationsFile(string kind)
    {
        var escaping = Escape(kind);
        var cfg = new Config { DestinationsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(EscapeKinds))]
    public void SaveRefusesAnEscapingMonitoredFoldersFile(string kind)
    {
        var escaping = Escape(kind);
        var cfg = new Config { MonitoredFoldersFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("monitored_folders_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(EscapeKinds))]
    public void SaveRefusesAnEscapingAlertsFile(string kind)
    {
        var escaping = Escape(kind);
        var cfg = new Config { AlertsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("alerts_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(EscapeKinds))]
    public void SaveRefusesAnEscapingBoxLabelsFile(string kind)
    {
        var escaping = Escape(kind);
        var cfg = new Config { BoxLabelsFile = escaping };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
        Assert.Contains(escaping, ex.Message);
    }

    [Theory]
    [MemberData(nameof(EscapeKinds))]
    public void TrySaveReportsAnEscapingDestinationsFileInsteadOfThrowing(string kind)
    {
        var escaping = Escape(kind);
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
        // using a directory the test process fully owns, so the assertion
        // that nothing landed there is airtight and needs no cleanup of a
        // real machine location.
        var outsideFile = Path.Combine(OutsideDir, "evil.json");
        var cfg = new Config { DestinationsFile = outsideFile };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains(outsideFile, ex.Message);
        Assert.False(File.Exists(outsideFile));
    }

    [Fact]
    public void TrySaveStillSavesTheOtherFilesWhenOneKeyEscapes()
    {
        // Each side file is attempted independently (TrySave's documented
        // contract) — a bad destinations_file must not block alerts.json,
        // monitored-folders.json, or the main config from saving, and must
        // not actually write anything at the escaping location.
        var outsideFile = Path.Combine(OutsideDir, "evil.json");
        var cfg = new Config { DestinationsFile = outsideFile };
        var ok = Config.TrySave(cfg, ConfigPath, out var error);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
        Assert.True(File.Exists(ConfigPath));
        Assert.True(File.Exists(Path.Combine(_dir, "alerts.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "monitored-folders.json")));
        Assert.False(File.Exists(outsideFile));
    }

    [Fact]
    public void TrySaveReportsThePureConfinementFailureAsARefusedKey()
    {
        // The 4-arg overload's extra out param (2026-08-07 audit, Task 1b) —
        // ShellViewModel uses it to tell "this side-file key's configured
        // path is structurally unwritable, and will be again on every
        // future save until someone edits it" apart from a real, possibly
        // transient I/O failure. When EVERY failure this call produced is a
        // confinement refusal, the key must come back in refusedSideFileKeys.
        var outsideFile = Path.Combine(OutsideDir, "evil.json");
        var cfg = new Config { DestinationsFile = outsideFile };
        var ok = Config.TrySave(cfg, ConfigPath, out var error, out var refusedKeys);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
        Assert.Equal(new[] { "destinations_file" }, refusedKeys);
    }

    [Fact]
    public void TrySaveReportsNoRefusedKeysWhenNothingIsWrong()
    {
        var cfg = new Config();
        var ok = Config.TrySave(cfg, ConfigPath, out var error, out var refusedKeys);
        Assert.True(ok, error);
        Assert.Empty(refusedKeys);
    }

    [Fact]
    public void TrySaveReportsNoRefusedKeysWhenAConfinementRefusalIsMixedWithARealFailure()
    {
        // box_labels_file (left at its relative default) points at a path
        // that already exists AS A DIRECTORY, not a file — a genuine,
        // non-confinement I/O failure, arising independently of
        // destinations_file's confinement refusal in the very same call.
        // refusedSideFileKeys must come back EMPTY here: a caller must
        // never suppress-on-sight a refusal that arrived bundled with
        // something genuinely new and different.
        var outsideFile = Path.Combine(OutsideDir, "evil.json");
        Directory.CreateDirectory(Path.Combine(_dir, "box-labels.json"));
        var cfg = new Config { DestinationsFile = outsideFile };
        var ok = Config.TrySave(cfg, ConfigPath, out var error, out var refusedKeys);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
        Assert.Empty(refusedKeys);
    }

    [Fact]
    public void SaveRefusesAnEscapingBoxLabelsFileEvenWhenSomethingAlreadyExistsThere()
    {
        // Regression for the existence-oracle bug: the box-labels bootstrap
        // guard ("only write if missing") used to probe File.Exists via the
        // UNCONFINED path, and only confine the write once the probe said
        // "missing". That let an attacker-controlled box_labels_file
        // silently no-op (success) when the target already existed and
        // throw when it didn't — an oracle for "does this path exist on
        // the victim's disk". Both branches must now refuse identically:
        // this test targets a file that DOES already exist outside the
        // config dir, so a still-oracling probe would silently succeed
        // instead of throwing.
        var outsideFile = Path.Combine(OutsideDir, "already-here.json");
        File.WriteAllText(outsideFile, """{"label_clients":[]}""");

        var cfg = new Config { BoxLabelsFile = outsideFile };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
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
        var absoluteDests = Path.Combine(OutsideDir, "abs-destinations.json");
        File.WriteAllText(absoluteDests, """{"routes":[{"label":"OUTSIDE","path":"C:/x"}]}""");

        var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = absoluteDests });
        File.WriteAllText(ConfigPath, configJson);

        var cfg = Config.Load(ConfigPath);
        Assert.Equal("OUTSIDE", Assert.Single(cfg.Routes).Label);
    }

    [Fact]
    public void LoadRefusesATraversalDestinationsFile()
    {
        var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = TraversalEscape });
        File.WriteAllText(ConfigPath, configJson);
        var ex = Assert.Throws<ConfigException>(() => Config.Load(ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
    }

    [Fact]
    public void LoadRefusesARootedWithoutDriveDestinationsFile()
    {
        var configJson = JsonSerializer.Serialize(new { inbox = "C:/in", destinations_file = RootedWithoutDriveEscape });
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
        // ...\configdir-evil\x.json starts with the STRING ...\configdir
        // (the real config dir) but is not inside it — a naive StartsWith
        // on the raw path string would wrongly accept this. Build that
        // sibling next to the real config dir (still inside _testRoot) and
        // point a side-file key at a file inside it.
        var evilSibling = _dir + "-evil";
        Directory.CreateDirectory(evilSibling);
        var evilFile = Path.Combine(evilSibling, "x.json");
        var cfg = new Config { DestinationsFile = evilFile };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.False(File.Exists(evilFile));
    }
}
