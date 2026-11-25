namespace MemTidy;

/// <summary>
/// The exclusions window: programs that are never napped or trimmed. Changes apply at once and are saved to
/// settings.json; a hand edit of that file is picked up when the window is activated again.
/// </summary>
sealed class ExclusionsForm : Form
{
    readonly Settings settings;
    readonly ProcessNapper napper;
    readonly Icon icon;
    readonly PageHeader header = new()
    {
        Title = "排除清單",
        Subtitle = "清單裡的程式在背景時不會被降速或釋出記憶體。適合通話、音樂、錄影等需要持續運作的程式。",
        ReserveRight = 150,
    };
    readonly MacButton addButton = new("新增程式") { Primary = true, Radius = 99, Glyph = Glyphs.Add, Behind = p => p.GroupedBack };
    readonly SearchBox search = new("搜尋程式名稱或 .exe");
    readonly Segmented filter = new();
    readonly SuggestionCard suggestion = new() { Margin = new Padding(0, 14, 0, 0) };
    readonly Caption caption = new() { End = "依名稱排序" };
    readonly AppList list = new(adds: false);
    readonly TableLayoutPanel layout;
    readonly Label note = new() { AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
    AddAppForm? addForm;
    string? shownSuggestion;

    public ExclusionsForm(Settings settings, ProcessNapper napper, Icon icon)
    {
        this.settings = settings;
        this.napper = napper;
        this.icon = icon;
        SuspendLayout();
        Theme.Refresh();
        var p = Theme.Current;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = "排除清單 — 記憶體整理";
        Icon = icon;
        Font = Theme.Body;
        BackColor = p.GroupedBack;
        ForeColor = p.Text;
        ClientSize = new Size(760, 860);
        MinimumSize = new Size(600, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        header.Controls.Add(addButton);
        header.Resize += (_, _) =>
        {
            float s = header.DeviceDpi / 96f;
            addButton.Bounds = new Rectangle(header.Width - (int)(124 * s), (int)(2 * s), (int)(124 * s), (int)(40 * s));
        };
        addButton.Font = Theme.UI(10.5f, FontStyle.Bold);
        addButton.Click += (_, _) => ShowAdd();

        filter.SelectedChanged += RefreshView;
        search.Input.TextChanged += (_, _) => RefreshView();
        suggestion.Skip += () =>
        {
            if (shownSuggestion != null) settings.DismissSuggestion(shownSuggestion);
            RefreshView();
        };
        suggestion.Add.Click += (_, _) => { if (shownSuggestion != null) settings.Exclude(shownSuggestion); };
        list.Act += row => settings.Include(row.Exe);

        var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = p.GroupedBack };
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // else the row takes the controls' default 100 px height
        search.Margin = new Padding(0, 0, 14, 0);
        filter.Margin = Padding.Empty;
        tools.Controls.Add(search);
        tools.Controls.Add(filter);

        var restore = new FooterLink("還原預設清單");
        restore.LinkClicked += (_, _) => RestoreDefaults();
        var edit = new FooterLink("在 settings.json 中編輯");
        edit.LinkClicked += (_, _) => Settings.EditFile();
        note.Font = Theme.Body;
        note.ForeColor = p.Text2;
        note.Text = Startup.IsAdmin ? "本次以系統管理員執行，變更不會儲存" : "名稱不分大小寫 · 變更立即生效";
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = p.GroupedBack };
        links.Controls.Add(restore);
        links.Controls.Add(edit);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(8, 14, 0, 0), BackColor = p.GroupedBack };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(links);
        footer.Controls.Add(note);

        layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 22, 24, 14), BackColor = p.GroupedBack };
        layout.SuspendLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));  // header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // search + filter
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // suggestion (shown when there is one)
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));  // caption
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // list
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // footer
        // Fixed rows: auto-placement skips hidden controls, which would move everything up while the suggestion is hidden.
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(tools, 0, 1);
        layout.Controls.Add(suggestion, 0, 2);
        layout.Controls.Add(caption, 0, 3);
        layout.Controls.Add(new ListFrame(list) { Back = q => q.GroupedCard, Border = false }, 0, 4);
        layout.Controls.Add(footer, 0, 5);
        Controls.Add(layout);
        layout.ResumeLayout(false);
        suggestion.Visible = false;

        settings.ExclusionsChanged += OnExclusionsChanged;
        FormClosed += (_, _) => { settings.ExclusionsChanged -= OnExclusionsChanged; addForm?.Close(); };
        Activated += (_, _) => settings.ReloadExclusionsIfChanged();
        RefreshView();
        ResumeLayout(false);
        PerformLayout();
    }

    void OnExclusionsChanged() => RefreshView();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { search.Input.Focus(); search.Input.SelectAll(); return true; }
        if (keyData == (Keys.Control | Keys.N)) { ShowAdd(); return true; }
        if (keyData == Keys.Escape && search.Input.Focused && search.Input.TextLength > 0) { search.Input.Clear(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    IReadOnlyList<ProcessNapper.AppView> Running => Demo.On ? Demo.Apps : napper.Running;

    /// <summary>Rebuilds what the window shows: called on every change and by the tray's 2-second timer.</summary>
    public void RefreshView()
    {
        var running = Running.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var all = settings.ExcludedNames.Select(exe =>
        {
            running.TryGetValue(exe, out var app);
            bool on = running.ContainsKey(exe);
            var (name, kind) = KnownApps.Describe(exe, app.Path);
            return on
                ? new AppRow(exe, name, kind, $"{(app.Napped ? "休眠中" : "執行中")} · {Format.Bytes(app.WorkingSet)}", app.Napped ? q => q.Indigo : q => q.Green)
                : new AppRow(exe, name, kind, "未執行", q => q.Gray);
        }).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Exe).ToList();

        int runningCount = all.Count(r => running.ContainsKey(r.Exe));
        filter.Items = [$"全部 {all.Count}", $"執行中 {runningCount}", $"未執行 {all.Count - runningCount}"];

        var q = search.Input.Text.Trim();
        var rows = all.Where(r => filter.Selected switch { 1 => running.ContainsKey(r.Exe), 2 => !running.ContainsKey(r.Exe), _ => true })
                      .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Exe.Contains(q, StringComparison.OrdinalIgnoreCase))
                      .ToList();
        caption.Start = rows.Count == all.Count ? $"你的排除清單 · {all.Count} 個" : $"你的排除清單 · 顯示 {rows.Count} / {all.Count} 個";
        caption.Invalidate();
        list.EmptyText = all.Count == 0 ? "清單是空的。按「新增程式」加入在背景也要保持全速的程式。"
                       : q.Length > 0 ? $"沒有符合「{q}」的程式。" : "這個分類目前沒有程式。";
        list.SetRows(rows);

        var s = Demo.On ? Demo.Suggestion : napper.Suggestion;
        if (s is var (exe, _) && (settings.ExcludedNames.Contains(exe) || settings.DismissedSuggestions.Contains(exe, StringComparer.OrdinalIgnoreCase)))
            s = null;
        ShowSuggestion(s, running);
        addForm?.RefreshView();
    }

    void ShowSuggestion((string Name, int Count)? s, Dictionary<string, ProcessNapper.AppView> running)
    {
        shownSuggestion = s?.Name;
        if (s is var (exe, count))
        {
            running.TryGetValue(exe, out var app);
            var (name, _) = KnownApps.Describe(exe, app.Path);
            suggestion.Heading = $"建議加入：{name}（{exe}）";
            suggestion.Reason = $"今天休眠時有 {count} 次因背景工作被喚醒（CPU 超過 {napper.WakeCpuPercent:0.#}%），可能在背景播放或視訊。";
            suggestion.Invalidate();
        }
        bool visible = s != null;
        if (suggestion.Visible == visible) return;
        layout.SuspendLayout();
        suggestion.Visible = visible;
        layout.RowStyles[2] = new RowStyle(SizeType.Absolute, visible ? 84 : 0);
        layout.ResumeLayout(true);
    }

    public Form ShowAdd()
    {
        if (addForm == null)
        {
            addForm = new AddAppForm(settings, () => Running, icon);
            addForm.FormClosed += (_, _) => addForm = null;
            addForm.Show(this);
        }
        addForm.Activate();
        return addForm;
    }

    void RestoreDefaults()
    {
        var defaults = Settings.DefaultExclusions.Select(Settings.NormalizeExe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = settings.ExcludedNames.Count(x => !defaults.Contains(x));
        int removed = defaults.Count(x => !settings.ExcludedNames.Contains(x));
        if (added == 0 && removed == 0) return;
        var text = $"要把排除清單還原成預設的 {defaults.Count} 個程式嗎？"
                   + (added > 0 ? $"\n你加入的 {added} 個程式會被移除，之後背景閒置時可能被休眠。" : "")
                   + (removed > 0 ? $"\n你移除的 {removed} 個預設程式會加回來。" : "");
        if (MessageBox.Show(this, text, "還原預設清單", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;
        ActionLog.Add("排除清單已還原為預設");
        settings.SetExclusions(Settings.DefaultExclusions);
    }
}
