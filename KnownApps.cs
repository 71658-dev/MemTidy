using System.Diagnostics;

namespace MemTidy;

enum AppKind { Browser, Call, Music, Recording, Other }

/// <summary>Friendly names and kinds for the exclusions window, so a row reads "LINE 通話元件", not just "linecall.exe".</summary>
static class KnownApps
{
    static readonly Dictionary<string, (string Name, AppKind Kind)> table = new(StringComparer.OrdinalIgnoreCase)
    {
        // The default exclusions
        ["spotify.exe"] = ("Spotify", AppKind.Music),
        ["vlc.exe"] = ("VLC 媒體播放器", AppKind.Music),
        ["foobar2000.exe"] = ("foobar2000", AppKind.Music),
        ["obs64.exe"] = ("OBS Studio", AppKind.Recording),
        ["zoom.exe"] = ("Zoom", AppKind.Call),
        ["ms-teams.exe"] = ("Microsoft Teams", AppKind.Call),
        ["teams.exe"] = ("Microsoft Teams（傳統版）", AppKind.Call),
        ["discord.exe"] = ("Discord", AppKind.Call),
        ["webex.exe"] = ("Webex", AppKind.Call),
        ["line.exe"] = ("LINE", AppKind.Call),
        ["linecall.exe"] = ("LINE 通話元件", AppKind.Call),
        ["linemediaplayer.exe"] = ("LINE 媒體播放器", AppKind.Music),
        ["rtkauduservice64.exe"] = ("Realtek 音訊服務", AppKind.Music),
        ["brave.exe"] = ("Brave", AppKind.Browser),
        ["claude.exe"] = ("Claude", AppKind.Other),
        ["outlook.exe"] = ("Outlook", AppKind.Other),
        // Often added
        ["chrome.exe"] = ("Google Chrome", AppKind.Browser),
        ["msedge.exe"] = ("Microsoft Edge", AppKind.Browser),
        ["firefox.exe"] = ("Firefox", AppKind.Browser),
        ["opera.exe"] = ("Opera", AppKind.Browser),
        ["vivaldi.exe"] = ("Vivaldi", AppKind.Browser),
        ["arc.exe"] = ("Arc", AppKind.Browser),
        ["msedgewebview2.exe"] = ("Edge WebView2", AppKind.Browser),
        ["slack.exe"] = ("Slack", AppKind.Call),
        ["skype.exe"] = ("Skype", AppKind.Call),
        ["telegram.exe"] = ("Telegram", AppKind.Call),
        ["whatsapp.exe"] = ("WhatsApp", AppKind.Call),
        ["wechat.exe"] = ("WeChat", AppKind.Call),
        ["googlemeet.exe"] = ("Google Meet", AppKind.Call),
        ["applemusic.exe"] = ("Apple Music", AppKind.Music),
        ["itunes.exe"] = ("iTunes", AppKind.Music),
        ["kkbox.exe"] = ("KKBOX", AppKind.Music),
        ["musicbee.exe"] = ("MusicBee", AppKind.Music),
        ["aimp.exe"] = ("AIMP", AppKind.Music),
        ["potplayermini64.exe"] = ("PotPlayer", AppKind.Music),
        ["mpc-hc64.exe"] = ("MPC-HC", AppKind.Music),
        ["obs32.exe"] = ("OBS Studio（32 位元）", AppKind.Recording),
        ["snippingtool.exe"] = ("剪取工具", AppKind.Recording),
        ["sharex.exe"] = ("ShareX", AppKind.Recording),
        ["bandicam.exe"] = ("Bandicam", AppKind.Recording),
        ["camtasia.exe"] = ("Camtasia", AppKind.Recording),
        ["onedrive.exe"] = ("OneDrive", AppKind.Other),
        ["dropbox.exe"] = ("Dropbox", AppKind.Other),
        ["googledrivefs.exe"] = ("Google 雲端硬碟", AppKind.Other),
        ["notion.exe"] = ("Notion", AppKind.Other),
        ["code.exe"] = ("Visual Studio Code", AppKind.Other),
    };

    public static string KindLabel(AppKind k) => k switch
    {
        AppKind.Browser => "瀏覽器",
        AppKind.Call => "通話",
        AppKind.Music => "音樂",
        AppKind.Recording => "錄影",
        _ => "其他",
    };

    /// <summary>Name and kind for an exe; a running copy's file description is used for unknown programs.</summary>
    public static (string Name, AppKind Kind) Describe(string exe, string? path)
    {
        if (table.TryGetValue(exe, out var known)) return known;
        if (path != null && described.TryGetValue(path, out var cached)) return (cached, AppKind.Other);
        string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        if (path != null)
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d) && d.Length <= 60) name = d;
            }
            catch { /* unreadable version info: the file name will do */ }
            described[path] = name;
        }
        return (name, AppKind.Other);
    }

    static readonly Dictionary<string, string> described = new(StringComparer.OrdinalIgnoreCase);
}
