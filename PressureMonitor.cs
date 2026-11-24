using System.Runtime.InteropServices;

namespace MemTidy;

enum Pressure { Green, Yellow, Red }

readonly record struct MemSample(DateTime Time, double UsedPct, double CommitPct, Pressure Level);

/// <summary>
/// macOS judges memory by *pressure* rather than "free RAM": a full RAM full of cache is fine,
/// running out of room to hand new pages is not. Here pressure combines available physical
/// memory, commit charge vs. the commit limit, and the kernel's own low-memory signal,
/// with separate enter/exit thresholds so the level does not flap.
/// </summary>
sealed class PressureMonitor : IDisposable
{
    // Enter / exit thresholds (percent)
    const double YellowAvailEnter = 20, YellowAvailExit = 24, YellowCommitEnter = 85, YellowCommitExit = 81;
    const double RedAvailEnter = 8, RedAvailExit = 11, RedCommitEnter = 92, RedCommitExit = 89;

    public const int HistorySeconds = 300;
    public const int SampleSeconds = 2;

    readonly IntPtr lowMemHandle = Native.CreateMemoryResourceNotification(Native.LowMemoryResourceNotification);
    readonly Queue<MemSample> history = new();
    Pressure? forced;
    readonly DateTime forcedUntil;
    int compressionPid = -1;

    public PressureMonitor(Pressure? forced, int forceSeconds)
    {
        this.forced = forced;
        forcedUntil = forceSeconds > 0 ? DateTime.Now.AddSeconds(forceSeconds) : DateTime.MaxValue;
    }

    public Pressure Level { get; private set; } = Pressure.Green;
    public Native.MEMORYSTATUSEX Status { get; private set; }
    public long CompressedBytes { get; private set; } = -1;
    public bool KernelLowMemory { get; private set; }
    public bool Forced => forced.HasValue;
    public IReadOnlyCollection<MemSample> History => history;

    public double UsedPct => Status.ullTotalPhys == 0 ? 0 : 100.0 * (Status.ullTotalPhys - Status.ullAvailPhys) / Status.ullTotalPhys;
    public double AvailPct => 100 - UsedPct;
    public double CommitPct => Status.ullTotalPageFile == 0 ? 0 : 100.0 * (Status.ullTotalPageFile - Status.ullAvailPageFile) / Status.ullTotalPageFile;

    /// <summary>Demo mode: five minutes of history shaped like the design (a climb through yellow to red and back).</summary>
    public void SeedDemoHistory()
    {
        history.Clear();
        int n = HistorySeconds / SampleSeconds;
        var now = DateTime.Now;
        for (int i = 0; i < n - 1; i++)
        {
            double v = 63 + 2 * Math.Sin(i / 9.0) + Math.Sin(i / 3.7);
            if (i >= 58 && i <= 104) v += 30 * Math.Sin(Math.PI * (i - 58) / 46);
            v = Math.Min(v, 97);
            var level = v >= 92 ? Pressure.Red : v >= 80 ? Pressure.Yellow : Pressure.Green;
            history.Enqueue(new MemSample(now.AddSeconds(-(n - 1 - i) * SampleSeconds), v, 50, level));
        }
    }

    public void Sample()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        Status = m;

        KernelLowMemory = lowMemHandle != IntPtr.Zero && Native.QueryMemoryResourceNotification(lowMemHandle, out var low) && low;
        if (forced.HasValue && DateTime.Now >= forcedUntil) forced = null; // simulation over: back to real readings
        Level = forced ?? Classify(Level, AvailPct, CommitPct, KernelLowMemory);
        CompressedBytes = ReadCompressionStore();

        history.Enqueue(new MemSample(DateTime.Now, UsedPct, CommitPct, Level));
        while (history.Count > HistorySeconds / SampleSeconds) history.Dequeue();
    }

    static Pressure Classify(Pressure current, double avail, double commit, bool kernelLow)
    {
        bool redIn = avail < RedAvailEnter || commit > RedCommitEnter || kernelLow;
        bool redOut = avail > RedAvailExit && commit < RedCommitExit && !kernelLow;
        bool yellowIn = avail < YellowAvailEnter || commit > YellowCommitEnter;
        bool yellowOut = avail > YellowAvailExit && commit < YellowCommitExit;

        if (redIn) return Pressure.Red;
        if (current == Pressure.Red && !redOut) return Pressure.Red;
        if (yellowIn) return Pressure.Yellow;
        if (current >= Pressure.Yellow && !yellowOut) return Pressure.Yellow;
        return Pressure.Green;
    }

    /// <summary>Windows' own compressor lives in the "Memory Compression" process; its working set is the compressed store.</summary>
    long ReadCompressionStore()
    {
        // The process never restarts, so look it up once.
        if (compressionPid < 0)
            compressionPid = Native.EnumProcesses().FirstOrDefault(p => p.Name == "memory compression").Pid;
        var size = compressionPid > 0 ? Native.WorkingSet(compressionPid) : 0;
        return size > 0 ? size : -1; // 0 means it could not be read (usually needs admin): show "—", not "0 KB"
    }

    public static string Label(Pressure p) => p switch
    {
        Pressure.Green => "正常",
        Pressure.Yellow => "偏高",
        _ => "吃緊",
    };

    public void Dispose()
    {
        if (lowMemHandle != IntPtr.Zero) Native.CloseHandle(lowMemHandle);
    }
}
