using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace MemTidy;

sealed class TrayApp : ApplicationContext
{
    static readonly Color GreenC = Color.FromArgb(52, 199, 89), YellowC = Color.FromArgb(255, 179, 0),
                          RedC = Color.FromArgb(255, 59, 48), PausedC = Color.FromArgb(142, 142, 147);

    public static Color ColorOf(Pressure p) => p switch { Pressure.Green => GreenC, Pressure.Yellow => YellowC, _ => RedC };

    readonly Settings settings;
    readonly PressureMonitor pressure;
    readonly ForegroundWatcher foreground;
    readonly ProcessNapper napper;
    readonly MemoryBreakdown breakdown = new();
    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly Dictionary<string, Icon> icons = new();
    readonly System.Windows.Forms.Timer sampleTimer = new() { Interval = PressureMonitor.SampleSeconds * 1000 };
    readonly System.Windows.Forms.Timer cycleTimer = new() { Interval = 10_000 };
    readonly ToolStripMenuItem pauseItem, startupItem, appearanceItem;
    StatusForm? panel;
    Pressure lastLevel = Pressure.Green;
    bool restored;

    readonly RegisteredWaitHandle exitWait;

    public TrayApp(Settings settings, Pressure? forced, int forceSeconds, bool showPanel)
    {
        this.settings = settings;
        pressure = new PressureMonitor(forced, forceSeconds);
        foreground = new ForegroundWatcher();
        napper = new ProcessNapper(settings, foreground);

        pauseItem = new ToolStripMenuItem("暫停自動整理", null, (_, _) => TogglePause()) { Checked = settings.Paused };
        startupItem = new ToolStripMenuItem("開機時自動啟動", null, (_, _) => ToggleStartup()) { Checked = Startup.Enabled };
        appearanceItem = new ToolStripMenuItem("外觀");
        foreach (var (mode, label) in new[] { ("system", "跟隨 Windows"), ("light", "淺色"), ("dark", "深色") })
            appearanceItem.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetTheme(mode))
                { Tag = mode, Checked = settings.Theme == mode, Padding = new Padding(2, 3, 2, 3) });
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("開啟面板", null, (_, _) => ShowPanel()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("立即整理閒置程式", null, (_, _) => TidyNow());
        menu.Items.Add("排除清單…", null, (_, _) => ShowExclusions());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pauseItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(appearanceItem);
        menu.Items.Add("開啟設定資料夾", null, (_, _) => Settings.OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        // The design's menu: flat surface, rounded blue highlight, rounded window corners on Windows 11.
        menu.Renderer = new MenuRenderer();
        menu.Font = Theme.Body;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(0, 5, 0, 5);
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem) item.Padding = new Padding(2, 3, 2, 3);
        menu.Opening += (_, _) => { Theme.Refresh(); Theme.RoundCorners(menu.Handle); };
        var sub = (ToolStripDropDownMenu)appearanceItem.DropDown;
        sub.Renderer = menu.Renderer;
        sub.Font = Theme.Body;
        sub.ShowImageMargin = false;
        sub.ShowCheckMargin = true;
        sub.Padding = menu.Padding;
        sub.Opening += (_, _) => Theme.RoundCorners(sub.Handle);

        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        // WinForms installs its UI context with the first control (the menu above): capture it only now.
        ui = SynchronizationContext.Current!;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };

        sampleTimer.Tick += (_, _) => OnSample();
        cycleTimer.Tick += (_, _) => napper.Cycle(pressure.Level, Roomy);
        SystemEvents.SessionEnding += OnSessionEnding;
        // A program just excluded is woken now, not at the next 10-second cycle.
        settings.ExclusionsChanged += () =>
        {
            napper.Cycle(pressure.Level, Roomy);
            panel?.RefreshView();
        };

        var rights = Startup.IsAdmin ? "（以系統管理員執行：本次不寫入紀錄檔與設定檔）" : "";
        ActionLog.Add(forced.HasValue ? $"啟動（模擬壓力：{PressureMonitor.Label(forced.Value)}）{rights}" : $"啟動{rights}");
        OnSample();
        if (Demo.On) pressure.SeedDemoHistory(); // after the first reading, so the demo graph ends at the real usage
        napper.Cycle(pressure.Level, Roomy); // baseline CPU counters
        sampleTimer.Start();
        cycleTimer.Start();

        // Startup pages (JIT, WinForms init) are never needed again: hand them back once settled.
        var settle = new System.Windows.Forms.Timer { Interval = 5000 };
        // The panel's memory breakdown counters take a second or more to open: do that now, off the UI thread, so the
        // panel opens at once. Trim only once they are open, so their pages go too.
        settle.Tick += (_, _) => { settle.Dispose(); WarmBreakdown().ContinueWith(_ => ui.Post(_ => TrimSelf(), null)); };
        settle.Start();

        // "MemTidy.exe --exit" from a script or build.ps1 asks this instance to quit cleanly (restoring every app).
        exitWait = ThreadPool.RegisterWaitForSingleObject(Program.ExitSignal, (_, _) => ui.Post(_ => ExitThread(), null), null, -1, true);

        if (showPanel) ShowPanel();

        // Retiring the old administrator task needs the user's go-ahead: ask once the tray is up, not before.
        if (Startup.LegacyTaskPending)
        {
            var ask = new System.Windows.Forms.Timer { Interval = 1500 };
            ask.Tick += async (_, _) => { ask.Stop(); ask.Dispose(); await Startup.OfferLegacyTaskRemoval(); };
            ask.Start();
        }
    }

    /// <summary>At least 30% available (cache included: Windows can hand it out instantly).</summary>
    bool Roomy => pressure.AvailPct >= 30;

    static Icon? appIcon;
    public static Icon AppIcon => appIcon ??= Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

    /// <summary>Opens the breakdown counters in the background (once); an open panel shows them as soon as they are ready.</summary>
    Task WarmBreakdown() => breakdown.WarmUp(() => ui.Post(_ => panel?.RefreshView(), null));

    static void TrimSelf()
    {
        GC.Collect();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Native.SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    void OnSample()
    {
        pressure.Sample();
        if (pressure.Level != lastLevel)
        {
            ActionLog.Add($"記憶體壓力：{PressureMonitor.Label(lastLevel)} → {PressureMonitor.Label(pressure.Level)}");
            // React immediately when pressure rises instead of waiting up to 10 s.
            if (pressure.Level > lastLevel) napper.Cycle(pressure.Level, Roomy);
            lastLevel = pressure.Level;
        }
        var color = settings.Paused ? PausedC : ColorOf(pressure.Level);
        tray.Icon = IconFor(color);
        var text = $"記憶體壓力：{PressureMonitor.Label(pressure.Level)}{(settings.Paused ? "（已暫停）" : "")}\n" +
                   $"已用 {pressure.UsedPct:0}%　休眠中 {napper.NappedCount} 個程式";
        tray.Text = text.Length > 127 ? text[..127] : text;
        panel?.RefreshView();
        exclusions?.RefreshView();
    }

    void TidyNow()
    {
        ActionLog.Add("手動整理");
        napper.Cycle(pressure.Level, Roomy, manual: true);
        panel?.RefreshView();
    }

    void TogglePause()
    {
        settings.Paused = !settings.Paused;
        pauseItem.Checked = settings.Paused;
        settings.Save();
        if (settings.Paused) napper.WakeAll("暫停自動整理");
        ActionLog.Add(settings.Paused ? "已暫停自動整理" : "恢復自動整理");
        OnSample();
    }

    void ToggleStartup()
    {
        try
        {
            Startup.Set(!Startup.Enabled);
            startupItem.Checked = Startup.Enabled;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "記憶體整理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void SetTheme(string mode)
    {
        settings.Theme = mode;
        settings.Save();
        foreach (ToolStripMenuItem item in appearanceItem.DropDownItems) item.Checked = (string)item.Tag! == mode;
        Theme.Preference = Theme.Parse(mode);
        bool wasDark = Theme.Current.Dark;
        Theme.Refresh();
        if (Theme.Current.Dark == wasDark) return;
        // The windows take their colours when they are built: open ones are rebuilt in the new look, in place.
        if (panel != null)
        {
            var bounds = panel.Bounds;
            panel.Close();
            ShowPanel();
            panel!.Bounds = bounds;
        }
        if (cacheForm != null)
        {
            cacheForm.Close();
            ShowCache();
        }
        if (exclusions != null)
        {
            var bounds = exclusions.Bounds;
            exclusions.Close();
            ShowExclusions();
            exclusions!.Bounds = bounds;
        }
    }

    ExclusionsForm? exclusions;

    void ShowExclusions()
    {
        if (exclusions == null)
        {
            exclusions = new ExclusionsForm(settings, napper, AppIcon);
            exclusions.FormClosed += (_, _) => { exclusions = null; TrimSelf(); };
            exclusions.Show();
        }
        if (exclusions.WindowState == FormWindowState.Minimized) exclusions.WindowState = FormWindowState.Normal;
        exclusions.Activate();
    }

    /// <summary>Test switch --snapshot-exclusions=file.png: opens the exclusions window and captures it 2 s later.</summary>
    public void SnapshotExclusions(string path, string? addPath)
    {
        ShowExclusions();
        var add = addPath != null ? exclusions!.ShowAdd() : null;
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Snapshot(exclusions!, path);
            if (add != null) Snapshot(add, addPath!);
        };
        t.Start();
    }

    CacheForm? cacheForm;

    /// <summary>Test switch --snapshot=file.png: the panel renders itself to an image (works with a locked screen).</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    public void SnapshotPanel(string path)
    {
        ShowPanel();
        if (panel != null) Snapshot(panel, path);
    }

    /// <summary>Test switch --snapshot-cache=file.png: opens the cache window in this process and captures it 4 s later.</summary>
    public void SnapshotCache(string path)
    {
        var f = new CacheForm(AppIcon);
        f.Show();
        var t = new System.Windows.Forms.Timer { Interval = 4000 };
        t.Tick += (_, _) => { t.Stop(); Snapshot(f, path); f.Close(); };
        t.Start();
    }

    /// <summary>Test switch --snapshot-menu=file.png: opens the tray menu (hovering the first item) and captures it.</summary>
    public void SnapshotMenu(string path)
    {
        var menu = tray.ContextMenuStrip!;
        menu.Show(new Point(400, 300));
        menu.Items[0].Select();
        var t = new System.Windows.Forms.Timer { Interval = 800 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            using var bmp = new Bitmap(menu.Width, menu.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                PrintWindow(menu.Handle, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(path);
            menu.Close();
        };
        t.Start();
    }

    void Snapshot(Form panel, string path)
    {
        // PrintWindow with PW_RENDERFULLCONTENT captures what DWM composes, title bar included.
        using var bmp = new Bitmap(panel.Width, panel.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(panel.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path);
        ActionLog.Add($"面板截圖：{path}");
    }

    void ShowCache()
    {
        if (!StandbyFiles.IsAdmin)
        {
            // Only the cache window runs elevated, as its own short-lived process, from the installed copy when there
            // is one; the tray copy keeps normal rights.
            try { Startup.RunElevated($"--cache-window {Theme.Arg}")?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { } // user declined the UAC prompt
            return;
        }

        if (cacheForm == null)
        {
            cacheForm = new CacheForm(AppIcon);
            cacheForm.FormClosed += (_, _) => { cacheForm = null; TrimSelf(); };
            cacheForm.Show();
        }
        cacheForm.Activate();
    }

    void ShowPanel()
    {
        if (panel == null)
        {
            WarmBreakdown(); // opened before the 5 s settle: start the counters now
            panel = new StatusForm(pressure, napper, settings, breakdown, AppIcon, TidyNow, ShowCache, ShowExclusions);
            panel.FormClosed += (_, _) =>
            {
                panel = null;
                TrimSelf(); // a resident tool should not keep UI pages around
            };
            panel.RefreshView(); // fill before the first paint, not right after it
            panel.Show();
        }
        else panel.RefreshView();
        if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
        panel.Activate();
    }

    /// <summary>A rounded memory-chip glyph in the pressure colour, drawn at the tray's size.</summary>
    Icon IconFor(Color c)
    {
        var size = SystemInformation.SmallIconSize.Width;
        var key = $"{c.ToArgb()}-{size}";
        if (icons.TryGetValue(key, out var cached)) return cached;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pin = Math.Max(1f, size / 16f), inset = size * 0.18f, body = size - 2 * inset;
            using var pinPen = new Pen(c, pin);
            for (int i = 0; i < 3; i++)
            {
                float t = inset + body * (0.2f + 0.3f * i);
                g.DrawLine(pinPen, t, 0.5f, t, inset);
                g.DrawLine(pinPen, t, size - inset, t, size - 0.5f);
                g.DrawLine(pinPen, 0.5f, t, inset, t);
                g.DrawLine(pinPen, size - inset, t, size - 0.5f, t);
            }
            using var path = RoundRect(new RectangleF(inset, inset, body, body), body * 0.22f);
            using var fill = new SolidBrush(c);
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(110, 0, 0, 0), Math.Max(1f, size / 20f));
            g.DrawPath(edge, path);
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        Native.DestroyIcon(h);
        icons[key] = icon;
        return icon;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Restore("登出或關機");

    void Restore(string reason)
    {
        if (restored) return;
        restored = true;
        napper.WakeAll(reason);
    }

    protected override void ExitThreadCore()
    {
        sampleTimer.Stop();
        cycleTimer.Stop();
        Restore("結束程式");
        ActionLog.Add("結束");
        SystemEvents.SessionEnding -= OnSessionEnding;
        exitWait.Unregister(null);
        panel?.Close();
        cacheForm?.Close();
        exclusions?.Close();
        tray.Visible = false;
        tray.Dispose();
        foreground.Dispose();
        pressure.Dispose();
        breakdown.Dispose();
        base.ExitThreadCore();
    }
}
