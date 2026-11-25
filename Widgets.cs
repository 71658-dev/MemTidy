using System.Drawing.Drawing2D;

namespace MemTidy;

/// <summary>Painting helpers shared by the controls below.</summary>
static class Gfx
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        using var path = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, path);
    }

    public static void Dot(Graphics g, Color c, float cx, float cy, float diameter)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, cx - diameter / 2, cy - diameter / 2, diameter, diameter);
    }

    const TextFormatFlags Base = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter) =>
        TextRenderer.DrawText(g, s, f, r, c, flags | Base);

    public static Size Measure(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, Base | TextFormatFlags.SingleLine);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>A double-buffered, fully self-painted control that knows the palette and the DPI scale.</summary>
abstract class Painted : Control
{
    protected Painted()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty;
    }

    protected static Palette P => Theme.Current;
    protected float S(float logical) => logical * DeviceDpi / 96f;
    protected int Si(float logical) => (int)Math.Round(S(logical));

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Draw(e.Graphics);
    }

    protected virtual Color Surface => P.Window;
    protected abstract void Draw(Graphics g);
}

/// <summary>Bold section title, an optional grey note after it, and an optional colour legend on the right.</summary>
sealed class SectionHeader : Painted
{
    string title;
    public string? Note { get; set; }
    public (string Label, Func<Palette, Color> Color)[] Legend { get; init; } = [];

    public SectionHeader(string title) { this.title = title; Height = 26; Dock = DockStyle.Fill; }

    public string Title { get => title; set { if (title != value) { title = value; Invalidate(); } } }

    protected override void Draw(Graphics g)
    {
        var r = ClientRectangle;
        var size = Gfx.Measure(title, Theme.Heading);
        Gfx.Text(g, title, Theme.Heading, P.Text, new Rectangle(0, 0, size.Width + 2, r.Height));
        if (Note != null)
            Gfx.Text(g, Note, Theme.Small, P.Text2, new Rectangle(size.Width + Si(10), 0, r.Width, r.Height));

        int x = r.Width;
        foreach (var (label, color) in Legend.Reverse())
        {
            var w = Gfx.Measure(label, Theme.Small).Width;
            x -= w;
            Gfx.Text(g, label, Theme.Small, P.Text2, new Rectangle(x, 0, w + 2, r.Height));
            x -= Si(13);
            Gfx.Dot(g, color(P), x + S(4), r.Height / 2f, S(8));
            x -= Si(12);
        }
    }
}

/// <summary>Memory used over the last five minutes, filled and stroked in the colour of the pressure at each moment.</summary>
sealed class GraphPanel : Painted
{
    readonly PressureMonitor pressure;

    public GraphPanel(PressureMonitor pressure) { this.pressure = pressure; Dock = DockStyle.Fill; }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        if (r.Width < 20 || r.Height < 20) return;
        Gfx.FillRound(g, P.Fill, r, S(10));

        using (var grid = new Pen(P.Separator, 1))
            foreach (var pct in new[] { 25, 50, 75 })
            {
                float y = r.Bottom - r.Height * pct / 100f;
                g.DrawLine(grid, r.Left, y, r.Right, y);
            }

