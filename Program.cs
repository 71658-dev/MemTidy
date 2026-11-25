namespace MemTidy;

static class Program
{
    /// <summary>Set by "MemTidy.exe --exit" to make the running instance quit cleanly.</summary>
    static EventWaitHandle? exitSignal;
    public static EventWaitHandle ExitSignal => exitSignal ??= Startup.ExitEvent();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--exit"))
        {
            ExitSignal.Set();
            return;
        }

        // "--cache-report=file.txt": write which files are in the cache and exit (needs admin). Runs beside the tray copy.
        var report = args.FirstOrDefault(a => a.StartsWith("--cache-report="));
        if (report != null)
        {
            File.WriteAllText(report[15..], StandbyFiles.Format(StandbyFiles.Query()), System.Text.Encoding.UTF8);
            return;
        }

        // "--cache-window": just the cache list, started elevated (after the Windows prompt) from the panel.
        // No tray, no settings or log files, and it ends when its window closes.
        if (args.Contains("--cache-window"))
        {
            // The tray passes its palette along, since this copy reads no settings.
            if (args.Contains("--theme=dark") || args.Contains("--theme=light")) Theme.Forced = args.Contains("--theme=dark");
            ApplicationConfiguration.Initialize();
            Application.Run(new CacheForm(TrayApp.AppIcon));
            return;
        }

        // "--open-cache": what the panel's cache button does, without the tray: asks Windows to open the cache window
        // as administrator (from the installed copy when there is one). Used by tests.
        if (args.Contains("--open-cache"))
        {
            ApplicationConfiguration.Initialize();
            try { Startup.RunElevated("--cache-window")?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { } // declined at the Windows prompt
            return;
        }

        // "--remove-legacy-task": started elevated, with the user's consent, only to delete the old administrator task.
        if (args.Contains("--remove-legacy-task"))
        {
            Startup.DeleteLegacyTask();
            return;
        }

        // MemTidy itself runs with whatever rights it was started with: normal rights from the Run key or a double-click.
        using var mutex = Startup.InstanceMutex(out bool running);
        if (running) return; // already running in the tray

        // Test switches: --force-pressure=yellow|red [--force-for=seconds], --idle-minutes=1,
        // --run-seconds=120 (exit cleanly afterwards), --show-panel
        Pressure? forced = null;
        double? idleMinutes = null;
        int runSeconds = 0, forceSeconds = 0;
        bool showPanel = args.Contains("--show-panel");
        foreach (var a in args)
        {
            if (a.StartsWith("--force-pressure=") && Enum.TryParse<Pressure>(a[17..], true, out var p)) forced = p;
            if (a.StartsWith("--idle-minutes=") && double.TryParse(a[15..], out var m)) idleMinutes = m;
            if (a.StartsWith("--run-seconds=") && int.TryParse(a[14..], out var r)) runSeconds = r;
            if (a.StartsWith("--force-for=") && int.TryParse(a[12..], out var f)) forceSeconds = f;
            if (a == "--theme=dark" || a == "--theme=light") Theme.Forced = a == "--theme=dark";
            if (a == "--demo") Demo.On = true;
        }

        ApplicationConfiguration.Initialize();
        // A UI bug must never take the process down: that would leave napped apps slowed until they restart.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ActionLog.Add($"介面錯誤（已略過）：{e.Exception.GetType().Name}: {e.Exception.Message}");
        var settings = Settings.Load();
        Theme.Preference = Theme.Parse(settings.Theme);
        if (args.Length == 0) Startup.Ensure(settings.FirstRun); // test runs never touch the startup entry
        if (idleMinutes.HasValue) // not saved
        {
            settings.IdleMinutes = idleMinutes.Value;
            settings.QuietSeconds = Math.Min(settings.QuietSeconds, (int)(idleMinutes.Value * 60));
        }
        var app = new TrayApp(settings, forced, forceSeconds, showPanel);
        // --snapshot=file.png [--snapshot-after=seconds]: render the panel to an image (testing)
        var snapshot = args.FirstOrDefault(a => a.StartsWith("--snapshot="));
        if (snapshot != null)
        {
            int after = args.FirstOrDefault(a => a.StartsWith("--snapshot-after=")) is string s && int.TryParse(s[17..], out var sec) ? sec : 3;
            var st = new System.Windows.Forms.Timer { Interval = after * 1000 };
            st.Tick += (_, _) => { st.Stop(); app.SnapshotPanel(snapshot[11..]); };
            st.Start();
        }
        if (args.FirstOrDefault(a => a.StartsWith("--snapshot-menu=")) is string menuShot)
            app.SnapshotMenu(menuShot[16..]);
        if (args.FirstOrDefault(a => a.StartsWith("--snapshot-cache=")) is string cacheShot)
            app.SnapshotCache(cacheShot[17..]);
        if (runSeconds > 0)
        {
            var t = new System.Windows.Forms.Timer { Interval = runSeconds * 1000 };
            t.Tick += (_, _) => { t.Stop(); app.ExitThread(); };
            t.Start();
        }
        Application.Run(app);
    }
}
