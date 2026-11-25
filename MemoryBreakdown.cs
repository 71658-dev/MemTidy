using System.Runtime.InteropServices;

namespace MemTidy;

/// <summary>
/// Splits RAM the way Activity Monitor does: what programs use, what the system keeps as cache
/// (standby list: recently used files and apps, released instantly when needed), and what is truly empty.
/// Read through performance counters, which need no admin rights.
/// Opening the PDH query loads the counter registry and perflib and takes a second or more, so the tray opens it once
/// on a background thread (<see cref="WarmUp"/>) and keeps it; until it is ready <see cref="Read"/> returns null.
/// </summary>
sealed class MemoryBreakdown : IDisposable
{
    [StructLayout(LayoutKind.Explicit)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public long LargeValue;
    }

    const uint PDH_FMT_LARGE = 0x400;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    IntPtr query;
    readonly IntPtr[] standby = new IntPtr[3];
    IntPtr free;
    volatile bool ok, disposed;
    Task? warming;

    /// <summary>Opens the query on a thread-pool thread (once); <paramref name="ready"/> runs there when it is usable.</summary>
    public Task WarmUp(Action? ready = null) => warming ??= Task.Run(() =>
    {
        Open();
        if (ok) ready?.Invoke();
    });

    void Open()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return;
        string[] standbyPaths =
        [
            @"\Memory\Standby Cache Core Bytes",
            @"\Memory\Standby Cache Normal Priority Bytes",
            @"\Memory\Standby Cache Reserve Bytes",
        ];
        bool good = true;
        for (int i = 0; i < standbyPaths.Length; i++)
            good &= PdhAddEnglishCounterW(q, standbyPaths[i], IntPtr.Zero, out standby[i]) == 0;
        good &= PdhAddEnglishCounterW(q, @"\Memory\Free & Zero Page List Bytes", IntPtr.Zero, out free) == 0;
        good &= PdhCollectQueryData(q) == 0; // the first collection is slow too: do it here, not on the UI thread
        lock (this)
        {
            query = q;
            if (disposed) { PdhCloseQuery(q); query = IntPtr.Zero; return; }
            ok = good;
        }
    }

    /// <summary>Returns (cache, empty) in bytes, or null if the counters are unavailable or not warmed up yet.</summary>
    public (long Cache, long Empty)? Read()
    {
        if (!ok || PdhCollectQueryData(query) != 0) return null;
        long cache = 0;
        foreach (var c in standby)
        {
            if (PdhGetFormattedCounterValue(c, PDH_FMT_LARGE, out _, out var v) != 0) return null;
            cache += v.LargeValue;
        }
        if (PdhGetFormattedCounterValue(free, PDH_FMT_LARGE, out _, out var f) != 0) return null;
        return (cache, f.LargeValue);
    }

    public void Dispose()
    {
        lock (this)
        {
            disposed = true;
            ok = false;
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
        }
    }
}
