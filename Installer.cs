using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemTidy;

/// <summary>Why an install or removal stopped. Every one of these leaves the computer as it was.</summary>
enum InstallFailure { None, Cancelled, Hash, InUse, Failed, RiskyEnvironment }

enum StepState { Todo, Active, Done }

sealed class InstallStep(string title, string detail)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public StepState State { get; set; }
}

/// <summary>
/// What install.ps1 does, for the installer window. Only the administrator step (AdminStep.ps1, built into this exe)
/// runs as administrator: its text is handed to System32's powershell.exe as -EncodedCommand, so nothing is read from
/// a user-writable place after the user consents, and this exe (which sits in a user-writable folder) is never itself
/// started as administrator. Everything else runs with the user's normal rights, and only after the administrator
/// step has succeeded, so a failure always leaves the computer as it was.
/// </summary>
sealed class Installer
{
    public static string InstallDir => Path.GetDirectoryName(Startup.InstalledExe)!;
    public const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MemTidy.lnk");
    public const string ShortcutShown = @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\MemTidy.lnk";
    public const string DataDirShown = @"%LOCALAPPDATA%\MemTidy";

    /// <summary>Test switches: walk through the screens without changing anything (--demo, --installer-fail=…).</summary>
    public bool Simulate { get; init; }
    public InstallFailure SimulatedFailure { get; set; }

