using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MemTidy;

/// <summary>
/// MemTidy runs with normal rights and starts at sign-in through the per-user Run key. Only the cache window asks
/// for administrator rights, on demand, as its own short-lived process.
///
/// Builds from 2026-11-24 ran as administrator through a scheduled task named "MemTidy". That task is removed on
/// upgrade: it started an exe in a user-writable folder with highest privileges and could be triggered without a
/// prompt, so any program running as the user could gain administrator rights through it.
/// </summary>
static class Startup
{
    const string LegacyTaskName = "MemTidy";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static string ExePath => Environment.ProcessPath!;

    /// <summary>
    /// Where install.ps1 puts MemTidy. Only administrators can change files there, so the copy that runs as
    /// administrator (the cache window) can't be swapped or have files planted next to it.
    /// </summary>
    public static string InstalledExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MemTidy", "MemTidy.exe");

    /// <summary>
    /// The installed copy when there is one, else this exe: the copy that starts at sign-in and the only one ever
    /// asked to run as administrator, even when a build from another folder is the one running.
    /// </summary>
    public static string PreferredExe => File.Exists(InstalledExe) ? InstalledExe : ExePath;

    /// <summary>
    /// User environment variables that make a .NET program load extra code when it starts (profilers, another
    /// runtime, extra dependencies). Any program running as the user can set them, and a program started as
    /// administrator would inherit them. Startup hooks are switched off in MemTidy; the rest can only be refused up
    /// front. Machine-wide variables are left alone: only administrators can set those.
    /// </summary>
    public static List<string> RiskyEnvironment()
    {
        static bool Risky(string name) =>
            name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COR_PROFILER", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DOTNET_ADDITIONAL_DEPS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DOTNET_SHARED_STORE", StringComparison.OrdinalIgnoreCase);

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyName in new[] { "Environment", "Volatile Environment" })
        {
            using var k = Registry.CurrentUser.OpenSubKey(keyName);
            if (k == null) continue;
            foreach (var n in k.GetValueNames()) if (Risky(n)) found.Add(n);
        }
        return found.ToList();
    }

    /// <summary>
    /// Asks Windows to run the preferred copy as administrator with <paramref name="args"/>; refuses (and explains)
    /// if the user environment could slip other code into it. Throws Win32Exception if the user declines the prompt.
    /// </summary>
    public static Process? RunElevated(string args)
    {
        var risky = RiskyEnvironment();
        if (risky.Count > 0)
        {
            ActionLog.Add($"已取消以系統管理員執行：使用者環境變數 {string.Join("、", risky)} 可能讓其他程式碼一起執行");
            MessageBox.Show(
                "MemTidy 沒有以系統管理員身分開啟，因為你的使用者環境變數設定了：\n\n" + string.Join("\n", risky) + "\n\n" +
                "這些變數會讓 .NET 程式啟動時載入額外的程式碼。如果不是你自己設定的，可能是惡意程式所為，建議請資訊人員檢查。",
                "記憶體整理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return Process.Start(new ProcessStartInfo(PreferredExe, args) { UseShellExecute = true, Verb = "runas" });
    }

    /// <summary>
    /// System tools are always started by full path: started by bare name, Windows would first look in MemTidy's own
    /// folder, where any program running as the user could have put a look-alike.
    /// </summary>
    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);
    public static string WindowsTool(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);

    // ---- Start at sign-in (per user, normal rights) ----

    public static bool Enabled => RunKeyValue() != null;

    public static void Set(bool on)
    {
        if (on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue("MemTidy", $"\"{PreferredExe}\"");
        }
        else
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue("MemTidy", false);
        }
    }

    /// <summary>True when the old administrator task is still there and needs the user's go-ahead to be removed.</summary>
    public static bool LegacyTaskPending { get; private set; }

    /// <summary>
    /// On a plain launch: retire the old administrator task, enable sign-in start on the very first run (unticking it
    /// later is respected), and follow the exe if it has moved. Never shows UI: if removing the old task needs the
    /// user, <see cref="LegacyTaskPending"/> is set and the tray asks once it is up.
    /// </summary>
    public static void Ensure(bool firstRun)
    {
        try
        {
            if (LegacyTaskExists())
            {
                Set(true); // the old task meant "start at sign-in": keep that, with normal rights
                if (DeleteLegacyTask()) ActionLog.Add(LegacyDone);
                else LegacyTaskPending = true;
                return;
            }
            var run = RunKeyValue();
            if (firstRun && run == null) { Set(true); ActionLog.Add("已設定開機時自動啟動"); }
            // Follow the exe if it moved; once installed, a build run from elsewhere never takes over sign-in start.
            else if (run != null && !SamePath(run, PreferredExe)) { Set(true); ActionLog.Add($"開機啟動改為：{PreferredExe}"); }
        }
        catch (Exception ex) { ActionLog.Add($"開機啟動設定失敗：{ex.Message}"); }
    }

    static string? RunKeyValue()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("MemTidy") as string;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().Trim('"'), b.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    // ---- Retiring the old administrator task ----

    public static bool LegacyTaskExists() => RunSchtasks($"/query /tn {LegacyTaskName}") == 0;

    /// <summary>Used directly by "MemTidy.exe --remove-legacy-task", the elevated helper the user approves.</summary>
    public static bool DeleteLegacyTask() => RunSchtasks($"/delete /tn {LegacyTaskName} /f") == 0;

    const string LegacyDone = "已移除舊版以系統管理員執行的開機工作，改用一般權限啟動";
    const string LegacyKept = "舊的系統管理員開機工作仍保留（下次啟動會再詢問）";

    /// <summary>
    /// Called by the tray once it is up. A task that runs with highest privileges can usually only be deleted with
    /// administrator rights, so explain why it should go, then let Windows ask for consent. The dialog is kept on top:
    /// a program started at sign-in or in the background may not bring its windows to the front by itself.
    /// </summary>
    public static async Task OfferLegacyTaskRemoval()
    {
        if (!LegacyTaskPending) return;
        DialogResult answer;
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                      StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
        {
            owner.Show();
            owner.Activate();
            answer = MessageBox.Show(owner,
                "舊版 MemTidy 設定了「登入時以系統管理員身分執行」的排程工作。\n\n" +
                "這個做法有安全風險：在你帳號下執行的其他程式，可能藉由它取得系統管理員權限。" +
                "新版改用一般權限執行，已不需要這個排程工作。\n\n" +
                "要移除它嗎？（會跳出一次 Windows 確認視窗）",
                "記憶體整理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        }
        if (answer != DialogResult.Yes) { ActionLog.Add(LegacyKept); return; }
        try
        {
            using var helper = RunElevated("--remove-legacy-task");
            if (helper != null) await Task.Run(() => helper.WaitForExit(60_000)); // keep the tray responsive meanwhile
        }
        catch (System.ComponentModel.Win32Exception) { } // declined at the Windows prompt
        LegacyTaskPending = await Task.Run(LegacyTaskExists);
        ActionLog.Add(LegacyTaskPending ? LegacyKept : LegacyDone);
    }

    static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo(SystemTool("schtasks.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---- Named objects every copy of MemTidy can use ----
    // Objects made by an elevated process are normally off-limits to normal ones (no write-up), which would break
    // single-instance detection and "--exit" if MemTidy is ever started as administrator by hand. Give them a medium
    // integrity label and access for this user, SYSTEM and administrators only.

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, IntPtr size);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateMutexW(ref SECURITY_ATTRIBUTES sa, bool initialOwner, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);

    const int ErrorAlreadyExists = 183;

    static T WithSharedSecurity<T>(Func<SECURITY_ATTRIBUTES, T> create)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var sddl = $"D:(A;;GA;;;{sid})(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            return create(sa);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Creates or opens the single-instance mutex; alreadyRunning is true if another copy holds it.</summary>
    public static SafeWaitHandle InstanceMutex(out bool alreadyRunning)
    {
        bool exists = false;
        var h = WithSharedSecurity(sa =>
        {
            var handle = CreateMutexW(ref sa, false, @"Local\MemTidy-singleton");
            exists = Marshal.GetLastWin32Error() == ErrorAlreadyExists;
            return handle;
        });
        // Can't even open it: a copy made it without the shared label, so one is running.
        alreadyRunning = exists || h == IntPtr.Zero;
        return new SafeWaitHandle(h, true);
    }

    /// <summary>The event "MemTidy.exe --exit" sets to make the running copy quit cleanly.</summary>
    public static EventWaitHandle ExitEvent()
    {
        var h = WithSharedSecurity(sa => CreateEventW(ref sa, false, false, @"Local\MemTidy-exit"));
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var e = new EventWaitHandle(false, EventResetMode.AutoReset);
        e.SafeWaitHandle = new SafeWaitHandle(h, true);
        return e;
    }
}
