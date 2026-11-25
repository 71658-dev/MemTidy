using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MemTidy;

// Controls for the exclusions window's grouped look: grey page, light cards (reversed in dark mode).

/// <summary>Glyphs of Windows' icon font (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
static class Glyphs
{
    public const string Search = "", Add = "", Lightbulb = "";
    public const string Globe = "", Phone = "", Music = "", Video = "", App = "";

    public static string Of(AppKind k) => k switch
    {
        AppKind.Browser => Globe, AppKind.Call => Phone, AppKind.Music => Music, AppKind.Recording => Video, _ => App,
    };

    public static Color ColorOf(AppKind k, Palette p) => k switch
    {
        AppKind.Browser => p.Accent, AppKind.Call => p.Green, AppKind.Music => p.Red, AppKind.Recording => p.Indigo,
        _ => Color.FromArgb(142, 142, 147), // the light palette's Gray is too pale behind a white glyph
    };

    /// <summary>A white glyph on a coloured rounded square (the app tile in the rows and the suggestion card).</summary>
    public static void Tile(Graphics g, string glyph, Color color, RectangleF r, float scale)
    {
        Gfx.FillRound(g, color, r, 8 * scale);
        Gfx.Text(g, glyph, Theme.Icons(13f), Color.White, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>The round ⊖ / ⊕ action of a row.</summary>
    public static void CircleAction(Graphics g, Color c, PointF center, float scale, bool plus, bool hover)
    {
        float rad = 9 * scale, w = 1.6f * scale;
        if (hover) Gfx.Dot(g, Color.FromArgb(40, c), center.X, center.Y, rad * 2 + 8 * scale);
        using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, center.X - rad, center.Y - rad, rad * 2, rad * 2);
        float arm = 4.5f * scale;
        g.DrawLine(pen, center.X - arm, center.Y, center.X + arm, center.Y);
        if (plus) g.DrawLine(pen, center.X, center.Y - arm, center.X, center.Y + arm);
    }
}

/// <summary>Large page title, a grey explanation under it, and room on the right for a control (the add button).</summary>
sealed class PageHeader : Painted
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public int ReserveRight { get; set; }

    public PageHeader() { Dock = DockStyle.Fill; }
    protected override Color Surface => P.GroupedBack;

    protected override void Draw(Graphics g)
    {
        var font = Theme.UI(15f, FontStyle.Bold);
        int th = Gfx.Measure(Title, font).Height;
        Gfx.Text(g, Title, font, P.Text, new Rectangle(0, Si(4), Width - Si(ReserveRight), th));
        Gfx.Text(g, Subtitle, Theme.Body, P.Text2, new Rectangle(0, Si(4) + th + Si(6), Width, Height - th - Si(10)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}

/// <summary>Rounded grey search field with a magnifier; the text box inside is borderless.</summary>
sealed class SearchBox : Panel
{
    public readonly TextBox Input = new() { BorderStyle = BorderStyle.None };

    public SearchBox(string placeholder)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Input.PlaceholderText = placeholder;
        Input.Font = Theme.UI(10.5f);
        Controls.Add(Input);
        Click += (_, _) => Input.Focus();
        Input.GotFocus += (_, _) => Invalidate();
        Input.LostFocus += (_, _) => Invalidate();
    }

    float S(float v) => v * DeviceDpi / 96f;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = (int)S(40);
        Input.SetBounds(left, (Height - Input.PreferredHeight) / 2, Math.Max(10, Width - left - (int)S(14)), Input.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.GroupedBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Gfx.FillRound(g, p.Track, r, S(10));
        if (Input.Focused) Gfx.StrokeRound(g, Color.FromArgb(160, p.Accent), RectangleF.Inflate(r, -1, -1), S(9), S(2));
        Input.BackColor = p.Track;
        Input.ForeColor = p.Text;
        Gfx.Text(g, Glyphs.Search, Theme.Icons(11f), p.Text2, new Rectangle((int)S(14), 0, (int)S(20), Height));
    }
}

/// <summary>macOS segmented control: a grey track with the chosen segment raised on a card-coloured pill.</summary>
sealed class Segmented : Painted
{
    string[] items = [];
    int selected, hot = -1;
    public event Action? SelectedChanged;

    public Segmented()
    {
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    protected override Color Surface => P.GroupedBack;

    public string[] Items { get => items; set { items = value; Invalidate(); } }

    public int Selected
    {
        get => selected;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, items.Length - 1));
            if (value == selected) return;
            selected = value;
            Invalidate();
            SelectedChanged?.Invoke();
        }
    }

