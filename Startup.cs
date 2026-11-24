using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MemTidy;

/// <summary>
/// MemTidy runs as administrator by default. Starting elevated at sign-in without a UAC prompt each time
/// is done with a Task Scheduler task set to "run with highest privileges" (created once, when elevated);
/// a normal launch hands over to that task, or asks UAC once if the task does not exist yet.
/// </summary>
static class Startup
{
    const string TaskName = "MemTidy";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static string ExePath => Environment.ProcessPath!;

    // ---- Becoming administrator ----

    /// <summary>
    /// Called on a normal (non-elevated) launch. Returns true when an elevated copy was started and this one should exit;
    /// false to carry on with normal rights (UAC declined, or it could not be started).
    /// </summary>
    public static bool HandOverToElevated()
    {
        // The startup task already runs this exe elevated: starting it needs no UAC prompt.
        if (TaskCommand() is string cmd && SamePath(cmd, ExePath) && Schtasks($"/run /tn {TaskName}", out _) == 0)
            return true;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ActionLog.Add("未取得系統管理員權限，以一般權限執行");
            return false;
        }
    }

    // ---- Start at sign-in ----

    public static bool Enabled => TaskCommand() != null || RunKeyValue() != null;

    /// <summary>Turn sign-in start on/off. Elevated: scheduled task (starts as admin, no prompt). Otherwise: the Run key.</summary>
    public static void Set(bool on)
    {
        if (!on)
        {
            if (TaskCommand() != null) Schtasks($"/delete /tn {TaskName} /f", out _);
            DeleteRunKey();
            return;
        }
        if (IsAdmin && CreateTask())
        {
            DeleteRunKey();
            return;
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue("MemTidy", $"\"{ExePath}\"");
    }

    /// <summary>
    /// On a normal launch: enable sign-in start on the very first run (unticking it later is respected),
    /// upgrade an old Run-key entry to the elevated task, and follow the exe if it has moved.
    /// </summary>
    public static void Ensure(bool firstRun)
    {
        try
        {
            var task = TaskCommand();
            var run = RunKeyValue();
            if (firstRun && task == null && run == null) { Set(true); ActionLog.Add("已設定開機時自動啟動"); return; }
            if (!IsAdmin) return;
            if (run != null) { Set(true); ActionLog.Add("開機啟動改為以系統管理員執行（工作排程器）"); return; }
            if (task != null && !SamePath(task, ExePath)) { Set(true); ActionLog.Add("程式位置已變更，已更新開機啟動路徑"); }
        }
        catch (Exception ex) { ActionLog.Add($"開機啟動設定失敗：{ex.Message}"); }
    }

    static string? RunKeyValue()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("MemTidy") as string;
    }

    static void DeleteRunKey()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue("MemTidy", false);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().Trim('"'), b.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe the startup task runs, or null if there is no task.</summary>
    static string? TaskCommand()
    {
        if (Schtasks($"/query /tn {TaskName} /xml", out var xml) != 0) return null;
        int a = xml.IndexOf("<Command>", StringComparison.Ordinal), b = xml.IndexOf("</Command>", StringComparison.Ordinal);
        return a >= 0 && b > a ? System.Net.WebUtility.HtmlDecode(xml[(a + 9)..b]) : "";
    }

    static bool CreateTask()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(ExePath);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>MemTidy 記憶體整理：登入時以系統管理員身分常駐系統匣</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "memtidy-task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            if (Schtasks($"/create /tn {TaskName} /xml \"{file}\" /f", out var output) == 0) return true;
            ActionLog.Add($"建立開機工作失敗：{output.Trim()}");
            return false;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    static int Schtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---- Named objects both elevated and normal copies can use ----
    // Objects made by an elevated process are normally off-limits to normal ones (no write-up), which would break
    // single-instance detection and "--exit". Give them a medium integrity label and full access for this user.

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
        // Can't even open it: an older elevated copy made it without the shared label, so one is running.
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
