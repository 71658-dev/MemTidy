using System.Drawing.Drawing2D;

namespace MemTidy;

// Controls for the installer window, in the exclusions window's grouped look: grey page, light cards (reversed in dark mode).

static class InstallerGlyphs
{
    public const string Shield = "", Info = "";

    /// <summary>The shield outline of the design (24-unit view box), optionally with a check inside.</summary>
    public static void Shield24(Graphics g, Color c, RectangleF box, float width, bool check)
    {
        float k = box.Width / 24f;
        PointF P(float x, float y) => new(box.X + x * k, box.Y + y * k);
        using var path = new GraphicsPath();
        path.AddLine(P(12, 2), P(20, 5));
        path.AddLine(P(20, 5), P(20, 11));
        path.AddBezier(P(20, 11), P(20, 16), P(16.6f, 20.3f), P(12, 22));
        path.AddBezier(P(12, 22), P(7.4f, 20.3f), P(4, 16), P(4, 11));
        path.AddLine(P(4, 11), P(4, 5));
        path.CloseFigure();
        using var pen = new Pen(c, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, path);
        if (check) g.DrawLines(pen, [P(8.5f, 12), P(11, 14.5f), P(15.5f, 9.5f)]);
    }

    /// <summary>A white check mark (24-unit view box).</summary>
    public static void Check24(Graphics g, Color c, RectangleF box, float width)
    {
        float k = box.Width / 24f;
        using var pen = new Pen(c, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, [new PointF(box.X + 5 * k, box.Y + 12 * k), new PointF(box.X + 10 * k, box.Y + 17 * k), new PointF(box.X + 19 * k, box.Y + 7 * k)]);
    }

    /// <summary>A white exclamation mark (24-unit view box).</summary>
    public static void Exclaim24(Graphics g, Color c, RectangleF box, float width)
    {
        float k = box.Width / 24f;
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, box.X + 12 * k, box.Y + 7 * k, box.X + 12 * k, box.Y + 13 * k);
        Gfx.Dot(g, c, box.X + 12 * k, box.Y + 16.8f * k, width * 1.05f);
    }

    /// <summary>The activity indicator: 12 spokes fading behind the leading one.</summary>
    public static void Spinner(Graphics g, Color c, PointF center, float radius, int phase)
    {
        float w = Math.Max(1.5f, radius * 0.22f);
        using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 12; i++)
        {
            int age = (phase - i + 12) % 12;
            pen.Color = Color.FromArgb(Math.Max(40, 255 - age * 20), c);
            double a = i * Math.PI / 6 - Math.PI / 2;
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            g.DrawLine(pen, center.X + cos * radius * 0.5f, center.Y + sin * radius * 0.5f, center.X + cos * radius, center.Y + sin * radius);
        }
    }
}

