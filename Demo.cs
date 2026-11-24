namespace MemTidy;

/// <summary>
/// "--demo": the lists show fixed sample rows (the same as the MemTidy UI design) instead of this computer's
/// processes, log and cached files, so screenshots for the README reveal nothing about the machine.
/// Memory figures and the pressure graph stay real.
/// </summary>
static class Demo
{
    public static bool On { get; set; }

    public static (string Name, int Pid, long WorkingSet, TimeSpan Napped, long Trimmed)[] Naps { get; } =
    [
        ("msedgewebview2.exe", 25896, 186L << 20, TimeSpan.FromMinutes(9), 139L << 20),
        ("vbcscompiler.exe", 22892, 41L << 20, TimeSpan.FromMinutes(7), 214L << 20),
        ("chrome.exe", 23580, 96L << 20, TimeSpan.FromMinutes(3), 174L << 20),
        ("onedrive.exe", 9120, 58L << 20, TimeSpan.FromMinutes(15), 0),
    ];

    public static long TotalTrimmed => 767L << 20;

    /// <summary>Log lines in the real log's wording, newest first, with their age.</summary>
    public static (TimeSpan Ago, string Text)[] Log { get; } =
    [
        (TimeSpan.FromSeconds(20), "喚醒 code.exe (23688)：回到前景"),
        (TimeSpan.FromSeconds(100), "記憶體壓力：偏高 → 正常"),
        (TimeSpan.FromSeconds(128), "記憶體壓力：吃緊 → 偏高"),
        (TimeSpan.FromSeconds(144), "整理 chrome.exe (23580)：釋出 174 MB 交給系統壓縮/換出"),
        (TimeSpan.FromSeconds(144), "整理 code.exe (23688)：釋出 240 MB 交給系統壓縮/換出"),
        (TimeSpan.FromSeconds(150), "記憶體壓力：偏高 → 吃緊"),
        (TimeSpan.FromSeconds(164), "休眠 chrome.exe (23580)：降低優先權、效率模式、記憶體低優先"),
        (TimeSpan.FromSeconds(170), "休眠 vbcscompiler.exe (22892)：降低優先權、效率模式、記憶體低優先"),
        (TimeSpan.FromSeconds(171), "休眠 msedgewebview2.exe (25896)：降低優先權、效率模式、記憶體低優先"),
    ];

    public static StandbyFiles.Report Cache()
    {
        (string, long)[] files =
        [
            (@"C:\Program Files\Microsoft VS Code\Code.exe", 229),
            (@"C:\Program Files\nodejs\node.exe", 116),
            (@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", 82),
            (@"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", 79),
            (@"C:\Program Files\GitHub CLI\gh.exe", 54),
            (@"C:\Program Files\dotnet\sdk\10.0.404\Roslyn\bincore\Microsoft.CodeAnalysis.CSharp.dll", 21),
            (@"C:\Windows\System32\winevt\Logs\Application.evtx", 19),
            (@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.dll", 18),
            (@"C:\Windows\System32\Windows.UI.Xaml.dll", 17),
            (@"C:\Windows\System32\DriverStore\FileRepository\iigd_dch.inf_amd64\igd10um64xe.dll", 13),
            (@"C:\Windows\System32\SnippingTool\SnippingToolUI.dll", 13),
            (@"C:\Program Files\dotnet\sdk\10.0.404\Microsoft.Build.dll", 10),
        ];
        var list = files.Select(f => (f.Item1, f.Item2 << 20)).ToList();
        return new StandbyFiles.Report(3_300L << 20, 3_000L << 20, 3_000L << 20, list, new long[8], TimeSpan.FromSeconds(0.6), null);
    }
}