        var samples = pressure.History.ToArray();
        if (samples.Length > 1)
        {
            int slots = PressureMonitor.HistorySeconds / PressureMonitor.SampleSeconds;
            float step = r.Width / (slots - 1);
            float x0 = r.Right - step * (samples.Length - 1);
            PointF At(int i) => new(x0 + step * i, r.Bottom - r.Height * (float)Math.Clamp(samples[i].UsedPct, 0, 100) / 100f);

            using var clip = Gfx.Round(r, S(10));
            g.SetClip(clip);
            // One filled run per stretch of the same pressure level; each segment takes the level of its later sample.
            int start = 0, n = samples.Length;
            while (start < n - 1)
            {
                var level = samples[start + 1].Level;
                int end = start + 1;
                while (end + 1 < n && samples[end + 1].Level == level) end++;
                var color = P.Of(level);
                var line = Enumerable.Range(start, end - start + 1).Select(At).ToArray();
                var area = line.Prepend(new PointF(line[0].X, r.Bottom)).Append(new PointF(line[^1].X, r.Bottom)).ToArray();
                using (var fill = new SolidBrush(Color.FromArgb(70, color))) g.FillPolygon(fill, area);
                using (var pen = new Pen(color, S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line);
                start = end;
            }
            g.ResetClip();
        }

        int pad = Si(8), h = Si(16);
        Gfx.Text(g, "100%", Theme.Small, P.Text2, new Rectangle(0, pad / 2, (int)r.Width - pad, h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        Gfx.Text(g, "5 分鐘前", Theme.Small, P.Text2, new Rectangle(pad, (int)r.Bottom - h - pad / 2, (int)r.Width, h));
        Gfx.Text(g, "現在", Theme.Small, P.Text2, new Rectangle(0, (int)r.Bottom - h - pad / 2, (int)r.Width - pad, h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>One rounded stacked bar of RAM (in use / cache / empty) with a legend underneath.</summary>
sealed class BreakdownBar : Painted
{
    long total;
    (long Bytes, string Label, Func<Palette, Color> Color)[] parts = [];

    public BreakdownBar() { Dock = DockStyle.Fill; }

    public void Set(long total, (long, string, Func<Palette, Color>)[] parts)
    {
        this.total = total;
        this.parts = parts;
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        if (total <= 0 || Width < 40) return;
        var bar = new RectangleF(0, 0, Width - 1, S(14));
        using (var clip = Gfx.Round(bar, S(7)))
        {
            using (var track = new SolidBrush(P.Track)) g.FillPath(track, clip);
            g.SetClip(clip);
            float x = 0;
            foreach (var (bytes, _, color) in parts)
            {
                float w = Math.Max(bytes > 0 ? S(3) : 0, bar.Width * Math.Clamp(bytes, 0, total) / total);
                using var b = new SolidBrush(color(P));
                g.FillRectangle(b, x, 0, w, bar.Height);
                x += w;
            }
            g.ResetClip();
        }

        int lx = 0, ly = (int)bar.Bottom + Si(10), lh = Si(18);
        foreach (var (bytes, label, color) in parts)
        {
            Gfx.FillRound(g, color(P), new RectangleF(lx, ly + (lh - S(10)) / 2, S(10), S(10)), S(3));
            lx += Si(16);
            var lw = Gfx.Measure(label, Theme.Body).Width;
            Gfx.Text(g, label, Theme.Body, P.Text, new Rectangle(lx, ly, lw + 2, lh));
            lx += lw + Si(4);
            var value = Format.Bytes(bytes);
            var vw = Gfx.Measure(value, Theme.Heading).Width;
            Gfx.Text(g, value, Theme.Heading, P.Text, new Rectangle(lx, ly, vw + 2, lh));
            lx += vw + Si(22);
        }
    }
}

/// <summary>macOS-style push button: filled accent (primary) or bordered neutral.</summary>
sealed class MacButton : Button
{
    bool hover, pressed;
    public bool Primary { get; init; }
    /// <summary>What is painted behind the rounded corners (the card the button sits on).</summary>
    public Func<Palette, Color> Behind { get; init; } = p => p.Window;

    public MacButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = Theme.Body;
        Height = 32;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { pressed = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Behind(p));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color face, text;
        if (Primary)
        {
            face = pressed ? Gfx.Mix(p.Accent, Color.Black, 0.18f) : hover ? Gfx.Mix(p.Accent, Color.White, 0.08f) : p.Accent;
            text = p.OnAccent;
            Gfx.FillRound(g, face, r, 7 * s);
        }
        else
        {
            face = pressed ? p.ButtonFacePressed : hover ? Gfx.Mix(p.ButtonFace, p.ButtonFacePressed, 0.5f) : p.ButtonFace;
            text = p.Text;
            Gfx.FillRound(g, face, r, 7 * s);
            Gfx.StrokeRound(g, p.ButtonBorder, r, 7 * s);
        }
        if (!Enabled) text = Color.FromArgb(128, text);
        if (Focused && ShowFocusCues)
            Gfx.StrokeRound(g, Color.FromArgb(150, p.Accent), RectangleF.Inflate(r, -1.5f * s, -1.5f * s), 6 * s, 2 * s);
        Gfx.Text(g, Text, Font, text, ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>The grouped status block: pressure dot and title, a grid of figures, and its buttons on the right.</summary>
sealed class StatusCard : Painted
{
    readonly List<MacButton> buttons = [];
    public Func<Palette, Color> DotColor { get; set; } = p => p.Green;
    public string Heading { get; set; } = "";
    public string? Note { get; set; }
    public (string Label, string Value)[] Facts { get; set; } = [];

    public StatusCard() { Dock = DockStyle.Fill; }

    public void AddButton(MacButton b)
    {
        buttons.Add(b);
        Controls.Add(b);
    }

    const int ButtonWidth = 190;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int y = Si(16);
        foreach (var b in buttons)
        {
            b.Bounds = new Rectangle(Width - Si(16) - Si(ButtonWidth), y, Si(ButtonWidth), Si(32));
            y += Si(40);
        }
    }

    protected override void Draw(Graphics g)
    {
        Gfx.FillRound(g, P.Fill, new RectangleF(0, 0, Width - 1, Height - 1), S(10));
        int left = Si(16), right = Width - Si(16) - (buttons.Count > 0 ? Si(ButtonWidth) + Si(20) : 0);
        int y = Si(14), th = Si(26);
        Gfx.Dot(g, DotColor(P), left + S(6), y + th / 2f, S(12));
        var tw = Gfx.Measure(Heading, Theme.Title).Width;
        Gfx.Text(g, Heading, Theme.Title, P.Text, new Rectangle(left + Si(20), y, tw + 2, th));
        if (Note != null)
            Gfx.Text(g, Note, Theme.Small, P.Text2, new Rectangle(left + Si(20) + tw + Si(10), y, Math.Max(0, right - left - tw), th), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        y += th + Si(12);
        int cols = 3, colW = Math.Max(1, (right - left) / cols), lh = Si(16), vh = Si(20);
        for (int i = 0; i < Facts.Length; i++)
        {
            int cx = left + (i % cols) * colW, cy = y + (i / cols) * (lh + vh + Si(8));
            Gfx.Text(g, Facts[i].Label, Theme.Small, P.Text2, new Rectangle(cx, cy, colW - Si(8), lh), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Facts[i].Value, Theme.Heading, P.Text, new Rectangle(cx, cy + lh, colW - Si(8), vh), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>A big figure, a grey line beside it, and a wrapped explanation, on a grouped fill (the cache summary).</summary>
sealed class SummaryCard : Painted
{
    public string Big { get; set; } = "";
    public string Line { get; set; } = "";
    public string Paragraph { get; set; } = "";
    /// <summary>Logical px kept free on the right for a control placed on the card (e.g. a button).</summary>
    public int ReserveRight { get; set; }

    public SummaryCard() { Dock = DockStyle.Fill; }

    protected override void Draw(Graphics g)
    {
        Gfx.FillRound(g, P.Fill, new RectangleF(0, 0, Width - 1, Height - 1), S(10));
        int left = Si(16), y = Si(14), h = Si(32);
        var bw = Gfx.Measure(Big, Theme.Huge).Width;
        Gfx.Text(g, Big, Theme.Huge, P.Text, new Rectangle(left, y, bw + 2, h));
        Gfx.Text(g, Line, Theme.Body, P.Text2, new Rectangle(left + bw + Si(14), y + Si(4), Math.Max(0, Width - left - bw - Si(30) - Si(ReserveRight)), h), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Gfx.Text(g, Paragraph, Theme.Small, P.Text2, new Rectangle(left, y + h + Si(8), Math.Min(Width - 2 * left, Si(680)), Height - y - h - Si(16)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}

enum Cell { Text, Muted, Strong, Dot, Bar, Mono }

sealed record ListRow(string[] Cells, Color? Dot = null, float Bar = 0, object? Tag = null);

/// <summary>A details list with the design's look: zebra rows, light header, accent selection, special cells.</summary>
sealed class ZebraList : ListView
{
    readonly Cell[] styles;
    readonly int[] logicalWidths;

    public ZebraList(params (string Title, int Width, Cell Style)[] cols)
    {
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BorderStyle = BorderStyle.None;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Font = Theme.Body;
        styles = cols.Select(c => c.Style).ToArray();
        logicalWidths = cols.Select(c => c.Width).ToArray();
        foreach (var (title, width, _) in cols) Columns.Add(title, width);
    }

    static Palette P => Theme.Current;
    float S(float v) => v * DeviceDpi / 96f;
    int Si(float v) => (int)Math.Round(S(v));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyControlTheme(this);
        BackColor = P.Window;
        ForeColor = P.Text;
        SmallImageList = new ImageList { ImageSize = new Size(1, Si(26)) }; // sets the row height
        for (int i = 0; i < Columns.Count - 1; i++) Columns[i].Width = Si(logicalWidths[i]); // widths are given at 96 dpi
        StretchLastColumn();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); StretchLastColumn(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); StretchLastColumn(); }

    void StretchLastColumn()
    {
        if (Columns.Count == 0 || !IsHandleCreated) return;
        int others = 0;
        for (int i = 0; i < Columns.Count - 1; i++) others += Columns[i].Width;
        // The vertical scrollbar may not be shown yet when rows were just added: leave room for it if the rows will need it.
        // Row height is the one set through SmallImageList; never ask the control for item bounds here: during a resize
        // or while rows are being replaced it can refuse (ArgumentOutOfRangeException from GetItemRect).
        int rowHeight = SmallImageList?.ImageSize.Height ?? Si(26);
        bool needsScroll = Items.Count * rowHeight > ClientSize.Height - HeaderHeight();
        bool hasScroll = (GetWindowLong(Handle, -16 /* GWL_STYLE */) & 0x00200000 /* WS_VSCROLL */) != 0;
        int room = needsScroll && !hasScroll ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;
        Columns[^1].Width = Math.Max(Si(80), ClientSize.Width - others - room);
    }

    public void SetRows(IEnumerable<ListRow> rows)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var r in rows) Items.Add(new ListViewItem(r.Cells) { Tag = r });
        EndUpdate();
        StretchLastColumn(); // a vertical scrollbar may have appeared
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(P.Window)) e.Graphics.FillRectangle(bg, e.Bounds);
        var r = Rectangle.Inflate(e.Bounds, -Si(10), 0);
        Gfx.Text(e.Graphics, e.Header?.Text ?? "", Theme.UI(8.25f, FontStyle.Bold), P.Text2, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var line = new Pen(P.Separator);
        e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => e.DrawDefault = false;

    /// <summary>
    /// The themed list control draws column lines in the empty area under the rows; paint that area flat
    /// in the window colour once the control has painted.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // Columns always fit the width (long text is shortened), so never show a horizontal scrollbar:
        // the list control would otherwise add one for a pixel or two of rounding.
        if (m.Msg == 0x0083 /* WM_NCCALCSIZE */ && IsHandleCreated)
        {
            int style = GetWindowLong(Handle, -16 /* GWL_STYLE */);
            if ((style & 0x00100000 /* WS_HSCROLL */) != 0) SetWindowLong(Handle, -16, style & ~0x00100000);
        }
        base.WndProc(ref m);
        if (m.Msg != 0x000F /* WM_PAINT */ || !IsHandleCreated) return;
        // Rows taller than the list leave no empty area: skip the item-rect query and the extra Graphics on every paint.
        if (Items.Count * (SmallImageList?.ImageSize.Height ?? Si(26)) >= ClientSize.Height) return;
        int top;
        try { top = Items.Count > 0 ? Items[^1].Bounds.Bottom : HeaderHeight(); }
        catch (ArgumentOutOfRangeException) { return; } // rows are being replaced; the next paint covers it
        if (top >= ClientSize.Height) return;
        using var g = CreateGraphics();
        using var b = new SolidBrush(P.Window);
        g.FillRectangle(b, 0, Math.Max(top, HeaderHeight()), ClientSize.Width, ClientSize.Height - top);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    struct RECT { public int Left, Top, Right, Bottom; }

    int HeaderHeight()
    {
        var header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        return header != IntPtr.Zero && GetWindowRect(header, out var r) ? r.Bottom - r.Top : 0;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var row = e.Item.Tag as ListRow;
        bool selected = e.Item.Selected;
        var bg = selected ? P.Accent : e.ItemIndex % 2 == 1 ? P.Zebra : P.Window;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);

        var style = e.ColumnIndex < styles.Length ? styles[e.ColumnIndex] : Cell.Text;
        var text = e.SubItem.Text;
        var r = Rectangle.Inflate(e.Bounds, -Si(10), 0);
        Color fg = selected ? P.OnAccent : style == Cell.Muted ? P.Text2 : P.Text;
        const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        switch (style)
        {
            case Cell.Dot:
                Gfx.Dot(g, selected ? P.OnAccent : row?.Dot ?? P.Gray, r.Left + S(4), r.Top + r.Height / 2f, S(8));
                Gfx.Text(g, text, Theme.Heading, fg, new Rectangle(r.Left + Si(14), r.Top, r.Width - Si(14), r.Height), line);
                break;
            case Cell.Bar:
                int tw = Si(64);
                Gfx.Text(g, text, Theme.Heading, fg, new Rectangle(r.Left, r.Top, tw, r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                var track = new RectangleF(r.Left + tw + S(10), r.Top + (r.Height - S(6)) / 2, Math.Max(0, r.Width - tw - S(10)), S(6));
                if (track.Width > 4)
                {
                    Gfx.FillRound(g, selected ? Color.FromArgb(90, P.OnAccent) : P.Track, track, S(3));
                    var fill = track with { Width = Math.Max(S(4), track.Width * Math.Clamp(row?.Bar ?? 0, 0, 1)) };
                    Gfx.FillRound(g, selected ? P.OnAccent : P.Green, fill, S(3));
                }
                break;
            case Cell.Mono:
                Gfx.Text(g, text, Theme.Mono, fg, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
                break;
            case Cell.Strong:
                Gfx.Text(g, text, Theme.Heading, fg, r, line);
                break;
            default:
                Gfx.Text(g, text, Theme.Body, fg, r, line);
                break;
        }
    }
}

/// <summary>Rounded hairline frame around a list, like the design's inset tables.</summary>
sealed class ListFrame : Panel
{
    public ListFrame(Control inner)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Controls.Add(inner);
    }

    float S(float v) => v * DeviceDpi / 96f;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        int pad = (int)Math.Round(S(4));
        Padding = new Padding(pad);
        base.OnLayout(levent);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        using var path = Gfx.Round(new RectangleF(0, 0, Width, Height), S(10));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        e.Graphics.Clear(p.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Gfx.StrokeRound(e.Graphics, p.Separator, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(10));
    }
}

/// <summary>Tray menu in the design's style: flat surface, rounded accent-blue highlight, white text on it.</summary>
sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    static Palette P => Theme.Current;
    static Color Surface => P.Dark ? Color.FromArgb(43, 43, 45) : Color.FromArgb(248, 248, 248);

    public MenuRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Surface);
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { } // Windows 11 draws the rounded border
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        float s = e.ToolStrip?.DeviceDpi / 96f ?? 1;
        e.Graphics.ResetClip(); // the default clip leaves out the check-mark column
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(4 * s, 1, e.Item.Width - 8 * s, e.Item.Height - 2);
        Gfx.FillRound(e.Graphics, P.Accent, r, 5 * s);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? P.Text2 : e.Item.Selected ? P.OnAccent : P.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var color = e.Item.Selected ? P.OnAccent : P.Text;
        Gfx.Text(e.Graphics, "✓", Theme.Heading, color, e.ImageRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = e.ToolStrip?.DeviceDpi / 96f ?? 1;
        int y = e.Item.Height / 2;
        using var pen = new Pen(P.Separator);
        e.Graphics.DrawLine(pen, 10 * s, y, e.Item.Width - 10 * s, y);
    }
}
