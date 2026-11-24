using System.Runtime.InteropServices;
using System.Text;

// Every Windows DLL this program calls is loaded from System32 only, never from MemTidy's own folder,
// where a look-alike DLL could have been put (it matters most for the cache window, which runs as administrator).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace MemTidy;

/// <summary>Win32 calls used to approximate the macOS memory-management loop.</summary>
static class Native
{
    // ---- Memory status ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;   // commit limit
        public ulong ullAvailPageFile;   // commit limit - commit charge
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public const int LowMemoryResourceNotification = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryMemoryResourceNotification(IntPtr handle, out bool resourceState);

    // ---- Process access ----
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(int pid, out int sessionId);

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    /// <summary>Full path of the process's exe, or null if it can't be opened.</summary>
    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static string? ImageName(int pid) =>
        ImagePath(pid) is string path ? Path.GetFileName(path).ToLowerInvariant() : null;

    // ---- Owner check: even when elevated, only touch the current user's own programs ----
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    static readonly System.Security.Principal.SecurityIdentifier CurrentUser =
        System.Security.Principal.WindowsIdentity.GetCurrent().User!;

    public static bool IsOwnedByCurrentUser(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(token, 1 /* TokenUser */, buf, 256, out _)) return false;
            return new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(buf)) == CurrentUser;
        }
        finally { Marshal.FreeHGlobal(buf); CloseHandle(token); }
    }

    // ---- Priority / QoS (App Nap analogue) ----
    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;

    [DllImport("kernel32.dll")]
    public static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    public const int ProcessMemoryPriority = 0;
    public const int ProcessPowerThrottling = 4;
    public const uint MEMORY_PRIORITY_LOW = 2;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;      // PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr process, int infoClass, ref uint memoryPriority, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr process, int infoClass, ref uint memoryPriority, int size);

    // ---- Working set (compressor analogue) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr process);

    public static long WorkingSet(IntPtr process) =>
        GetProcessMemoryInfo(process, out var c, Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>()) ? (long)c.WorkingSetSize : 0;

    public static long WorkingSet(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try { return WorkingSet(h); } finally { CloseHandle(h); }
    }

    // ---- Process list (Toolhelp gives parent PIDs in one cheap pass) ----
    const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    public readonly record struct ProcEntry(int Pid, int ParentPid, string Name);

    public static List<ProcEntry> EnumProcesses()
    {
        var list = new List<ProcEntry>(400);
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add(new ProcEntry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile.ToLowerInvariant()));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // ---- Foreground tracking ----
    public const uint EVENT_SYSTEM_FOREGROUND = 3;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    // ---- Misc ----
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
