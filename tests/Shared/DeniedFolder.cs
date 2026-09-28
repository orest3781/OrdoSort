using System.Diagnostics;
using System.Security.Principal;

namespace OrdoSort.TestSupport;

/// <summary>Makes a real folder unreadable to the current user for the
/// length of a test (an ACL deny through icacls), and lifts the deny on
/// Dispose so the temp folder can be deleted.</summary>
public sealed class DeniedFolder : IDisposable
{
    private readonly string _path;
    private readonly string _user = Environment.UserDomainName + "\\" + Environment.UserName;

    public DeniedFolder(string path)
    {
        _path = path;
        var code = Icacls(_path, "/deny", $"{_user}:(OI)(CI)R");
        if (code != 0)
            throw new InvalidOperationException($"icacls could not deny read on {_path} (exit code {code})");
    }

    /// <summary>True when the folder really can't be listed now. False only
    /// in an elevated session, which reads past a deny: the one case where a
    /// test may skip. Anywhere else a deny that didn't hold means the
    /// fixture is broken, and that throws rather than letting the test pass
    /// with nothing checked (Q2-45).</summary>
    public bool Holds
    {
        get
        {
            try
            {
                Directory.EnumerateFiles(_path).Any();
            }
            catch (UnauthorizedAccessException) { return true; }
            if (RunningElevated()) return false;
            throw new InvalidOperationException(
                $"the deny on {_path} did not hold and this run is not elevated, so the test would prove nothing");
        }
    }

    public void Dispose() => Icacls(_path, "/remove:d", _user);

    private static bool RunningElevated() =>
        OperatingSystem.IsWindows()
        && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static int Icacls(params string[] args)
    {
        var psi = new ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
