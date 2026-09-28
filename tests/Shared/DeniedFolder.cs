using System.Diagnostics;

namespace OrdoSort.TestSupport;

/// <summary>Makes a real folder unreadable to the current user for the
/// length of a test (an ACL deny through icacls), and lifts the deny on
/// Dispose so the temp folder can be deleted. An elevated or backup-privilege
/// session can read past a deny, so <see cref="Holds"/> says whether it did;
/// a test that needs the folder unreadable should do nothing when it
/// doesn't.</summary>
public sealed class DeniedFolder : IDisposable
{
    private readonly string _path;
    private readonly string _user = Environment.UserDomainName + "\\" + Environment.UserName;

    public DeniedFolder(string path)
    {
        _path = path;
        Icacls(_path, "/deny", $"{_user}:(OI)(CI)R");
    }

    /// <summary>True when the folder really can't be listed now.</summary>
    public bool Holds
    {
        get
        {
            try
            {
                Directory.EnumerateFiles(_path).Any();
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
        }
    }

    public void Dispose() => Icacls(_path, "/remove:d", _user);

    private static void Icacls(params string[] args)
    {
        var psi = new ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
