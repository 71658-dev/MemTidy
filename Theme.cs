using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MemTidy;

/// <summary>
/// Colours and type from the MemTidy UI design (macOS 26 look): flat window surface, soft grouped fills,
/// hairline separators, accent blue controls and the green / yellow / red pressure colours.
/// Follows the Windows light/dark app setting unless the user picked one in the tray menu; text colours keep at least 4.5:1 contrast on their surfaces.
/// </summary>
sealed record Palette(
    bool Dark,
    Color Window, Color Fill, Color Track, Color Zebra, Color Separator,
    Color Text, Color Text2, Color Accent, Color OnAccent,
    Color Green, Color Yellow, Color Red, Color Indigo, Color Orange, Color Gray,
    Color ButtonFace, Color ButtonFacePressed, Color ButtonBorder)
{
    public static readonly Palette Light = new(
        false,
        Window: Color.White, Fill: Color.FromArgb(242, 242, 244), Track: Color.FromArgb(229, 229, 234),
        Zebra: Color.FromArgb(245, 245, 247), Separator: Color.FromArgb(218, 218, 222),
        Text: Color.FromArgb(29, 29, 31), Text2: Color.FromArgb(99, 99, 102),
        Accent: Color.FromArgb(13, 111, 255), OnAccent: Color.White,
        Green: Color.FromArgb(52, 199, 89), Yellow: Color.FromArgb(255, 204, 0), Red: Color.FromArgb(255, 56, 60),
        Indigo: Color.FromArgb(97, 85, 245), Orange: Color.FromArgb(255, 141, 40), Gray: Color.FromArgb(174, 174, 178),
        ButtonFace: Color.White, ButtonFacePressed: Color.FromArgb(229, 229, 234), ButtonBorder: Color.FromArgb(200, 200, 205));

    public static readonly Palette DarkPalette = new(
        true,
        Window: Color.FromArgb(30, 30, 30), Fill: Color.FromArgb(44, 44, 46), Track: Color.FromArgb(58, 58, 60),
        Zebra: Color.FromArgb(38, 38, 40), Separator: Color.FromArgb(62, 62, 66),
        Text: Color.FromArgb(242, 242, 247), Text2: Color.FromArgb(161, 161, 166),
        Accent: Color.FromArgb(10, 132, 255), OnAccent: Color.White,
        Green: Color.FromArgb(48, 209, 88), Yellow: Color.FromArgb(255, 214, 10), Red: Color.FromArgb(255, 69, 58),
        Indigo: Color.FromArgb(94, 92, 230), Orange: Color.FromArgb(255, 159, 10), Gray: Color.FromArgb(99, 99, 102),
        ButtonFace: Color.FromArgb(58, 58, 60), ButtonFacePressed: Color.FromArgb(72, 72, 74), ButtonBorder: Color.FromArgb(72, 72, 74));

    public Color Of(Pressure p) => p switch { Pressure.Green => Green, Pressure.Yellow => Yellow, _ => Red };
}

static class Theme
{
    static Palette? current;

    /// <summary>The palette for the current Windows app mode; re-read with <see cref="Refresh"/>.</summary>
    public static Palette Current => current ??= Read();

    public static void Refresh() => current = Read();

    /// <summary>Switch --theme=dark|light (tests, and the elevated cache window); wins over <see cref="Preference"/>.</summary>
    public static bool? Forced { get; set; }

    /// <summary>The user's choice from settings.json (true = dark); null follows Windows.</summary>
    public static bool? Preference { get; set; }

    /// <summary>"light" / "dark" / "system" as stored in settings.json.</summary>
    public static bool? Parse(string? mode) => mode switch { "dark" => true, "light" => false, _ => null };

    /// <summary>The switch that gives another MemTidy process the palette in use now.</summary>
    public static string Arg => Current.Dark ? "--theme=dark" : "--theme=light";

    static Palette Read()
    {
        var dark = Forced ?? Preference;
        if (dark.HasValue) return dark.Value ? Palette.DarkPalette : Palette.Light;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0 ? Palette.DarkPalette : Palette.Light;
        }
        catch { return Palette.Light; }
    }

    // ---- Type (13 px body = 9.75 pt) ----
    const string Face = "Microsoft JhengHei UI";
    static readonly Dictionary<(float, FontStyle), Font> fonts = new();

    public static Font UI(float pt = 9.75f, FontStyle style = FontStyle.Regular)
    {
        if (!fonts.TryGetValue((pt, style), out var f)) fonts[(pt, style)] = f = new Font(Face, pt, style);
        return f;
    }

    public static Font Body => UI();
    public static Font Small => UI(8.25f);
    public static Font Heading => UI(9.75f, FontStyle.Bold);
    public static Font Title => UI(12.75f, FontStyle.Bold);
    public static Font Huge => UI(16.5f, FontStyle.Bold);
    static Font? mono;
    public static Font Mono => mono ??= new Font("Consolas", 8.6f);

    // ---- Window chrome ----
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark title bar in dark mode (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Form f)
    {
        int dark = Current.Dark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    /// <summary>Rounded corners for popups such as the tray menu (Windows 11).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    /// <summary>Dark scrollbars and headers for common controls in dark mode.</summary>
    public static void ApplyControlTheme(Control c) =>
        SetWindowTheme(c.Handle, Current.Dark ? "DarkMode_Explorer" : "Explorer", null);
}
