namespace MemTidy;

/// <summary>
/// Tracks which app the user is looking at. Like App Nap, "in use" is judged per app (exe name),
/// so all of Chrome's helper processes count as foreground while any Chrome window is.
/// Must be created on the UI thread: the out-of-context hook is delivered through its message loop.
/// </summary>
sealed class ForegroundWatcher : IDisposable
{
    readonly Native.WinEventDelegate callback; // kept alive so the GC does not collect the hook target
    readonly IntPtr hook;
    readonly Dictionary<string, DateTime> lastForeground = new();
    readonly DateTime started = DateTime.Now;

    public int Pid { get; private set; }
    public string? Name { get; private set; }

    /// <summary>Raised with (pid, exe name) whenever the foreground app changes.</summary>
    public event Action<int, string>? Changed;

    public ForegroundWatcher()
    {
        callback = OnEvent;
        hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Update(Native.GetForegroundWindow());
    }

    void OnEvent(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Update(hwnd);

    void Update(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Pid) return;
        var name = Native.ImageName(pid);
        if (Name != null) lastForeground[Name] = DateTime.Now; // the previous app was in use until now
        Pid = pid;
        Name = name;
        if (name == null) return;
        lastForeground[name] = DateTime.Now;
        Changed?.Invoke(pid, name);
    }

    /// <summary>When this app was last in the foreground; apps never seen count from when MemTidy started.</summary>
    public DateTime LastSeen(string name)
    {
        if (name == Name) return DateTime.Now;
        return lastForeground.TryGetValue(name, out var t) ? t : started;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
    }
}