/// <summary>A control whose spinner turns while <see cref="Spinning"/> is set.</summary>
abstract class Animated : Painted
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 80 };
    protected int Phase { get; private set; }

    protected Animated() { timer.Tick += (_, _) => { Phase = (Phase + 1) % 12; Invalidate(); }; }

    protected bool Spinning
    {
        get => timer.Enabled;
        set { if (timer.Enabled != value) timer.Enabled = value; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The window's top: the app icon, a large title and a grey line under it.</summary>
sealed class InstallerHeader : Painted
{
    readonly Bitmap? icon;
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    public InstallerHeader()
    {
        Dock = DockStyle.Fill;
        try { using var ico = Icon.ExtractIcon(Environment.ProcessPath!, 0, 256); icon = ico?.ToBitmap(); }
        catch { icon = TrayApp.AppIcon.ToBitmap(); }
    }

    protected override Color Surface => P.GroupedBack;

    protected override void Draw(Graphics g)
    {
        int size = Si(64), gap = Si(16);
        int top = (Height - size) / 2;
        if (icon != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(0, top, size, size));
        }
        int x = size + gap, w = Width - x;
        var titleFont = Theme.Huge;
        int th = Gfx.Measure(Title, titleFont).Height;
        var sub = TextRenderer.MeasureText(Subtitle, Theme.Body, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int total = th + Si(3) + sub.Height;
        int y = (Height - total) / 2;
        Gfx.Text(g, Title, titleFont, P.Text, new Rectangle(x, y, w, th), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        Gfx.Text(g, Subtitle, Theme.Body, P.Text2, new Rectangle(x, y + th + Si(3), w, sub.Height + 2), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}

/// <summary>iOS-style switch: a 64 × 28 capsule, green when on, with a white 24 px knob.</summary>
sealed class ToggleSwitch : Control
{
    bool on;
    public event Action? CheckedChanged;
    public Func<Palette, Color> Behind { get; init; } = p => p.GroupedCard;

    public ToggleSwitch(string name)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        AccessibleName = name;
        AccessibleRole = AccessibleRole.CheckButton;
        TabStop = true;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
    }

    public bool Checked
    {
        get => on;
        set
        {
            if (on == value) return;
            on = value;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
            CheckedChanged?.Invoke();
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessible(this);

    sealed class SwitchAccessible(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : 0);
        public override string DefaultAction => owner.Checked ? "關閉" : "開啟";
        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Behind(p));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(1, (Height - 28 * s) / 2, Width - 2, 28 * s);
        Gfx.FillRound(g, on ? p.Green : p.Track, r, r.Height / 2);
        if (Focused && ShowFocusCues) Gfx.StrokeRound(g, p.Accent, RectangleF.Inflate(r, -0.5f, -0.5f), r.Height / 2, 2 * s);
        float d = 24 * s, cy = r.Y + r.Height / 2;
        float cx = on ? r.Right - 2 * s - d / 2 : r.X + 2 * s + d / 2;
        Gfx.Dot(g, Color.FromArgb(40, 0, 0, 0), cx, cy + 1 * s, d + 1 * s);
        Gfx.Dot(g, Color.White, cx, cy, d);
    }
}

/// <summary>One row of a <see cref="RowsCard"/>: a title, a grey (optionally monospace) line, and a switch or a pill on the right.</summary>
sealed record CardRow(string Title, string Detail, bool MonoDetail = false, string? Pill = null, bool PillStrong = false, ToggleSwitch? Switch = null);

/// <summary>A rounded card of rows separated by hairlines, like an iOS grouped list.</summary>
sealed class RowsCard : Painted
{
    IReadOnlyList<CardRow> rows = [];
    public int RowHeight { get; init; } = 52;
    public float TitlePt { get; init; } = 11.25f;

    public RowsCard() { Dock = DockStyle.Top; }
    protected override Color Surface => P.GroupedBack;

    public IReadOnlyList<CardRow> Rows
    {
        get => rows;
        set
        {
            foreach (var r in rows) if (r.Switch != null && !value.Any(v => v.Switch == r.Switch)) Controls.Remove(r.Switch);
            rows = value;
            foreach (var r in rows) if (r.Switch != null && r.Switch.Parent != this) Controls.Add(r.Switch);
            Height = Si(RowHeight) * rows.Count;
            PerformLayout();
            Invalidate();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { Height = Si(RowHeight) * rows.Count; base.OnDpiChangedAfterParent(e); }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int h = Si(RowHeight);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Switch is { } sw)
                sw.Bounds = new Rectangle(Width - Si(16) - Si(64), i * h + (h - Si(30)) / 2, Si(64), Si(30));
    }

    protected override void Draw(Graphics g)
    {
        if (rows.Count == 0) return;
        Gfx.FillRound(g, P.GroupedCard, new RectangleF(0, 0, Width, Height), S(10));
        int h = Si(RowHeight), pad = Si(16);
        var titleFont = Theme.UI(TitlePt);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int y = i * h;
            if (i > 0)
            {
                using var pen = new Pen(P.Separator, 1);
                g.DrawLine(pen, pad, y, Width, y);
            }
            int right = Width - pad;
            if (row.Switch != null) right -= Si(64) + Si(12);
            if (row.Pill != null)
            {
                var pf = Theme.UI(9f);
                var ps = Gfx.Measure(row.Pill, pf);
                var pr = new RectangleF(right - ps.Width - Si(16), y + (h - Si(22)) / 2f, ps.Width + Si(16), Si(22));
                Gfx.FillRound(g, row.PillStrong ? P.Track : P.Fill, pr, pr.Height / 2);
                Gfx.Text(g, row.Pill, pf, P.Text, Rectangle.Round(pr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                right = (int)pr.X - Si(12);
            }
            var detailFont = row.MonoDetail ? Theme.Mono : Theme.Small;
            int t1 = Gfx.Measure(row.Title, titleFont).Height, t2 = Gfx.Measure(row.Detail, detailFont).Height;
            int top = y + (h - t1 - Si(2) - t2) / 2;
            Gfx.Text(g, row.Title, titleFont, P.Text, new Rectangle(pad, top, right - pad, t1), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, row.Detail, detailFont, P.Text2, new Rectangle(pad, top + t1 + Si(2), right - pad, t2 + 1),
                TextFormatFlags.Left | TextFormatFlags.Top | (row.MonoDetail ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis));
        }
    }
}

/// <summary>Wrapped text on the page background (paragraphs, captions above cards).</summary>
sealed class TextBlock : Painted
{
    public string Text2 { get; set; } = "";
    public Font TextFont { get; init; } = Theme.Body;
    public bool Muted { get; init; }
    public int Inset { get; init; }

    public TextBlock() { Dock = DockStyle.Top; }
    protected override Color Surface => P.GroupedBack;

    public void Fit(int width)
    {
        var size = TextRenderer.MeasureText(Text2, TextFont, new Size(Math.Max(1, width - Si(Inset) * 2), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Height = size.Height + 2;
    }

    protected override void Draw(Graphics g) =>
        Gfx.Text(g, Text2, TextFont, Muted ? P.Text2 : P.Text, new Rectangle(Si(Inset), 0, Width - Si(Inset) * 2, Height),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
}

/// <summary>A blue information icon and a note on a soft grey panel.</summary>
sealed class InfoBanner : Painted
{
    public string Message { get; set; } = "";
    public InfoBanner() { Dock = DockStyle.Top; }
    protected override Color Surface => P.GroupedBack;

    public void Fit(int width)
    {
        var size = TextRenderer.MeasureText(Message, Theme.Body, new Size(Math.Max(1, width - Si(14 + 16 + 10 + 14)), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Height = size.Height + Si(24);
    }

    protected override void Draw(Graphics g)
    {
        Gfx.FillRound(g, P.Dark ? P.Fill : P.Track, new RectangleF(0, 0, Width, Height), S(10));
        Gfx.Text(g, InstallerGlyphs.Info, Theme.Icons(11f), P.Accent, new Rectangle(Si(14), Si(12), Si(18), Si(18)), TextFormatFlags.Left | TextFormatFlags.Top);
        int x = Si(14 + 16 + 10);
        Gfx.Text(g, Message, Theme.Body, P.Text, new Rectangle(x, Si(12), Width - x - Si(14), Height - Si(20)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}

/// <summary>The progress screen: one row per step (done, in progress, not yet), and a thin bar under the card.</summary>
sealed class StepsCard : Animated
{
    IReadOnlyList<(string Title, string Detail, StepState State)> steps = [];

    public StepsCard() { Dock = DockStyle.Fill; }
    protected override Color Surface => P.GroupedBack;

    public void Set(IReadOnlyList<(string, string, StepState)> value)
    {
        steps = value;
        Spinning = steps.Any(s => s.State == StepState.Active);
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        if (steps.Count == 0) return;
        int h = Si(52), pad = Si(16), cardH = h * steps.Count;
        Gfx.FillRound(g, P.GroupedCard, new RectangleF(0, 0, Width, cardH), S(10));
        var titleFont = Theme.UI(11.25f);
        for (int i = 0; i < steps.Count; i++)
        {
            var (title, detail, state) = steps[i];
            int y = i * h;
            if (i > 0)
            {
                using var pen = new Pen(P.Separator, 1);
                g.DrawLine(pen, pad + Si(36), y, Width, y);
            }
            float cx = pad + S(11), cy = y + h / 2f;
            switch (state)
            {
                case StepState.Done:
                    Gfx.Dot(g, P.Green, cx, cy, S(22));
                    InstallerGlyphs.Check24(g, Color.White, new RectangleF(cx - S(8), cy - S(8), S(16), S(16)), S(2.4f));
                    break;
                case StepState.Active:
                    InstallerGlyphs.Spinner(g, P.Text2, new PointF(cx, cy), S(10), Phase);
                    break;
                default:
                    using (var pen = new Pen(P.Gray, S(1.5f))) g.DrawEllipse(pen, cx - S(10), cy - S(10), S(20), S(20));
                    break;
            }
            string status = state switch { StepState.Done => "完成", StepState.Active => "進行中", _ => "" };
            int sw = status.Length > 0 ? Gfx.Measure(status, Theme.Small).Width : 0;
            if (sw > 0) Gfx.Text(g, status, Theme.Small, P.Text2, new Rectangle(Width - pad - sw, y, sw + 2, h));
            int x = pad + Si(36), w = Width - x - pad - sw - Si(10);
            var color = state == StepState.Todo ? Color.FromArgb(153, P.Text) : P.Text;
            int t1 = Gfx.Measure(title, titleFont).Height, t2 = Gfx.Measure(detail, Theme.Small).Height;
            int top = y + (h - t1 - Si(2) - t2) / 2;
            Gfx.Text(g, title, titleFont, color, new Rectangle(x, top, w, t1), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, detail, Theme.Small, P.Text2, new Rectangle(x, top + t1 + Si(2), w, t2 + 1), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.PathEllipsis);
        }
        int done = steps.Count(s => s.State == StepState.Done);
        var bar = new RectangleF(0, cardH + S(18), Width, S(4));
        Gfx.FillRound(g, P.Track, bar, bar.Height / 2);
        if (done > 0) Gfx.FillRound(g, P.Accent, new RectangleF(bar.X, bar.Y, bar.Width * done / steps.Count, bar.Height), bar.Height / 2);
    }
}

enum Badge { Shield, Check, Warning }

/// <summary>The centred screens (waiting for Windows' prompt, done, error): a badge, a title, text, and an optional note or code.</summary>
sealed class CenterMessage : Animated
{
    public Badge Badge { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>A small card under the text (the tray-icon hint).</summary>
    public string? Chip { get; set; }
    /// <summary>Monospace lines under the text (expected / actual hash).</summary>
    public string? Code { get; set; }
    public bool Waiting { get => Spinning; set => Spinning = value; }

    public CenterMessage() { Dock = DockStyle.Fill; }
    protected override Color Surface => P.GroupedBack;

    protected override void Draw(Graphics g)
    {
        int maxW = Math.Min(Width, Si(440));
        var titleFont = Badge == Badge.Shield ? Theme.UI(12.75f, FontStyle.Bold) : Theme.UI(15f, FontStyle.Bold);
        var bodyFont = Badge == Badge.Shield ? Theme.Body : Theme.UI(10.5f);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        int badge = Badge == Badge.Shield ? Si(72) : Si(64), gap = Badge == Badge.Shield ? Si(16) : Si(14);
        var ts = TextRenderer.MeasureText(Title, titleFont, new Size(maxW, int.MaxValue), flags);
        var bs = TextRenderer.MeasureText(Body, bodyFont, new Size(maxW, int.MaxValue), flags);
        Size cs = Size.Empty;
        var chipFont = Theme.Body;
        if (Code != null) cs = TextRenderer.MeasureText(Code, Theme.Mono, Size.Empty, TextFormatFlags.NoPadding) + new Size(Si(24), Si(16));
        else if (Chip != null) cs = Gfx.Measure(Chip, chipFont) + new Size(Si(14 + 14 + 10 + 14), Si(16));
        int waitH = Waiting ? Si(20) : 0;
        int total = badge + gap + ts.Height + gap + bs.Height + (cs.Height > 0 ? gap + cs.Height : 0) + (Waiting ? gap + waitH : 0);
        int y = Math.Max(0, (Height - total) / 2), cx = Width / 2;

        var br = new RectangleF(cx - badge / 2f, y, badge, badge);
        switch (Badge)
        {
            case Badge.Shield:
                Gfx.FillRound(g, P.Dark ? P.Fill : P.Track, br, S(20));
                InstallerGlyphs.Shield24(g, P.Accent, RectangleF.Inflate(br, -S(18), -S(18)), S(2.6f), check: true);
                break;
            case Badge.Check:
                Gfx.Dot(g, P.Green, br.X + br.Width / 2, br.Y + br.Height / 2, badge);
                InstallerGlyphs.Check24(g, Color.White, RectangleF.Inflate(br, -S(16), -S(16)), S(4f));
                break;
            default:
                Gfx.Dot(g, P.Orange, br.X + br.Width / 2, br.Y + br.Height / 2, badge);
                InstallerGlyphs.Exclaim24(g, Color.White, RectangleF.Inflate(br, -S(16), -S(16)), S(4.2f));
                break;
        }
        y += badge + gap;
        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak;
        Gfx.Text(g, Title, titleFont, P.Text, new Rectangle(cx - maxW / 2, y, maxW, ts.Height + 2), center);
        y += ts.Height + gap;
        Gfx.Text(g, Body, bodyFont, P.Text2, new Rectangle(cx - maxW / 2, y, maxW, bs.Height + 2), center);
        y += bs.Height;
        if (cs.Height > 0)
        {
            y += gap;
            var r = new RectangleF(cx - cs.Width / 2f, y, cs.Width, cs.Height);
            if (Code != null)
            {
                Gfx.FillRound(g, P.Dark ? P.Fill : P.Track, r, S(8));
                Gfx.Text(g, Code, Theme.Mono, P.Text2, Rectangle.Round(RectangleF.Inflate(r, -S(12), -S(8))), TextFormatFlags.Left | TextFormatFlags.Top);
            }
            else
            {
                Gfx.FillRound(g, P.GroupedCard, r, S(10));
                Gfx.FillRound(g, P.Green, new RectangleF(r.X + S(14), r.Y + (r.Height - S(14)) / 2, S(14), S(14)), S(4));
                Gfx.Text(g, Chip!, chipFont, P.Text, Rectangle.Round(new RectangleF(r.X + S(38), r.Y, r.Width - S(38), r.Height)));
            }
            y += cs.Height;
        }
        if (Waiting)
        {
            y += gap;
            const string wait = "等待確認…";
            int ww = Gfx.Measure(wait, Theme.Body).Width, total2 = Si(16) + Si(8) + ww;
            int x = cx - total2 / 2;
            InstallerGlyphs.Spinner(g, P.Text2, new PointF(x + S(8), y + waitH / 2f), S(8), Phase);
            Gfx.Text(g, wait, Theme.Body, P.Text2, new Rectangle(x + Si(24), y, ww + 2, waitH));
        }
    }
}
