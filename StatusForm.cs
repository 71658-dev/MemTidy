namespace MemTidy;

/// <summary>
/// The main panel, following the MemTidy UI design: pressure graph, memory breakdown bar, status card
/// with the two actions, the napping apps and the recent actions. Light or dark with the Windows app mode.
/// </summary>
sealed class StatusForm : Form
{
    readonly PressureMonitor pressure;
    readonly ProcessNapper napper;
    readonly Settings settings;
    readonly MemoryBreakdown breakdown = new();
    readonly GraphPanel graph;
    readonly BreakdownBar bar = new();
    readonly StatusCard card = new() { Margin = new Padding(0, 16, 0, 0) };
    readonly SectionHeader napHeader = new("休眠中的背景程式") { Note = "切回該程式即自動恢復" };
    readonly ZebraList napList = new(("程式", 200, Cell.Strong), ("PID", 80, Cell.Muted), ("目前占用", 100, Cell.Text), ("已休眠", 100, Cell.Muted), ("累計釋出", 100, Cell.Text));
    readonly ZebraList logList = new(("時間", 90, Cell.Muted), ("類型", 90, Cell.Dot), ("動作", 400, Cell.Text));
    DateTime lastLogTop;

    public StatusForm(PressureMonitor pressure, ProcessNapper napper, Settings settings, Icon icon, Action tidyNow, Action showCache)
    {
        this.pressure = pressure;
        this.napper = napper;
        this.settings = settings;
        Theme.Refresh();
        var p = Theme.Current;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = Startup.IsAdmin ? "記憶體整理（系統管理員）" : "記憶體整理";
        Icon = icon;
        Font = Theme.Body;
        BackColor = p.Window;
        ForeColor = p.Text;
        ClientSize = new Size(780, 900);
        MinimumSize = new Size(660, 760);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosed += (_, _) => breakdown.Dispose();

        graph = new GraphPanel(pressure);
        var tidy = new MacButton("立即整理閒置程式") { Primary = true, Behind = q => q.Fill };
        tidy.Click += (_, _) => tidyNow();
        var cache = new MacButton(StandbyFiles.IsAdmin ? "查看快取內容" : "查看快取內容（需管理員）") { Behind = q => q.Fill };
        cache.Click += (_, _) => showCache();
        card.AddButton(tidy);
        card.AddButton(cache);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 12, 24, 20), BackColor = p.Window,
        };
        void Row(Control c, SizeType type, float size)
        {
            layout.RowStyles.Add(new RowStyle(type, size));
            layout.Controls.Add(c);
        }
        Row(new SectionHeader("記憶體使用量 · 最近 5 分鐘")
        {
            Legend = [("正常", q => q.Green), ("偏高", q => q.Yellow), ("吃緊", q => q.Red)],
        }, SizeType.Absolute, 34);
        Row(graph, SizeType.Absolute, 160);
        Row(new SectionHeader("記憶體分配"), SizeType.Absolute, 40);
        Row(bar, SizeType.Absolute, 46);
        Row(card, SizeType.Absolute, 172);
        Row(napHeader, SizeType.Absolute, 40);
        Row(new ListFrame(napList), SizeType.Percent, 40);
        Row(new SectionHeader("最近動作"), SizeType.Absolute, 40);
        Row(new ListFrame(logList), SizeType.Percent, 60);
        Controls.Add(layout);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    public void RefreshView()
    {
        var m = pressure.Status;
        long total = (long)m.ullTotalPhys, avail = (long)m.ullAvailPhys;
        long commit = (long)(m.ullTotalPageFile - m.ullAvailPageFile), limit = (long)m.ullTotalPageFile;

        var level = pressure.Level;
        card.DotColor = q => settings.Paused ? q.Gray : q.Of(level);
        card.Heading = $"記憶體壓力：{PressureMonitor.Label(level)}{(pressure.Forced ? "（模擬）" : "")}";
        var notes = new List<string>();
        if (settings.Paused) notes.Add("自動整理已暫停");
        if (pressure.KernelLowMemory) notes.Add("系統回報記憶體不足");
        card.Note = notes.Count > 0 ? string.Join(" · ", notes) : null;
        card.Facts =
        [
            ("實體記憶體", Format.Bytes(total)),
            ("已使用", $"{Format.Bytes(total - avail)}（{pressure.UsedPct:0}%）"),
            ("可用（含快取）", Format.Bytes(avail)),
            ("認可量", $"{Format.Bytes(commit).Replace(" GB", "")} / {Format.Bytes(limit)}（{pressure.CommitPct:0}%）"),
            ("系統壓縮", pressure.CompressedBytes < 0 ? "—（需系統管理員）" : Format.Bytes(pressure.CompressedBytes)),
            ("本次累計釋出", Format.Bytes(Demo.On ? Demo.TotalTrimmed : napper.TotalTrimmed)),
        ];
        card.Invalidate();
        graph.Invalidate();

        if (breakdown.Read() is var (cache, empty))
            bar.Set(total, [(total - cache - empty, "程式使用中", q => q.Accent), (cache, "快取（可隨時釋出）", q => q.Green), (empty, "完全閒置", q => q.Gray)]);
        else
            bar.Set(total, [(total - avail, "程式使用中", q => q.Accent), (avail, "可用（含快取）", q => q.Green)]);

        var naps = Demo.On
            ? Demo.Naps.Select(d => new ProcessNapper.NapView(d.Pid, d.Name, d.WorkingSet, DateTime.Now - d.Napped, d.Trimmed)).ToList()
            : napper.Napped.ToList();
        napHeader.Title = $"休眠中的背景程式（{naps.Count}）";
        napList.SetRows(naps.Select(n => new ListRow(
            [n.Name, n.Pid.ToString(), Format.Bytes(n.WorkingSet), Format.Ago(n.NappedAt), n.TrimmedBytes > 0 ? Format.Bytes(n.TrimmedBytes) : "—"])));

        if (Demo.On)
        {
            if (lastLogTop == default)
            {
                lastLogTop = DateTime.Now;
                var dp = Theme.Current;
                logList.SetRows(Demo.Log.Select(d => LogRow(new ActionLog.Entry(DateTime.Now - d.Ago, d.Text), dp)));
            }
            return;
        }
        var first = ActionLog.Recent.FirstOrDefault();
        if (first.Time != lastLogTop)
        {
            lastLogTop = first.Time;
            var p = Theme.Current;
            logList.SetRows(ActionLog.Recent.Take(100).Select(e => LogRow(e, p)));
        }
    }

    /// <summary>Splits a log line into the design's kind tag (with its colour) and the rest of the text.</summary>
    static ListRow LogRow(ActionLog.Entry e, Palette p)
    {
        var t = e.Text;
        var (kind, color, text) =
            t.StartsWith("休眠 ") ? ("休眠", p.Indigo, t[3..]) :
            t.StartsWith("整理 ") ? ("整理", p.Accent, t[3..]) :
            t.StartsWith("喚醒 ") ? ("喚醒", p.Green, t[3..]) :
            t.StartsWith("記憶體壓力：") ? ("壓力", p.Orange, t[6..]) :
            t.StartsWith("記憶體充足") || t.StartsWith("記憶體壓力回升") ? ("優先權", p.Indigo, t) :
            ("系統", p.Gray, t);
        return new ListRow([e.Time.ToString("HH:mm:ss"), kind, text], color);
    }
}
