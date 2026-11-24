using System.Runtime.InteropServices;

namespace MemTidy;

/// <summary>
/// Splits RAM the way Activity Monitor does: what programs use, what the system keeps as cache
/// (standby list: recently used files and apps, released instantly when needed), and what is truly empty.
/// Read through performance counters, which need no admin rights. Only alive while the panel is open.
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

    readonly IntPtr query;
    readonly IntPtr[] standby = new IntPtr[3];
    readonly IntPtr free;
    readonly bool ok;

    public MemoryBreakdown()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return;
        string[] standbyPaths =
        [
            @"\Memory\Standby Cache Core Bytes",
            @"\Memory\Standby Cache Normal Priority Bytes",
            @"\Memory\Standby Cache Reserve Bytes",
        ];
        ok = true;
        for (int i = 0; i < standbyPaths.Length; i++)
            ok &= PdhAddEnglishCounterW(query, standbyPaths[i], IntPtr.Zero, out standby[i]) == 0;
        ok &= PdhAddEnglishCounterW(query, @"\Memory\Free & Zero Page List Bytes", IntPtr.Zero, out free) == 0;
    }

    /// <summary>Returns (cache, empty) in bytes, or null if the counters are unavailable.</summary>
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
        if (query != IntPtr.Zero) PdhCloseQuery(query);
    }
}
