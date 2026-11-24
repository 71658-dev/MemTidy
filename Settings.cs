using System.Text.Json;

namespace MemTidy;

sealed class Settings
{
    /// <summary>How long an app must stay out of the foreground before it may nap.</summary>
    public double IdleMinutes { get; set; } = 5;
    /// <summary>CPU use (percent of one core) below which a process counts as idle.</summary>
    public double IdleCpuPercent { get; set; } = 1.0;
    /// <summary>A napping process that uses more than this is woken up (it is doing real work, e.g. playing audio).</summary>
    public double WakeCpuPercent { get; set; } = 5.0;
    /// <summary>Seconds of continuous low CPU required before napping.</summary>
    public int QuietSeconds { get; set; } = 60;
    public double TrimCooldownMinutes { get; set; } = 10;
    public int MaxTrimsPerCycle { get; set; } = 5;
    public int MinTrimWorkingSetMB { get; set; } = 50;
    public bool Paused { get; set; }

    /// <summary>True only when no settings file existed yet, i.e. the very first launch.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool FirstRun { get; private set; }

    /// <summary>User-editable exclusions. Defaults cover apps that work while in the background (calls, music, recording).</summary>
    public List<string> Exclusions { get; set; } =
    [
        "spotify.exe", "vlc.exe", "foobar2000.exe", "obs64.exe", "zoom.exe",
        "ms-teams.exe", "teams.exe", "discord.exe", "webex.exe", "line.exe", "linecall.exe", "linemediaplayer.exe",
        "rtkauduservice64.exe", "brave.exe", "claude.exe", "outlook.exe",
    ];

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemTidy");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string LogPath => Path.Combine(Dir, "memtidy.log");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) { ActionLog.Add($"設定檔讀取失敗，改用預設值：{ex.Message}"); }
        var s = new Settings { FirstRun = true };
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { ActionLog.Add($"設定檔儲存失敗：{ex.Message}"); }
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(Dir);
        System.Diagnostics.Process.Start("explorer.exe", Dir);
    }
}

/// <summary>Recent actions, kept in memory for the panel and appended to a capped log file.</summary>
static class ActionLog
{
    public readonly record struct Entry(DateTime Time, string Text);

    static readonly LinkedList<Entry> entries = new();
    const int Max = 200;

    public static IEnumerable<Entry> Recent => entries;

    public static void Add(string text)
    {
        var e = new Entry(DateTime.Now, text);
        entries.AddFirst(e);
        if (entries.Count > Max) entries.RemoveLast();
        try
        {
            var fi = new FileInfo(Settings.LogPath);
            fi.Directory?.Create();
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
            File.AppendAllText(fi.FullName, $"{e.Time:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch { /* logging must never break the tool */ }
    }
}
