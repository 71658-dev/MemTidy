namespace MemTidy;

/// <summary>
/// "Add program" for the exclusions window: pick one of this user's running programs (napping ones are marked),
/// type an exe name, or browse to an exe file. Stays open so several can be added in a row.
/// </summary>
sealed class AddAppForm : Form
{
    readonly Settings settings;
    readonly Func<IReadOnlyList<ProcessNapper.AppView>> running;
    readonly SearchBox search = new("輸入程式名稱，或 .exe 名稱後按 Enter");
    readonly Caption caption = new() { Start = "正在執行的程式", End = "依記憶體排序" };
    readonly AppList list = new(adds: true);

    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public AddAppForm(Settings settings, Func<IReadOnlyList<ProcessNapper.AppView>> running, Icon icon)
    {
        this.settings = settings;
        this.running = running;
        SuspendLayout();
        var p = Theme.Current;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = "新增程式 — 記憶體整理";
        Icon = icon;
        Font = Theme.Body;
        BackColor = p.GroupedBack;
        ForeColor = p.Text;
        ClientSize = new Size(560, 620);
        MinimumSize = new Size(460, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var header = new PageHeader { Title = "新增程式", Subtitle = "選一個正在執行的程式加入排除清單，或輸入程式的 .exe 名稱。" };
        list.Act += row => Add(row.Exe);
        search.Input.TextChanged += (_, _) => RefreshView();
        search.Input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && list.Rows.Count > 0) { Add(list.Rows[0].Exe); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.Items[0].Selected = list.Items[0].Focused = true; e.Handled = true; }
        };

        var browse = new FooterLink("選擇 .exe 檔…");
        browse.LinkClicked += (_, _) => Browse();
        var done = new MacButton("完成") { Behind = q => q.GroupedBack, Width = 96, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        done.Click += (_, _) => Close();
        CancelButton = done;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(8, 14, 0, 0), BackColor = p.GroupedBack };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browse.Anchor = AnchorStyles.Left;
        footer.Controls.Add(browse);
        footer.Controls.Add(done);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 22, 24, 16), BackColor = p.GroupedBack };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header);
        layout.Controls.Add(search);
        layout.Controls.Add(caption);
        layout.Controls.Add(new ListFrame(list) { Back = q => q.GroupedCard, Border = false });
        layout.Controls.Add(footer);
        Controls.Add(layout);

        Shown += (_, _) => search.Input.Focus();
        RefreshView();
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    /// <summary>The name typed in the search box as an exe name, or null when it can't be one.</summary>
    string? TypedExe()
    {
        var t = search.Input.Text.Trim();
        if (t.Length == 0 || t.Length > 260 || t.IndexOfAny(Invalid) >= 0) return null;
        return Settings.NormalizeExe(t);
    }

    public void RefreshView()
    {
        var q = search.Input.Text.Trim();
        var rows = running()
            .Where(a => !settings.ExcludedNames.Contains(a.Name) && !ProcessNapper.IsBuiltIn(a.Name))
            .Select(a =>
            {
                var (name, kind) = KnownApps.Describe(a.Name, a.Path);
                return a.Napped
                    ? new AppRow(a.Name, name, kind, $"休眠中 · {Format.Bytes(a.WorkingSet)}", c => c.Indigo, StatusTinted: true)
                    : new AppRow(a.Name, name, kind, $"執行中 · {Format.Bytes(a.WorkingSet)}", c => c.Green);
            })
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Exe.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A typed name that matches no running program can still be added (for programs that aren't open now).
        var typed = TypedExe();
        bool already = typed != null && settings.ExcludedNames.Contains(typed);
        if (typed != null && !already && !rows.Any(r => r.Exe.Equals(typed, StringComparison.OrdinalIgnoreCase)))
        {
            var (name, kind) = KnownApps.Describe(typed, null);
            // After the running matches, so Enter on "chr" adds chrome.exe rather than "chr.exe".
            rows.Add(new AppRow(typed, name, kind, rows.Count == 0 ? "按 Enter 加入這個名稱" : "加入這個名稱", c => c.Accent));
        }

        caption.Start = q.Length == 0 ? "正在執行的程式" : "搜尋結果";
        caption.Invalidate();
        list.EmptyText = already ? $"{typed} 已經在排除清單裡。"
                       : q.Length > 0 ? $"沒有符合「{q}」的程式。" : "目前沒有其他正在執行的程式。";
        list.SetRows(rows);
    }

    void Add(string exe)
    {
        settings.Exclude(exe); // the exclusions window refreshes both windows through ExclusionsChanged
        if (search.Input.TextLength > 0) search.Input.Clear();
        else RefreshView();
    }

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "選擇要加入排除清單的程式",
            Filter = "程式 (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Add(Path.GetFileName(dlg.FileName));
    }
}
