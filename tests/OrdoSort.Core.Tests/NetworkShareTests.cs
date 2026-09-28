using System.Security.AccessControl;
using System.Security.Principal;

namespace OrdoSort.Core.Tests;

/// <summary>Saving over a real Windows network share, as two stations: you
/// through <c>\\localhost</c>, and a plain "Modify" test user through
/// <c>\\127.0.0.1</c> (Windows keeps one sign-in per server name). Needs the
/// one-time <c>scripts\share-test-setup.ps1</c> (as administrator) and then
/// <c>scripts\share-test-connect.ps1</c> after each sign-in; run with
/// <c>check.bat share</c>. Left out of the everyday check and CI, which have
/// no share. A test here fails, rather than passes quietly, when the share
/// isn't set up.</summary>
[Trait("Category", "NetworkShare")]
public sealed class NetworkShareTests : IDisposable
{
    private const string AsYou = @"\\localhost\OrdoSortTest$";
    private const string AsTestUser = @"\\127.0.0.1\OrdoSortTest$";
    private const string ReadOnlyAsTestUser = @"\\127.0.0.1\OrdoSortTestRO$";
    private const string TestUser = "OrdoSortShareTest";

    private readonly string _folder = "test-" + Guid.NewGuid().ToString("N")[..8];

    public NetworkShareTests()
    {
        RequireTwoStations();
        Directory.CreateDirectory(Path.Combine(AsYou, _folder));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(AsYou, _folder), recursive: true); }
        catch (IOException) { /* a leftover test folder on the test share is harmless */ }
    }

    private string Mine(string name) => Path.Combine(AsYou, _folder, name);
    private string TheirPathTo(string name) => Path.Combine(AsTestUser, _folder, name);

    /// <summary>Owner of a file, read through your own connection.</summary>
    private static string OwnerOf(string path) =>
        new FileInfo(path).GetAccessControl().GetOwner(typeof(NTAccount))!.Value;

    /// <summary>Without this the tests would run as you on both paths and
    /// prove nothing, so an unready share is a failure with the fix.</summary>
    private static void RequireTwoStations()
    {
        const string setup = "Set up the test share: run scripts\\share-test-setup.ps1 as administrator once, "
                             + "then scripts\\share-test-connect.ps1 (without admin) after each sign-in.";
        if (!Directory.Exists(AsYou)) Assert.Fail($"{AsYou} is not reachable. {setup}");
        var probe = Path.Combine(AsTestUser, "whoami-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            File.WriteAllText(probe, "probe");
            var owner = OwnerOf(probe);
            if (!owner.EndsWith("\\" + TestUser, StringComparison.OrdinalIgnoreCase))
                Assert.Fail($"{AsTestUser} connects as {owner}, not as {TestUser}. {setup}");
        }
        catch (IOException e) { Assert.Fail($"{AsTestUser} is not reachable ({e.Message}). {setup}"); }
        finally { File.Delete(probe); }
    }

    /// <summary>The office bug (QC, fixed in f80c880): File.Replace also
    /// copies the old file's owner and permissions, which a plain Modify user
    /// may not do when another station's user wrote the file last, so every
    /// settings save failed with "access denied".</summary>
    [Fact]
    public void AStationCanSaveTheConfigAnotherStationWroteLast()
    {
        Assert.True(Config.TrySave(new Config { Inbox = "from you" }, Mine("config.json"), out var mineError), mineError);
        Assert.EndsWith("\\" + Environment.UserName, OwnerOf(Mine("config.json")), StringComparison.OrdinalIgnoreCase);

        var saved = Config.TrySave(new Config { Inbox = "from the test user" }, TheirPathTo("config.json"), out var error);

        Assert.True(saved, error);
        Assert.Equal("from the test user", Config.Load(Mine("config.json"), createIfMissing: false).Inbox);
        Assert.Empty(Directory.GetFiles(Path.Combine(AsYou, _folder), "*.tmp"));
    }

    /// <summary>The same denial with no second station at all: over a share,
    /// a Modify user is refused File.Replace even on a file they own
    /// (checked 2026-09-27), so before f80c880 every save after the first
    /// failed, not only after another station's.</summary>
    [Fact]
    public void AStationCanSaveOverTheConfigItWroteItself()
    {
        Assert.True(Config.TrySave(new Config { Inbox = "first" }, TheirPathTo("config.json"), out var firstError), firstError);
        Assert.EndsWith("\\" + TestUser, OwnerOf(Mine("config.json")), StringComparison.OrdinalIgnoreCase);

        var saved = Config.TrySave(new Config { Inbox = "second" }, TheirPathTo("config.json"), out var error);

        Assert.True(saved, error);
        Assert.Equal("second", Config.Load(Mine("config.json"), createIfMissing: false).Inbox);
    }

    [Fact]
    public void ASaveToAReadOnlyShareFailsAndLeavesTheFileAsItWas()
    {
        Assert.True(Config.TrySave(new Config { Inbox = "original" }, Mine("config.json"), out var mineError), mineError);

        var saved = Config.TrySave(new Config { Inbox = "change" },
            Path.Combine(ReadOnlyAsTestUser, _folder, "config.json"), out var error);

        Assert.False(saved);
        Assert.NotEqual("", error);
        Assert.Equal("original", Config.Load(Mine("config.json"), createIfMissing: false).Inbox);
    }
}