    public bool Update { get; } = File.Exists(Startup.InstalledExe);
    public bool Removing { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool LaunchAfter { get; set; } = true;
    public bool RemoveSettings { get; set; }

    public List<InstallStep> Steps { get; private set; } = [];
    public int ExitCode { get; private set; }
    public string ExpectedHash { get; private set; } = "";
    public string ActualHash { get; private set; } = "";
    public List<string> RiskyVariables { get; private set; } = [];
    /// <summary>Steps after the administrator step that failed (the program is installed or removed regardless).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Raised (on a worker thread) when the step list changes, and when Windows' consent prompt opens or closes.</summary>
    public event Action? StepsChanged;
    public event Action<bool>? WaitingForConsent;

    public static bool MemTidyRunning
    {
        get
        {
            using var _ = Startup.InstanceMutex(out bool running);
            return running;
        }
    }

    void Plan(bool running)
    {
        var steps = new List<InstallStep>();
        if (Removing)
        {
            steps.Add(new("結束 MemTidy", "先把被調整過的程式恢復原狀"));
            steps.Add(new("移除程式檔", InstallDir));
            steps.Add(new("移除開機時自動啟動", @"HKCU\…\Run\MemTidy 與開始功能表捷徑"));
            if (RemoveSettings) steps.Add(new("刪除設定與紀錄檔", DataDirShown));
        }
        else
        {
            if (running) steps.Add(new("結束執行中的 MemTidy", "先把被調整過的程式恢復原狀"));
            steps.Add(new("複製程式檔", Startup.InstalledExe));
            steps.Add(new("確認檔案完整", "SHA-256 和安裝檔一致才換上新檔"));
            if (AutoStart) steps.Add(new("設定開機時自動啟動", "以你的帳號，不需系統管理員"));
            steps.Add(new("加入開始功能表", "以你的帳號，不需系統管理員"));
            if (LaunchAfter) steps.Add(new("啟動 MemTidy", "以一般權限執行"));
        }
        Steps = steps;
        Changed();
    }

    void Changed() => StepsChanged?.Invoke();

    // ---- Sample content for --installer-step (screenshots) ----

    /// <summary>The step list with the first <paramref name="done"/> steps finished and the next one in progress.</summary>
    public void PreviewSteps(int done)
    {
        Plan(Update);
        for (int i = 0; i < Steps.Count; i++) Steps[i].State = i < done ? StepState.Done : i == done ? StepState.Active : StepState.Todo;
    }

    public void PreviewFailure(InstallFailure f)
    {
        ExitCode = f switch { InstallFailure.Hash => 2, InstallFailure.InUse => 3, _ => 1 };
        ExpectedHash = "3F9A" + new string('0', 56) + "C21E";
        ActualHash = "B07D" + new string('0', 56) + "94A0";
        if (f == InstallFailure.RiskyEnvironment) RiskyVariables = ["COR_PROFILER"];
    }

    void Begin(int i) { Steps[i].State = StepState.Active; Changed(); }
    void Finish(int i) { Steps[i].State = StepState.Done; Changed(); }

    /// <summary>Runs the whole install or removal; call on a worker thread.</summary>
    public InstallFailure Run()
    {
        Warnings.Clear();
        ExitCode = 0;
        bool running = Simulate ? Update : MemTidyRunning;
        Plan(running);
        if (Simulate) return RunSimulated(running);

        RiskyVariables = Startup.RiskyEnvironment();
        if (RiskyVariables.Count > 0)
        {
            ActionLog.Add($"安裝程式沒有要求系統管理員權限：使用者環境變數 {string.Join("、", RiskyVariables)} 可能讓其他程式碼一起執行");
            return InstallFailure.RiskyEnvironment;
        }

        // Remember what was running, to start it again if anything fails: "your computer was not changed" must hold.
        string? restart = null;
        int i = 0;
        if (Removing || running)
        {
            Begin(i);
            if (running) restart = StopMemTidy();
            Finish(i++);
        }

        string hash = "";
        if (!Removing)
        {
            hash = HashOf(Environment.ProcessPath!);
            ExpectedHash = hash;
        }

        Begin(i);
        var failure = RunAdminStep(Removing ? "uninstall" : "install", hash);
        if (failure != InstallFailure.None)
        {
            if (failure == InstallFailure.Hash) ActualHash = SafeHash(Environment.ProcessPath!);
            if (failure != InstallFailure.Cancelled) ActionLog.Add($"安裝程式：系統管理員步驟失敗（結束代碼 {ExitCode}）");
            if (restart != null) StartTray(restart);
            Steps[i].State = StepState.Todo;
            Changed();
            return failure;
        }
        Finish(i++);

        if (Removing)
        {
            Begin(i);
            Try("移除開機時自動啟動", () => Startup.Set(false));
            Try("移除開始功能表捷徑", () => { if (File.Exists(Shortcut)) File.Delete(Shortcut); });
            Finish(i++);
            if (RemoveSettings)
            {
                Begin(i);
                Try("刪除設定與紀錄檔", () => { if (Directory.Exists(Settings.Dir)) Directory.Delete(Settings.Dir, true); });
                Finish(i++);
            }
            return InstallFailure.None;
        }

        Finish(i++); // the administrator step checked the hash before putting the new file in place
        if (AutoStart) { Begin(i); Try("設定開機時自動啟動", () => Startup.Set(true)); Finish(i++); }
        else Try("取消開機時自動啟動", () => Startup.Set(false));
        Begin(i); Try("加入開始功能表", () => CreateShortcut(Shortcut, Startup.InstalledExe)); Finish(i++);
        if (LaunchAfter) { Begin(i); Try("啟動 MemTidy", () => StartTray(Startup.InstalledExe)); Finish(i++); }
        else if (restart != null) Try("重新啟動 MemTidy", () => StartTray(Startup.InstalledExe));
        return InstallFailure.None;
    }

    void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Warnings.Add($"{what}失敗：{ex.Message}");
            ActionLog.Add($"安裝程式：{what}失敗：{ex.Message}");
        }
    }

    InstallFailure RunSimulated(bool running)
    {
        // A simulated failure happens once: "try again" then succeeds, as in the design.
        var fail = SimulatedFailure;
        SimulatedFailure = InstallFailure.None;
        int i = 0;
        if (Removing || running) { Begin(i); Thread.Sleep(700); Finish(i++); }
        Begin(i);
        WaitingForConsent?.Invoke(true);
        Thread.Sleep(1800);
        WaitingForConsent?.Invoke(false);
        if (fail == InstallFailure.Cancelled) { Steps[i].State = StepState.Todo; Changed(); return InstallFailure.Cancelled; }
        Thread.Sleep(800);
        if (fail != InstallFailure.None)
        {
            PreviewFailure(fail);
            Steps[i].State = StepState.Todo;
            Changed();
            return fail;
        }
        Finish(i++);
        for (; i < Steps.Count; i++) { Begin(i); Thread.Sleep(600); Finish(i); }
        return InstallFailure.None;
    }

    // ---- Steps ----

    /// <summary>
    /// Asks the running MemTidy to quit cleanly (it restores every app it adjusted), as "MemTidy.exe --exit" does,
    /// and waits up to 10 s. Returns the exe to start again if the install fails.
    /// </summary>
    static string StopMemTidy()
    {
        int self = Environment.ProcessId;
        string? path = Process.GetProcessesByName("MemTidy").Where(p => p.Id != self)
            .Select(p => { using (p) return Native.ImagePath(p.Id); })
            .FirstOrDefault(p => p != null && !p.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        // A handle of our own on the event must not outlive this: a tray started later would see it still set and quit.
        using (var exit = Startup.ExitEvent()) exit.Set();
        var deadline = DateTime.Now.AddSeconds(10);
        while (MemTidyRunning && DateTime.Now < deadline) Thread.Sleep(300);
        return path ?? Startup.PreferredExe;
    }

    /// <summary>Starts MemTidy in the tray with normal rights (this process's own).</summary>
    static void StartTray(string exe)
    {
        bool installed = exe.Equals(Startup.InstalledExe, StringComparison.OrdinalIgnoreCase);
        using var _ = Process.Start(new ProcessStartInfo(exe, installed ? "" : "--tray") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    static string HashOf(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    static string SafeHash(string path)
    {
        try { return HashOf(path); } catch { return ""; }
    }

    public static string ShortHash(string h) => h.Length < 8 ? "—" : $"{h[..4]}…{h[^4..]}";

    static string AdminScript()
    {
        using var s = typeof(Installer).Assembly.GetManifestResourceStream("AdminStep.ps1")
                      ?? throw new InvalidOperationException("AdminStep.ps1 is missing from the build");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>The administrator step, exactly as install.ps1's Invoke-AdminStep runs it.</summary>
    InstallFailure RunAdminStep(string mode, string hash)
    {
        var command = $"& {{ {AdminScript()} }} -Mode {Quote(mode)} -Source {Quote(Environment.ProcessPath!)} -Sha256 {Quote(hash)}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var powershell = Startup.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe");
        Process? p;
        WaitingForConsent?.Invoke(true);
        try
        {
            // Returns once the user has answered Windows' prompt and the process has started.
            p = Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return InstallFailure.Cancelled; } // ERROR_CANCELLED
        catch (Win32Exception ex)
        {
            ActionLog.Add($"安裝程式：無法啟動系統管理員步驟：{ex.Message}");
            ExitCode = ex.NativeErrorCode;
            return InstallFailure.Failed;
        }
        finally { WaitingForConsent?.Invoke(false); }
        if (p == null) { ExitCode = -1; return InstallFailure.Failed; }
        using (p)
        {
            p.WaitForExit();
            ExitCode = p.ExitCode;
        }
        return ExitCode switch { 0 => InstallFailure.None, 2 => InstallFailure.Hash, 3 => InstallFailure.InUse, _ => InstallFailure.Failed };
    }

    // ---- Start menu shortcut (IShellLinkW, no extra packages) ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    static void CreateShortcut(string lnk, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("MemTidy 記憶體整理");
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>The theme picked in settings.json, read without creating the file (Settings.Load would).</summary>
    public static bool? ThemePreference()
    {
        try
        {
            var file = Path.Combine(Settings.Dir, "settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("Theme", out var t) && t.ValueKind == JsonValueKind.String ? Theme.Parse(t.GetString()) : null;
        }
        catch { return null; }
    }
}
