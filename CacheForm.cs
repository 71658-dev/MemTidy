namespace MemTidy;

/// <summary>Which files are sitting in the cache (standby list), largest first, in the design's cache window. Needs admin.</summary>
sealed class CacheForm : Form
{
    readonly SummaryCard summary = new() { ReserveRight = 130 };
    readonly ZebraList list = new(("快取大小", 200, Cell.Bar), ("檔案", 560, Cell.Mono));
    readonly MacButton refresh = new("重新整理") { Behind = p => p.Fill };
    readonly Label footer = new() { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };

    public CacheForm(Icon icon)
    {
        Theme.Refresh();
        var p = Theme.Current;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = "快取內容";
        Icon = icon;
        Font = Theme.Body;
        BackColor = p.Window;
        ForeColor = p.Text;
        ClientSize = new Size(900, 660);
        MinimumSize = new Size(600, 420);
        StartPosition = FormStartPosition.CenterScreen;

        footer.Font = Theme.Small;
        footer.ForeColor = p.Text2;
        list.DoubleClick += (_, _) => RevealSelected();
        refresh.Click += async (_, _) => await LoadAsync();

        // The refresh button sits on the summary card, top right.
        summary.Controls.Add(refresh);
        summary.Resize += (_, _) =>
        {
            float s = summary.DeviceDpi / 96f;
            refresh.Bounds = new Rectangle(summary.Width - (int)(16 * s) - (int)(110 * s), (int)(16 * s), (int)(110 * s), (int)(32 * s));
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 20, 24, 16), BackColor = p.Window };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(summary);
        layout.Controls.Add(new SectionHeader("占用最多的檔案") { Note = "按兩下可在檔案總管中顯示", Margin = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(new ListFrame(list));
        layout.Controls.Add(footer);
        Controls.Add(layout);

        Shown += async (_, _) => await LoadAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    async Task LoadAsync()
    {
        refresh.Enabled = false;
        summary.Big = "…";
        summary.Line = "讀取中";
        summary.Paragraph = "";
        summary.Invalidate();
        var r = await Task.Run(() => StandbyFiles.Query());
        if (IsDisposed) return;
        refresh.Enabled = true;

        if (r.StandbyBytes == 0 && r.Error != null)
        {
            summary.Big = "—";
            summary.Line = $"無法讀取：{r.Error}";
        }
        else
        {
            summary.Big = Format.Bytes(r.StandbyBytes);
            summary.Line = r.NamedBytes >= r.FileBytes
                ? $"快取共計 · 其中檔案 {Format.Bytes(r.FileBytes)}，已全部對應到檔名"
                : $"快取共計 · 其中檔案 {Format.Bytes(r.FileBytes)}，已對應檔名 {Format.Bytes(r.NamedBytes)}";
        }
        summary.Paragraph = "這些是最近讀過的檔案，留在記憶體裡讓下次開啟更快。有程式需要記憶體時，系統會自動讓出這些空間，不需要清除。"
                            + (r.Error != null && r.StandbyBytes > 0 ? $"\n注意：{r.Error}" : "");
        summary.Invalidate();

        long max = r.Files.Count > 0 ? Math.Max(1, r.Files.Max(f => f.Bytes)) : 1;
        list.SetRows(r.Files.Select(f => new ListRow([Format.Bytes(f.Bytes), f.Path], Bar: (float)f.Bytes / max)));
        footer.Text = r.StandbyBytes == 0 ? "" : $"列出最大的 {r.Files.Count} 個 · 以系統管理員身分讀取 · 耗時 {r.Elapsed.TotalSeconds:0.0} 秒 · 使用與 RAMMap 相同的 Windows 內部介面";
    }

    void RevealSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var path = list.SelectedItems[0].SubItems[1].Text;
        if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
