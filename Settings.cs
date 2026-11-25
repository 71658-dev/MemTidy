using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>Look of the panel and menu: "system" (follow the Windows app mode), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>True only when no settings file existed yet, i.e. the very first launch.</summary>
    [JsonIgnore] public bool FirstRun { get; private set; }

    /// <summary>User-editable exclusions. Defaults cover apps that work while in the background (calls, music, recording).</summary>
    public List<string> Exclusions { get; set; } =
    [
        "spotify.exe", "vlc.exe", "foobar2000.exe", "obs64.exe", "zoom.exe",
        "ms-teams.exe", "teams.exe", "discord.exe", "webex.exe", "line.exe", "linecall.exe", "linemediaplayer.exe",
        "rtkauduservice64.exe", "brave.exe", "claude.exe", "outlook.exe",
    ];

    /// <summary><see cref="Exclusions"/> normalised to lower-case exe names, built by <see cref="Load"/>.</summary>
    [JsonIgnore] public HashSet<string> ExcludedNames { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Suggestions the user chose to skip in the exclusions window; never suggested again.</summary>
    public List<string> DismissedSuggestions { get; set; } = [];

    public static IReadOnlyList<string> DefaultExclusions { get; } = new Settings().Exclusions;

    /// <summary>Raised after the exclusions change (from the window or a hand edit picked up by <see cref="ReloadExclusionsIfChanged"/>).</summary>
    public event Action? ExclusionsChanged;

    /// <summary>Replaces the exclusions: normalised, de-duplicated, saved, and in effect at once.</summary>
    public void SetExclusions(IEnumerable<string> names)
    {
        Exclusions = names.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 260).Select(NormalizeExe)
                          .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        ExcludedNames = new HashSet<string>(Exclusions, StringComparer.OrdinalIgnoreCase);
        Save();
        ExclusionsChanged?.Invoke();
    }

    public void Exclude(string exe)
    {
        exe = NormalizeExe(exe);
        if (ExcludedNames.Contains(exe)) return;
        ActionLog.Add($"已加入排除清單：{exe}"); // before the change: it wakes the program, which is logged too
        SetExclusions(Exclusions.Append(exe));
    }

    public void Include(string exe)
    {
        exe = NormalizeExe(exe);
        if (!ExcludedNames.Contains(exe)) return;
        ActionLog.Add($"已從排除清單移除：{exe}");
        SetExclusions(Exclusions.Where(x => !NormalizeExe(x).Equals(exe, StringComparison.OrdinalIgnoreCase)));
    }

    public void DismissSuggestion(string exe)
    {
        exe = NormalizeExe(exe);
        if (DismissedSuggestions.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        DismissedSuggestions.Add(exe);
        Save();
    }

    DateTime lastWrite = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : default;

    /// <summary>Picks up a hand edit of settings.json's exclusions (the window calls this when it is activated).</summary>
    public void ReloadExclusionsIfChanged()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var t = File.GetLastWriteTimeUtc(FilePath);
            if (t == lastWrite) return;
            lastWrite = t;
            var fresh = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
            if (fresh?.Exclusions is null) return;
            var normalized = fresh.Exclusions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeExe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalized.SetEquals(ExcludedNames)) return;
            Exclusions = fresh.Exclusions;
            ExcludedNames = new HashSet<string>(normalized.Where(x => x.Length <= 260), StringComparer.OrdinalIgnoreCase);
            ActionLog.Add("已從 settings.json 重新載入排除清單");
            ExclusionsChanged?.Invoke();
        }
        catch (Exception ex) { ActionLog.Add($"重新載入排除清單失敗（沿用目前清單）：{ex.Message}"); }
    }

    public static void EditFile()
    {
        if (!File.Exists(FilePath)) return;
        System.Diagnostics.Process.Start(Startup.SystemTool("notepad.exe"), $"\"{FilePath}\"");
    }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemTidy");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Friendly to hand edits: comments and a trailing comma don't break the file.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string LogPath => Path.Combine(Dir, "memtidy.log");

    public static Settings Load()
    {
        Settings s;
        if (!File.Exists(FilePath))
        {
            s = new Settings { FirstRun = true };
            s.Save();
        }
        else
        {
            try
            {
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
            }
            catch (Exception ex)
            {
                // Leave the file as it is (the user may want to fix it) and run on defaults meanwhile. This is not a
                // first run either, so a broken file never re-enables start at sign-in behind the user's back.
                ActionLog.Add($"設定檔格式有誤，本次改用預設值（原檔未修改）：{ex.Message}");
                s = new Settings();
            }
        }
        s.Validate();
        return s;
    }

    /// <summary>Brings every value into a sane range, so a typo in settings.json can't stop MemTidy from working.</summary>
    void Validate()
    {
        var corrected = new List<string>();
        double Real(double v, double min, double max, double fallback, string name)
        {
            if (double.IsFinite(v) && v >= min && v <= max) return v;
            corrected.Add(name);
            return fallback;
        }
        int Whole(int v, int min, int max, int fallback, string name)
        {
            if (v >= min && v <= max) return v;
            corrected.Add(name);
            return fallback;
        }

        IdleMinutes = Real(IdleMinutes, 1, 1440, 5, nameof(IdleMinutes));
        IdleCpuPercent = Real(IdleCpuPercent, 0.1, 50, 1, nameof(IdleCpuPercent));
        WakeCpuPercent = Real(WakeCpuPercent, IdleCpuPercent, 100, Math.Max(5, IdleCpuPercent), nameof(WakeCpuPercent));
        QuietSeconds = Whole(QuietSeconds, 10, 3600, 60, nameof(QuietSeconds));
        TrimCooldownMinutes = Real(TrimCooldownMinutes, 1, 1440, 10, nameof(TrimCooldownMinutes));
        MaxTrimsPerCycle = Whole(MaxTrimsPerCycle, 0, 50, 5, nameof(MaxTrimsPerCycle));
        MinTrimWorkingSetMB = Whole(MinTrimWorkingSetMB, 1, 65536, 50, nameof(MinTrimWorkingSetMB));
        if (Theme is not ("system" or "light" or "dark"))
        {
            Theme = "system";
            corrected.Add(nameof(Theme));
        }
        DismissedSuggestions ??= [];
        if (Exclusions is null)
        {
            Exclusions = new Settings().Exclusions;
            corrected.Add(nameof(Exclusions));
        }
        ExcludedNames = new HashSet<string>(
            Exclusions.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 260).Select(NormalizeExe),
            StringComparer.OrdinalIgnoreCase);

        if (corrected.Count > 0) ActionLog.Add($"設定檔有不合理的值，已改用預設：{string.Join("、", corrected)}");
    }

    public static string NormalizeExe(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe") ? name : name + ".exe";
    }

    public void Save()
    {
        // A copy running as administrator never writes into the user's profile: a program running as the user could
        // redirect the path (links, junctions) and turn the write into an administrator-level one.
        if (Startup.IsAdmin) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            lastWrite = File.GetLastWriteTimeUtc(FilePath); // our own write is not a hand edit
        }
        catch (Exception ex) { ActionLog.Add($"設定檔儲存失敗：{ex.Message}"); }
    }

    public static void OpenFolder()
    {
        if (!Startup.IsAdmin) Directory.CreateDirectory(Dir);
        System.Diagnostics.Process.Start(Startup.WindowsTool("explorer.exe"), $"\"{Dir}\"");
    }
}

/// <summary>Recent actions, kept in memory for the panel and appended to a capped log file.</summary>
static class ActionLog
{
    public readonly record struct Entry(DateTime Time, string Text);

    static readonly LinkedList<Entry> entries = new();
    const int Max = 200;

    /// <summary>See <see cref="Settings.Save"/>: as administrator the log stays in memory (the panel still shows it).</summary>
    static readonly bool writeFile = !Startup.IsAdmin;

    public static IEnumerable<Entry> Recent => entries;

    public static void Add(string text)
    {
        var e = new Entry(DateTime.Now, text);
        entries.AddFirst(e);
        if (entries.Count > Max) entries.RemoveLast();
        if (!writeFile) return;
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
