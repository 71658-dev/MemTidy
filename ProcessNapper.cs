using System.Runtime.InteropServices;

namespace MemTidy;

/// <summary>
/// The macOS-style policy:
///   yellow → idle background apps nap (EcoQoS + below-normal priority, like App Nap) and their
///            pages are marked low priority so the memory manager reclaims them first (like "inactive" pages);
///   red    → additionally hand napping apps' working sets back to the system, largest first,
///            where Windows' memory compression takes over (like the macOS compressor);
///   always → an app wakes the moment it comes to the foreground or starts doing real work.
/// Nothing is ever closed.
/// </summary>
sealed class ProcessNapper
{
    sealed class Proc
    {
        public int Pid, ParentPid;
        public string Name = "";
        public long Created;
        public long CpuTime;          // 100 ns units
        public DateTime Sampled;
        public DateTime LastActive;
        public double CpuPct;
        public long WorkingSet;
        public bool Owned;            // belongs to the current user (checked once)
        public bool? Protected;       // security / signing / audio / input software (checked once, when first a candidate)
        public string? Path;          // image path, read once when the exclusions window first needs it ("" = unreadable)

        public bool Napped;
        public DateTime NappedAt;
        public uint OrigPriority;     // 0 = we did not change it
        public uint OrigMemPriority;  // 0 = we did not change it
        public bool EcoSet;
        public DateTime LastTrim;
        public long TrimmedBytes;
    }

    public readonly record struct NapView(int Pid, string Name, long WorkingSet, DateTime NappedAt, long TrimmedBytes);