    RectangleF Segment(int i)
    {
        float pad = S(4), w = (Width - 2 * pad) / Math.Max(1, items.Length);
        return new RectangleF(pad + i * w, pad, w, Height - 2 * pad - 1);
    }

    int HitTest(Point pt)
    {
        for (int i = 0; i < items.Length; i++) if (Segment(i).Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (HitTest(e.Location) is var i and >= 0) Selected = i; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var i = HitTest(e.Location); if (i != hot) { hot = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Selected--;
        if (e.KeyCode == Keys.Right) Selected++;
    }

    protected override void Draw(Graphics g)
    {
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        Gfx.FillRound(g, P.Track, track, track.Height / 2);
        for (int i = 0; i < items.Length; i++)
        {
            var r = Segment(i);
            if (i == selected)
            {
                // A soft shadow under the raised segment, then the segment itself.
                Gfx.FillRound(g, Color.FromArgb(P.Dark ? 60 : 22, 0, 0, 0), r with { Y = r.Y + S(1) }, r.Height / 2);
                Gfx.FillRound(g, P.Dark ? P.ButtonFacePressed : P.Window, r, r.Height / 2);
                if (Focused && ShowFocusCues) Gfx.StrokeRound(g, Color.FromArgb(160, P.Accent), r, r.Height / 2, S(2));
            }
            else if (i == hot) Gfx.FillRound(g, Color.FromArgb(P.Dark ? 24 : 14, P.Text), r, r.Height / 2);
            Gfx.Text(g, items[i], i == selected ? Theme.UI(10.5f, FontStyle.Bold) : Theme.UI(10.5f), P.Text, Rectangle.Round(r),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>"Suggested: X" card with a reason, a skip link and a soft add button.</summary>
sealed class SuggestionCard : Painted
{
    public string Heading { get; set; } = "";
    public string Reason { get; set; } = "";
    public readonly MacButton Add = new("加入排除") { Soft = true, Radius = 99, Behind = p => p.GroupedCard };
    public event Action? Skip;
    Rectangle skipRect;
    bool skipHot;

    public SuggestionCard()
    {
        Dock = DockStyle.Fill;
        Add.Font = Theme.UI(10.5f, FontStyle.Bold);
        Controls.Add(Add);
    }

    protected override Color Surface => P.GroupedBack;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int w = Si(112), h = Si(36);
        Add.Bounds = new Rectangle(Width - Si(16) - w, (Height - h) / 2, w, h);
        var skip = Gfx.Measure("略過", Theme.UI(10.5f, FontStyle.Bold));
        skipRect = new Rectangle(Add.Left - Si(22) - skip.Width, (Height - skip.Height) / 2 - Si(4), skip.Width + Si(8), skip.Height + Si(8));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hot = skipRect.Contains(e.Location);
        Cursor = hot ? Cursors.Hand : Cursors.Default;
        if (hot != skipHot) { skipHot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); skipHot = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (skipRect.Contains(e.Location)) Skip?.Invoke(); }

    protected override void Draw(Graphics g)
    {
        Gfx.FillRound(g, P.GroupedCard, new RectangleF(0, 0, Width - 1, Height - 1), S(12));
        float t = S(34);
        Glyphs.Tile(g, Glyphs.Lightbulb, P.Orange, new RectangleF(Si(16), (Height - t) / 2, t, t), DeviceDpi / 96f);
        int left = Si(16) + (int)t + Si(14), right = skipRect.Left - Si(12);
        var hf = Theme.UI(10.5f, FontStyle.Bold);
        int hh = Gfx.Measure(Heading, hf).Height, rh = Gfx.Measure(Reason, Theme.Body).Height, top = (Height - hh - rh - Si(4)) / 2;
        const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        Gfx.Text(g, Heading, hf, P.Text, new Rectangle(left, top, right - left, hh), line);
        Gfx.Text(g, Reason, Theme.Body, P.Text2, new Rectangle(left, top + hh + Si(4), right - left, rh), line);
        var link = skipHot ? Gfx.Mix(P.Accent, P.Text, 0.3f) : P.Accent;
        Gfx.Text(g, "略過", Theme.UI(10.5f, FontStyle.Bold), link, skipRect, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>One row of <see cref="AppList"/>.</summary>
sealed record AppRow(string Exe, string Name, AppKind Kind, string Status, Func<Palette, Color> StatusDot, bool StatusTinted = false);

/// <summary>
/// Two-line app rows on a card: coloured tile, name, "exe · kind", a status with a dot, and a round ⊖ (remove) or
/// ⊕ (add) action on the right. A list control underneath, so scrolling, the keyboard and screen readers just work.
/// </summary>
sealed class AppList : ListView
{
    readonly bool adds;
    int hotAction = -1;
    public event Action<AppRow>? Act;
    public string EmptyText { get; set; } = "";

    /// <param name="adds">⊕ add actions (green) instead of ⊖ remove actions (red).</param>
    public AppList(bool adds)
    {
        this.adds = adds;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HeaderStyle = ColumnHeaderStyle.None;
        BorderStyle = BorderStyle.None;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Font = Theme.UI(10.5f);
        Columns.Add("程式", 400);
    }

    static Palette P => Theme.Current;
    float S(float v) => v * DeviceDpi / 96f;
    int Si(float v) => (int)Math.Round(S(v));
    int RowHeight => SmallImageList?.ImageSize.Height ?? Si(56);

    public IReadOnlyList<AppRow> Rows { get; private set; } = [];

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyControlTheme(this);
        BackColor = P.GroupedCard;
        ForeColor = P.Text;
        SmallImageList = new ImageList { ImageSize = new Size(1, Si(56)) }; // sets the row height
        Stretch();
    }

    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); Stretch(); }

    void Stretch()
    {
        if (Columns.Count == 0 || !IsHandleCreated) return;
        bool needsScroll = Items.Count * RowHeight > ClientSize.Height;
        bool hasScroll = (GetWindowLong(Handle, -16) & 0x00200000 /* WS_VSCROLL */) != 0;
        int room = needsScroll && !hasScroll ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;
        Columns[0].Width = Math.Max(Si(200), ClientSize.Width - room);
    }

    /// <summary>Shows the rows; when only their figures changed it repaints in place, keeping the scroll position and selection.</summary>
    public void SetRows(IReadOnlyList<AppRow> rows)
    {
        bool sameItems = rows.Count == Items.Count && rows.Select(r => r.Exe).SequenceEqual(Rows.Select(r => r.Exe));
        Rows = rows;
        if (sameItems)
        {
            for (int i = 0; i < rows.Count; i++) { Items[i].Tag = rows[i]; Items[i].Text = rows[i].Name; }
            Invalidate();
            return;
        }
        var keep = SelectedItems.Count > 0 ? ((AppRow)SelectedItems[0].Tag!).Exe : null;
        BeginUpdate();
        Items.Clear();
        foreach (var r in rows) Items.Add(new ListViewItem(r.Name) { Tag = r, Selected = r.Exe == keep });
        EndUpdate();
        Stretch();
        Invalidate();
    }

    Rectangle ActionRect(Rectangle row) => new(row.Right - Si(58), row.Top, Si(48), row.Height);

    int ActionAt(Point pt)
    {
        var item = GetItemAt(pt.X, pt.Y);
        return item != null && ActionRect(item.Bounds).Contains(pt) ? item.Index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int a = ActionAt(e.Location);
        Cursor = a >= 0 ? Cursors.Hand : Cursors.Default;
        if (a == hotAction) return;
        if (hotAction >= 0 && hotAction < Items.Count) Invalidate(Items[hotAction].Bounds);
        hotAction = a;
        if (a >= 0) Invalidate(Items[a].Bounds);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hotAction >= 0 && hotAction < Items.Count) Invalidate(Items[hotAction].Bounds);
        hotAction = -1;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ActionAt(e.Location) is var i and >= 0) Act?.Invoke((AppRow)Items[i].Tag!);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedItems.Count == 0) return;
        if ((!adds && e.KeyCode == Keys.Delete) || (adds && e.KeyCode == Keys.Enter))
        {
            Act?.Invoke((AppRow)SelectedItems[0].Tag!);
            e.Handled = true;
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) { }
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (e.Item.Tag is not AppRow row) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var b = e.Bounds with { Width = Math.Max(e.Bounds.Width, ClientSize.Width) };
        float s = DeviceDpi / 96f;
        var bg = e.Item.Selected ? Gfx.Mix(P.GroupedCard, P.Accent, Focused ? 0.16f : 0.08f) : P.GroupedCard;
        using (var brush = new SolidBrush(bg)) g.FillRectangle(brush, b);
        if (e.ItemIndex < Items.Count - 1)
        {
            using var line = new Pen(P.Separator);
            g.DrawLine(line, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
        }

        float t = S(32);
        Glyphs.Tile(g, Glyphs.Of(row.Kind), Glyphs.ColorOf(row.Kind, P), new RectangleF(b.Left + S(16), b.Top + (b.Height - t) / 2, t, t), s);

        var action = ActionRect(b);
        int statusW = Si(150), left = b.Left + Si(16) + (int)t + Si(12), statusX = action.Left - statusW, textW = Math.Max(Si(60), statusX - left - Si(10));
        var nameFont = Theme.UI(10.5f);
        int nh = Gfx.Measure("國", nameFont).Height, sh = Gfx.Measure("國", Theme.Body).Height, top = b.Top + (b.Height - nh - sh - Si(2)) / 2;
        const TextFormatFlags line1 = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        Gfx.Text(g, row.Name, nameFont, P.Text, new Rectangle(left, top, textW, nh), line1);
        Gfx.Text(g, $"{row.Exe} · {KnownApps.KindLabel(row.Kind)}", Theme.Body, P.Text2, new Rectangle(left, top + nh + Si(2), textW, sh), line1);

        var dot = row.StatusDot(P);
        Gfx.Dot(g, dot, statusX + S(4), b.Top + b.Height / 2f, S(8));
        Gfx.Text(g, row.Status, Theme.Body, row.StatusTinted ? dot : P.Text2, new Rectangle(statusX + Si(14), b.Top, statusW - Si(14), b.Height),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        Glyphs.CircleAction(g, adds ? P.Green : P.Red, new PointF(action.Left + action.Width / 2f, b.Top + b.Height / 2f), s, adds, e.ItemIndex == hotAction);
    }

    /// <summary>Paints the area under the rows in the card colour (and the empty-state line); hides the horizontal scrollbar.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083 /* WM_NCCALCSIZE */ && IsHandleCreated)
        {
            int style = GetWindowLong(Handle, -16);
            if ((style & 0x00100000 /* WS_HSCROLL */) != 0) SetWindowLong(Handle, -16, style & ~0x00100000);
        }
        base.WndProc(ref m);
        if (m.Msg != 0x000F /* WM_PAINT */ || !IsHandleCreated) return;
        if (Items.Count * RowHeight >= ClientSize.Height) return;
        int top;
        try { top = Items.Count > 0 ? Items[^1].Bounds.Bottom : 0; }
        catch (ArgumentOutOfRangeException) { return; }
        if (top >= ClientSize.Height) return;
        using var g = CreateGraphics();
        using var brush = new SolidBrush(P.GroupedCard);
        g.FillRectangle(brush, 0, top, ClientSize.Width, ClientSize.Height - top);
        if (Items.Count == 0 && EmptyText.Length > 0)
            Gfx.Text(g, EmptyText, Theme.Body, P.Text2, new Rectangle(Si(16), 0, ClientSize.Width - Si(32), Si(72)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

/// <summary>Grey caption above a card: text on the left, an optional note on the right.</summary>
sealed class Caption : Painted
{
    public string Start { get; set; } = "";
    public string End { get; set; } = "";

    public Caption() { Dock = DockStyle.Fill; }
    protected override Color Surface => P.GroupedBack;

    protected override void Draw(Graphics g)
    {
        var r = new Rectangle(Si(8), 0, Width - Si(16), Height - Si(4));
        Gfx.Text(g, Start, Theme.Body, P.Text2, r, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
        Gfx.Text(g, End, Theme.Body, P.Text2, r, TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
    }
}

/// <summary>Accent-coloured text link in the window's footer.</summary>
sealed class FooterLink : LinkLabel
{
    public FooterLink(string text)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.UI(10.5f, FontStyle.Bold);
        LinkBehavior = LinkBehavior.HoverUnderline;
        Margin = new Padding(0, 0, 24, 0);
        var p = Theme.Current;
        LinkColor = ActiveLinkColor = VisitedLinkColor = p.Accent;
        BackColor = p.GroupedBack;
    }
}
