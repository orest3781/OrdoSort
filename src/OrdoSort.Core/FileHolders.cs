using System.Runtime.InteropServices;

namespace OrdoSort.Core;

/// <summary>One program that has a file open.</summary>
/// <param name="Name">What Windows calls it: the window title's program
/// name ("Windows Explorer", "Adobe Acrobat"), or the service's name.</param>
/// <param name="ProcessId">Its process id.</param>
public sealed record FileHolder(string Name, int ProcessId);

/// <summary>Which programs have a file open, asked of Windows' Restart
/// Manager: the same source Windows' own "file in use" dialogs name. A
/// refused move used to say only "open in another program", and the file was
/// often held by something nobody had knowingly opened (Explorer's preview
/// pane, a thumbnail, a sync client), so there was nothing to go and close.</summary>
public static class FileHolders
{
    /// <summary>The programs holding <paramref name="path"/> open, or an empty
    /// list when none do or Windows can't say (a file on a share is often
    /// held by a program on another PC, which this PC can't see). Never
    /// throws: this only improves a message.</summary>
    public static IReadOnlyList<FileHolder> Of(string path)
    {
        try
        {
            return Ask(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                   or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<FileHolder>();
        }
    }

    private const int ErrorMoreData = 234;

    private static IReadOnlyList<FileHolder> Ask(string path)
    {
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString()) != 0)
            return Array.Empty<FileHolder>();
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0)
                return Array.Empty<FileHolder>();
            uint count = 0, reasons = 0;
            var err = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (err == 0 || err != ErrorMoreData) return Array.Empty<FileHolder>();

            var infos = new ProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, infos, ref reasons) != 0)
                return Array.Empty<FileHolder>();
            var holders = new List<FileHolder>();
            for (var i = 0; i < count; i++)
            {
                var name = infos[i].AppName.Trim();
                if (name.Length == 0) name = infos[i].ServiceShortName.Trim();
                if (name.Length > 0) holders.Add(new FileHolder(name, infos[i].Process.ProcessId));
            }
            return holders;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint files, string[] fileNames,
        uint applications, IntPtr uniqueProcesses, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? infos, ref uint rebootReasons);
}