    /// <summary>Never touched, whatever the settings say.</summary>
    static readonly HashSet<string> SystemExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows itself
        "system", "registry", "memory compression", "secure system", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe",
        "sihost.exe", "fontdrvhost.exe", "searchhost.exe", "searchindexer.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "applicationframehost.exe", "lockapp.exe", "logonui.exe", "consent.exe",
        "credentialuibroker.exe", "userinit.exe", "taskmgr.exe", "conhost.exe", "runtimebroker.exe", "dllhost.exe",
        "taskhostw.exe", "shellhost.exe", "unsecapp.exe", "memtidy.exe",
        // Security
        "msmpeng.exe", "nissrv.exe", "mpcmdrun.exe", "smartscreen.exe", "securityhealthsystray.exe",
        "securityhealthservice.exe", "voneagentconsoletray.exe", "pccntmon.exe", "secureconnector.exe",
        // Certificate signing (HiPKI, healthcare certificates)
        "chksrv.exe", "chtnode.exe", "hcaservisign.exe", "hcaservisignmonitor.exe", "hcaservisignworker.exe",
        // Audio
        "audiodg.exe", "rtkauduservice64.exe", "rtkuwp.exe", "igoswserver.exe",
        // Input methods and accessibility
        "ctfmon.exe", "textinputhost.exe", "tabtip.exe", "osk.exe", "chsime.exe", "chtime.exe",
        "asusimeservice.exe", "asusimeentry.exe", "narrator.exe", "magnify.exe", "atbroker.exe", "nvda.exe",
    };

    /// <summary>
    /// Publishers whose software is never slowed (security, VPN / network access, signing, audio, input, accessibility).
    /// Matching the publisher and the install folder catches helpers whose names give nothing away, such as HiPKI's
    /// chtnode.exe, which is a renamed Node.js.
    /// </summary>
    static readonly string[] ProtectedPublishers =
    [
        "Trend Micro", "ESET", "Kaspersky", "Symantec", "Broadcom", "McAfee", "Sophos", "CrowdStrike", "SentinelOne",
        "Palo Alto Networks", "Forescout", "Bitdefender", "Fortinet", "Check Point", "Cisco", "Avast", "AVG Technologies",
        "Malwarebytes", "F-Secure", "WithSecure", "Webroot", "Carbon Black", "AhnLab", "Zscaler", "Netskope", "Citrix",
        "HCA", "Chunghwa Telecom", "中華電信", "ServiSign",
        "Realtek", "Waves Audio", "Dolby", "A-Volute", "Nahimic", "Conexant", "Cirrus Logic", "Intelligo",
        "Synaptics", "ELAN Microelectronics", "Logitech",
        "NV Access", "Freedom Scientific", "Dolphin Computer Access",
    ];

    /// <summary>
    /// Install folders whose programs are never slowed. Programs run from the driver store are hardware-support
    /// companions installed with a driver (audio processing, touchpad, hotkeys, OEM services).
    /// </summary>
    static readonly string[] ProtectedFolders =
        [@"\HiPKI", @"\Trend Micro\", @"\Windows Defender", @"\ServiSign", @"\Logi\", @"\DriverStore\FileRepository\"];

    readonly Dictionary<int, Proc> procs = new();
    readonly Settings settings;
    readonly ForegroundWatcher foreground;
    readonly int selfPid = Environment.ProcessId;
    readonly int session;

    public ProcessNapper(Settings settings, ForegroundWatcher foreground)
    {
        this.settings = settings;
        this.foreground = foreground;
        Native.ProcessIdToSessionId(selfPid, out session);
        foreground.Changed += OnForeground;
    }

    public int NappedCount => procs.Values.Count(p => p.Napped);
    public long TotalTrimmed { get; private set; }

    public IEnumerable<NapView> Napped => procs.Values.Where(p => p.Napped)
        .OrderByDescending(p => p.WorkingSet)
        .Select(p => new NapView(p.Pid, p.Name, p.WorkingSet, p.NappedAt, p.TrimmedBytes));

    /// <summary>One program as the exclusions window shows it: all its processes together.</summary>
    public readonly record struct AppView(string Name, long WorkingSet, bool Napped, string? Path);

    /// <summary>This user's running programs (system and protected ones left out: they never nap), by name, largest first.</summary>
    public IReadOnlyList<AppView> Running => procs.Values
        .Where(p => p.Owned && !SystemExclusions.Contains(p.Name) && !IsProtected(p))
        .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => new AppView(g.First().Name.ToLowerInvariant(), g.Sum(p => p.WorkingSet), g.Any(p => p.Napped), PathOf(g.First())))
        .OrderByDescending(a => a.WorkingSet)
        .ToList();

    static string? PathOf(Proc p)
    {
        p.Path ??= Native.ImagePath(p.Pid) ?? "";
        return p.Path.Length > 0 ? p.Path : null;
    }

    public static bool IsBuiltIn(string name) => SystemExclusions.Contains(name);

    // Wake-ups for background work, per program, today: a program woken often is better left out of napping.
    readonly Dictionary<string, int> workWakes = new(StringComparer.OrdinalIgnoreCase);
    DateTime workWakesDay = DateTime.Today;

    /// <summary>The program most often woken for background work today (at least 3 times) and not yet excluded or skipped.</summary>
    public (string Name, int Count)? Suggestion
    {
        get
        {
            if (workWakesDay != DateTime.Today) return null;
            var best = workWakes
                .Where(kv => kv.Value >= 3 && !IsExcluded(kv.Key)
                             && !settings.DismissedSuggestions.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value).FirstOrDefault();
            return best.Key == null ? null : (best.Key, best.Value);
        }
    }

    public double WakeCpuPercent => settings.WakeCpuPercent;

    /// <summary>How long pressure must stay green (with room to spare) before napping apps get their memory priority back.
    /// 3 minutes, shortened along with the idle threshold when testing.</summary>
    TimeSpan CalmBeforeRestore => TimeSpan.FromMinutes(Math.Min(3, settings.IdleMinutes));
    DateTime? greenSince;

    /// <summary>One pass: refresh stats, wake whatever should be awake, then act according to pressure.</summary>
    /// <param name="roomy">Plenty of memory is available (cache counts: the system can release it instantly).</param>
    /// <param name="manual">"Tidy now": act as if pressure were red, still only on idle background apps.</param>
    public void Cycle(Pressure level, bool roomy, bool manual = false)
    {
        var now = DateTime.Now;
        greenSince = level == Pressure.Green ? greenSince ?? now : null;
        var entries = Native.EnumProcesses();
        var alive = new HashSet<int>(entries.Count);

        foreach (var e in entries)
        {
            if (e.Pid <= 4 || e.Pid == selfPid) continue;
            if (!Native.ProcessIdToSessionId(e.Pid, out var s) || s != session) continue;
            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (!Native.GetProcessTimes(h, out var created, out _, out var kernel, out var user)) continue;
                alive.Add(e.Pid);
                if (!procs.TryGetValue(e.Pid, out var p) || p.Created != created)
                {
                    // New process (or a reused PID): start observing; it must prove it is idle first.
                    procs[e.Pid] = new Proc { Pid = e.Pid, Name = e.Name, Created = created, CpuTime = kernel + user, Sampled = now, LastActive = now,
                                             Owned = Native.IsOwnedByCurrentUser(h), WorkingSet = Native.WorkingSet(h) };
                    continue;
                }
                p.ParentPid = e.ParentPid;
                var wall = (now - p.Sampled).TotalMilliseconds * 10_000;
                p.CpuPct = wall > 0 ? 100.0 * (kernel + user - p.CpuTime) / wall : 0;
                p.CpuTime = kernel + user;
                p.Sampled = now;
                p.WorkingSet = Native.WorkingSet(h);
                if (p.CpuPct >= settings.IdleCpuPercent) p.LastActive = now;
            }
            finally { Native.CloseHandle(h); }
        }

        foreach (var pid in procs.Keys.Where(k => !alive.Contains(k)).ToList()) procs.Remove(pid);

        // Wake-ups first: an app in use, or one that started real work (audio, sync, a download), should run at full speed.
        foreach (var p in procs.Values.Where(p => p.Napped))
        {
            if (IsForeground(p)) Wake(p, "回到前景");
            else if (p.CpuPct >= settings.WakeCpuPercent)
            {
                Wake(p, $"背景有工作（CPU {p.CpuPct:0.#}%）");
                if (workWakesDay != DateTime.Today) { workWakes.Clear(); workWakesDay = DateTime.Today; }
                workWakes[p.Name] = workWakes.GetValueOrDefault(p.Name) + 1;
            }
            else if (IsExcluded(p.Name) || IsProtected(p)) Wake(p, "已加入排除清單");
        }

        if (settings.Paused && !manual) return;
        var effective = manual ? Pressure.Red : level;
        if (effective == Pressure.Green)
        {
            // Memory is plentiful again: let napping apps keep their pages, so switching back is instant.
            // They stay in efficiency mode (CPU) until used, like App Nap.
            if (roomy && now - greenSince >= CalmBeforeRestore)
                foreach (var p in procs.Values.Where(p => p.Napped && p.OrigMemPriority != 0)) RestoreMemPriority(p);
            return;
        }

        var idle = procs.Values.Where(p => IsIdle(p, now)).ToList();
        foreach (var p in idle)
        {
            if (!p.Napped) Nap(p);
            else if (p.OrigMemPriority == 0) LowerMemPriority(p); // pressure is back after a green-light restore
        }

        if (effective == Pressure.Red)
        {
            long min = settings.MinTrimWorkingSetMB * 1024L * 1024;
            var cooldown = TimeSpan.FromMinutes(settings.TrimCooldownMinutes);
            foreach (var p in idle.Where(p => p.Napped && p.WorkingSet >= min && now - p.LastTrim >= cooldown)
                                  .OrderByDescending(p => p.WorkingSet).Take(settings.MaxTrimsPerCycle))
                Trim(p);
        }
    }

    bool IsIdle(Proc p, DateTime now) =>
        p.Owned && !IsForeground(p) && !IsExcluded(p.Name)
        && now - foreground.LastSeen(p.Name) >= TimeSpan.FromMinutes(settings.IdleMinutes)
        && now - p.LastActive >= TimeSpan.FromSeconds(settings.QuietSeconds)
        && !IsProtected(p); // last: it reads the exe's version info, once per process

    bool IsExcluded(string name) => SystemExclusions.Contains(name) || settings.ExcludedNames.Contains(name);

    static bool IsProtected(Proc p)
    {
        if (p.Protected is bool known) return known;
        bool result = false;
        var path = Native.ImagePath(p.Pid);
        if (path != null)
        {
            result = ProtectedFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (!result)
            {
                try
                {
                    var company = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
                    result = ProtectedPublishers.Any(v => company.Contains(v, StringComparison.OrdinalIgnoreCase));
                }
                catch { /* unreadable version info: the name lists still apply */ }
            }
        }
        p.Protected = result;
        return result;
    }

    /// <summary>
    /// Opens the process only if it is still the one we sampled. After a process exits, Windows can give its PID to a
    /// new process, which must never be napped, trimmed or "restored" by mistake.
    /// </summary>
    static IntPtr OpenSame(Proc p, uint access)
    {
        var h = Native.OpenProcess(access | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        if (Native.GetProcessTimes(h, out var created, out _, out _, out _) && created == p.Created) return h;
        Native.CloseHandle(h);
        return IntPtr.Zero;
    }

    /// <summary>Same app as the foreground window, or a child of it (e.g. a browser's renderers, a terminal's shell).</summary>
    bool IsForeground(Proc p)
    {
        if (foreground.Name != null && p.Name == foreground.Name) return true;
        int fg = foreground.Pid, cur = p.Pid;
        for (int depth = 0; depth < 32 && cur > 4; depth++)
        {
            if (cur == fg) return true;
            if (!procs.TryGetValue(cur, out var q) || q.ParentPid == cur) break;
            cur = q.ParentPid;
        }
        return false;
    }

    void OnForeground(int pid, string name)
    {
        // Don't wait for the next cycle: waking must feel instant, as with App Nap.
        foreach (var p in procs.Values.Where(p => p.Napped && IsForeground(p)).ToList()) Wake(p, "回到前景");
    }

    void Nap(Proc p)
    {
        var h = OpenSame(p, Native.PROCESS_SET_INFORMATION);
        if (h == IntPtr.Zero) return; // gone, PID reused, protected or another user's process: leave it alone
        try
        {
            var changes = new List<string>();

            // 1. Lower CPU priority, only if the app runs at the default level.
            if (Native.GetPriorityClass(h) == Native.NORMAL_PRIORITY_CLASS && Native.SetPriorityClass(h, Native.BELOW_NORMAL_PRIORITY_CLASS))
            {
                p.OrigPriority = Native.NORMAL_PRIORITY_CLASS;
                changes.Add("降低優先權");
            }

            // 2. EcoQoS ("Efficiency mode"), unless the app already manages its own throttling.
            var pt = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            int ptSize = Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>();
            bool selfManaged = Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref pt, ptSize)
                               && (pt.ControlMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
            if (!selfManaged)
            {
                pt = new Native.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                };
                if (Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref pt, ptSize))
                {
                    p.EcoSet = true;
                    changes.Add("效率模式");
                }
            }

            // 3. Low memory priority: its pages are first in line when the system needs room.
            if (TryLowerMem(h, p)) changes.Add("記憶體低優先");

            if (changes.Count == 0) return;
            p.Napped = true;
            p.NappedAt = DateTime.Now;
            ActionLog.Add($"休眠 {p.Name} ({p.Pid})：{string.Join("、", changes)}");
        }
        finally { Native.CloseHandle(h); }
    }

    static bool TryLowerMem(IntPtr h, Proc p)
    {
        uint mem = 0;
        if (!Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref mem, sizeof(uint)) || mem <= Native.MEMORY_PRIORITY_LOW)
            return false;
        uint low = Native.MEMORY_PRIORITY_LOW;
        if (!Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref low, sizeof(uint))) return false;
        p.OrigMemPriority = mem;
        return true;
    }

    void LowerMemPriority(Proc p)
    {
        var h = OpenSame(p, Native.PROCESS_SET_INFORMATION);
        if (h == IntPtr.Zero) return;
        try { if (TryLowerMem(h, p)) ActionLog.Add($"記憶體壓力回升，{p.Name} ({p.Pid}) 改回記憶體低優先"); }
        finally { Native.CloseHandle(h); }
    }

    void RestoreMemPriority(Proc p)
    {
        var h = OpenSame(p, Native.PROCESS_SET_INFORMATION);
        if (h == IntPtr.Zero) return;
        try
        {
            uint mem = p.OrigMemPriority;
            if (Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref mem, sizeof(uint)))
            {
                p.OrigMemPriority = 0;
                ActionLog.Add($"記憶體充足，恢復 {p.Name} ({p.Pid}) 的記憶體優先權（仍在效率模式）");
            }
        }
        finally { Native.CloseHandle(h); }
    }

    void Trim(Proc p)
    {
        var h = OpenSame(p, Native.PROCESS_SET_QUOTA);
        if (h == IntPtr.Zero) return;
        try
        {
            long before = Native.WorkingSet(h);
            p.LastTrim = DateTime.Now; // set even on failure so we don't retry every cycle
            if (!Native.EmptyWorkingSet(h)) return;
            long after = Native.WorkingSet(h);
            long freed = Math.Max(0, before - after);
            p.WorkingSet = after;
            p.TrimmedBytes += freed;
            TotalTrimmed += freed;
            ActionLog.Add($"整理 {p.Name} ({p.Pid})：釋出 {Format.Bytes(freed)} 交給系統壓縮/換出");
        }
        finally { Native.CloseHandle(h); }
    }

    void Wake(Proc p, string reason)
    {
        // A process that has exited (or whose PID now belongs to another) needs no restoring.
        var h = OpenSame(p, Native.PROCESS_SET_INFORMATION);
        bool gone = h == IntPtr.Zero;
        if (!gone)
        {
            try
            {
                // Only undo what we changed, and only if the app has not changed it itself meanwhile.
                if (p.OrigPriority != 0 && Native.GetPriorityClass(h) == Native.BELOW_NORMAL_PRIORITY_CLASS)
                    Native.SetPriorityClass(h, p.OrigPriority);
                if (p.EcoSet)
                {
                    var pt = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 }; // masks 0 = let the system decide
                    Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref pt, Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>());
                }
                if (p.OrigMemPriority != 0)
                {
                    uint mem = p.OrigMemPriority;
                    Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref mem, sizeof(uint));
                }
            }
            finally { Native.CloseHandle(h); }
        }
        p.Napped = false;
        p.OrigPriority = p.OrigMemPriority = 0;
        p.EcoSet = false;
        p.LastActive = DateTime.Now; // don't nap it again right away
        ActionLog.Add($"喚醒 {p.Name} ({p.Pid})：{reason}{(gone ? "（程式已結束，無需還原）" : "")}");
    }

    /// <summary>Undo everything (pause, exit, sign-out).</summary>
    public void WakeAll(string reason)
    {
        foreach (var p in procs.Values.Where(p => p.Napped).ToList()) Wake(p, reason);
    }
}

static class Format
{
    public static string Bytes(long b) => b switch
    {
        < 0 => "—",
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        _ => $"{b / 1024.0:0} KB",
    };

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? $"{d.Seconds} 秒" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} 分" : $"{(int)d.TotalHours} 時 {d.Minutes} 分";
    }
}
