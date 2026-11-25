namespace MemTidy;

/// <summary>
/// The installer window: what install.ps1 does, as screens. Opened by a plain start of a copy outside Program Files
/// (such as dist\MemTidy.exe) or by "MemTidy.exe --install". The work itself is in <see cref="Installer"/>.
/// </summary>
sealed class InstallerForm : Form
{
    enum Screen { Welcome, ConfirmRemove, Uac, Progress, Done, Removed, Error, AdminWarning, InstalledCopy }

    readonly Installer installer;
    readonly InstallerHeader header = new();
    readonly Panel body = new() { Margin = Padding.Empty };
    readonly Panel footer = new() { Margin = Padding.Empty };
    readonly ToggleSwitch autoStart = new("開機時自動啟動") { Checked = true };
    readonly ToggleSwitch launch = new("完成後立即啟動") { Checked = true };
    readonly ToggleSwitch wipe = new("同時刪除設定與紀錄檔");
    readonly List<Control> stack = [];
    readonly List<int> gaps = [];
    Screen screen;
    InstallFailure failure;
    bool busy;

    public InstallerForm(Installer installer)
    {
        this.installer = installer;
        SuspendLayout();
        var p = Theme.Current;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Icon = TrayApp.AppIcon;
        Font = Theme.Body;
        BackColor = p.GroupedBack;
        ForeColor = p.Text;
        ClientSize = new Size(600, 640);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        body.BackColor = footer.BackColor = p.GroupedBack;
        Controls.Add(header);
        Controls.Add(body);
        Controls.Add(footer);

        autoStart.CheckedChanged += () => { installer.AutoStart = autoStart.Checked; if (screen == Screen.Welcome) Go(Screen.Welcome); };
        launch.CheckedChanged += () => installer.LaunchAfter = launch.Checked;
        wipe.CheckedChanged += () => installer.RemoveSettings = wipe.Checked;

        installer.StepsChanged += () => Post(() => { if (screen == Screen.Progress) RefreshSteps(); });
        installer.WaitingForConsent += waiting => Post(() => Go(waiting ? Screen.Uac : Screen.Progress));

        Go(Startup.IsAdmin && !installer.Simulate ? Screen.AdminWarning
             : Startup.IsInstalledCopy && !installer.Simulate ? Screen.InstalledCopy
             : Screen.Welcome);
        ResumeLayout(false);
    }

    void Post(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Half-way through, closing would leave the user guessing what was done: the work takes only seconds.
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    float Scale1 => DeviceDpi / 96f;
    int Si(float v) => (int)Math.Round(v * Scale1);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (header == null) return; // during base construction
        int left = Si(28), w = ClientSize.Width - left * 2;
        header.Bounds = new Rectangle(left, Si(26), w, Si(72));
        footer.Bounds = new Rectangle(left, ClientSize.Height - Si(22) - Si(36), w, Si(36));
        int top = header.Bottom + Si(18);
        body.Bounds = new Rectangle(left, top, w, footer.Top - Si(18) - top);
        LayoutBody();
        LayoutFooter();
    }

    // ---- Body: a vertical stack of controls, or one control filling it ----

    void SetBody(params (Control Control, int GapBefore)[] items)
    {
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList())
        {
            body.Controls.Remove(c);
            if (c is not ToggleSwitch && !items.Any(i => i.Control == c)) c.Dispose();
        }
        stack.Clear();
        gaps.Clear();
        foreach (var (c, gap) in items)
        {
            c.Dock = DockStyle.None;
            stack.Add(c);
            gaps.Add(gap);
            body.Controls.Add(c);
        }
        body.ResumeLayout(false);
        LayoutBody();
    }

    void LayoutBody()
    {
        int y = 0, w = body.Width;
        for (int i = 0; i < stack.Count; i++)
        {
            var c = stack[i];
            y += Si(gaps[i]);
            if (c is InfoBanner b) b.Fit(w);
            if (c is TextBlock t) t.Fit(w);
            bool fill = c is StepsCard or CenterMessage;
            c.Bounds = new Rectangle(0, y, w, fill ? body.Height - y : c.Height);
            y += c.Height;
        }
    }

    // ---- Footer: an optional red link on the left, then the secondary and primary buttons on the right ----

    Control? link;
    MacButton? secondary, primary;

    void SetFooter(string? linkText, Action? onLink, string? second, Action? onSecond, string? first, Action? onFirst, bool shield = false, bool destructive = false)
    {
        foreach (Control c in footer.Controls.Cast<Control>().ToList()) { footer.Controls.Remove(c); c.Dispose(); }
        link = secondary = primary = null;
        var p = Theme.Current;
        if (linkText != null)
        {
            var l = new LinkLabel
            {
                Text = linkText, AutoSize = true, Font = Theme.Body, LinkBehavior = LinkBehavior.HoverUnderline,
                LinkColor = p.Red, ActiveLinkColor = p.Red, VisitedLinkColor = p.Red, BackColor = p.GroupedBack,
            };
            l.LinkClicked += (_, _) => onLink?.Invoke();
            link = l;
            footer.Controls.Add(l);
        }
        if (second != null)
        {
            secondary = new MacButton(second) { Behind = q => q.GroupedBack };
            secondary.Click += (_, _) => onSecond?.Invoke();
            footer.Controls.Add(secondary);
        }
        if (first != null)
        {
            primary = new MacButton(first) { Primary = true, Destructive = destructive, Behind = q => q.GroupedBack, Glyph = shield ? InstallerGlyphs.Shield : null };
            primary.Font = Theme.UI(9.75f, FontStyle.Bold);
            primary.Click += (_, _) => onFirst?.Invoke();
            footer.Controls.Add(primary);
            AcceptButton = primary;
        }
        else AcceptButton = null;
        CancelButton = secondary;
        LayoutFooter();
        (primary ?? (Control?)secondary)?.Focus();
    }

    void LayoutFooter()
    {
        int h = Si(34), y = (footer.Height - h) / 2, x = footer.Width;
        foreach (var b in new[] { primary, secondary })
        {
            if (b == null) continue;
            int tw = Gfx.Measure(b.Text, b.Font).Width + (b.Glyph != null ? Si(26) : 0);
            int bw = Math.Max(Si(96), tw + Si(36));
            x -= bw;
            b.Bounds = new Rectangle(x, y, bw, h);
            x -= Si(10);
        }
        if (link != null) link.Location = new Point(0, (footer.Height - link.Height) / 2);
    }

    // ---- Screens ----

    string Verb => installer.Removing ? "移除" : installer.Update ? "更新" : "安裝";

    void Go(Screen s)
    {
        screen = s;
        var p = Theme.Current;
        Text = installer.Removing ? "移除 MemTidy" : installer.Update ? "MemTidy 更新程式" : "MemTidy 安裝程式";
        switch (s)
        {
            case Screen.Welcome: ShowWelcome(); break;
            case Screen.ConfirmRemove: ShowConfirmRemove(); break;
            case Screen.Uac:
                Head($"正在{Verb} MemTidy", "等待系統管理員確認");
                SetBody((new CenterMessage
                {
                    Badge = Badge.Shield,
                    Title = "請在 Windows 確認視窗按「是」",
                    Body = $"只有這一步需要系統管理員權限：{(installer.Removing ? "刪除 Program Files 裡的程式檔" : "把程式檔放進 Program Files，讓其他程式無法掉包它")}。" +
                           "其他步驟都以你的帳號執行，MemTidy 平常也是一般權限。",
                    Waiting = true,
                }, 0));
                SetFooter(null, null, null, null, null, null);
                break;
            case Screen.Progress:
                Head($"正在{Verb} MemTidy", "請稍候，約需幾秒鐘");
                SetBody((new StepsCard(), 0));
                RefreshSteps();
                SetFooter(null, null, null, null, null, null);
                break;
            case Screen.Done:
            {
                Head(installer.Update ? "更新完成" : "安裝完成", Installer.InstallDir);
                bool launched = installer.LaunchAfter;
                var m = new CenterMessage
                {
                    Badge = Badge.Check,
                    Title = launched ? "MemTidy 已在系統匣執行" : "MemTidy 已安裝",
                    Body = (launched ? "記憶體寬裕時它什麼都不做；開始吃緊時，才會讓閒置的背景程式讓出資源，切回去立刻恢復。" : "可從開始功能表啟動 MemTidy。")
                           + Warnings(),
                    Chip = launched ? "圖示顏色＝記憶體壓力，左鍵開面板、右鍵開選單" : null,
                };
                SetBody((m, 0));
                SetFooter(null, null, null, null, "完成", Close);
                break;
            }
            case Screen.Removed:
                Head("已移除 MemTidy", installer.RemoveSettings ? "程式、開機啟動與設定都已刪除" : $"設定與紀錄檔仍保留在 {Installer.DataDirShown}");
                SetBody((new CenterMessage { Badge = Badge.Check, Title = "已從這台電腦移除", Body = "所有被調整過的程式都已恢復原狀。" + Warnings() }, 0));
                SetFooter(null, null, null, null, "完成", Close);
                break;
            case Screen.Error: ShowError(); break;
            case Screen.AdminWarning:
                Text = "MemTidy 安裝程式";
                Head("請改用一般權限開啟", "安裝程式以系統管理員身分執行了，沒有做任何變更");
                SetBody((new CenterMessage
                {
                    Badge = Badge.Warning,
                    Title = "請關閉後直接雙擊開啟",
                    Body = "只有「把程式檔放進 Program Files」需要系統管理員權限，安裝程式會在那一步才請 Windows 確認。" +
                           "開機時自動啟動和 MemTidy 本身都必須以你的帳號、一般權限設定與執行。",
                }, 0));
                SetFooter(null, null, null, null, "結束", Close);
                break;
            case Screen.InstalledCopy:
                Text = "MemTidy 安裝程式";
                Head("請從安裝檔開啟", Installer.InstallDir);
                SetBody((new CenterMessage
                {
                    Badge = Badge.Warning,
                    Title = "這是已安裝的 MemTidy",
                    Body = "它無法取代或移除正在執行的自己。要更新或移除，請雙擊新建置的 dist\\MemTidy.exe，或在命令列執行 install.ps1（移除：install.ps1 -Uninstall）。",
                }, 0));
                SetFooter(null, null, null, null, "結束", Close);
                break;
        }
    }

    string Warnings() => installer.Warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", installer.Warnings);

    void Head(string title, string subtitle)
    {
        header.Title = title;
        header.Subtitle = subtitle;
        header.Invalidate();
    }

    void ShowWelcome()
    {
        installer.Removing = false;
        bool upd = installer.Update;
        Text = upd ? "MemTidy 更新程式" : "MemTidy 安裝程式";
        Head(upd ? "更新 MemTidy 記憶體整理" : "安裝 MemTidy 記憶體整理",
             upd ? "將以這個版本取代已安裝的 MemTidy，設定與排除清單會保留" : "仿 macOS 的記憶體管理：只讓你沒在用的背景程式讓出資源，不關閉任何程式");

        var items = new List<(Control, int)>();
        if (upd && (installer.Simulate || Installer.MemTidyRunning))
            items.Add((new InfoBanner { Message = "MemTidy 正在執行。更新前會先結束它，被調整過的程式都會先恢復原狀，更新完再重新啟動。" }, 0));
        var options = new RowsCard
        {
            Rows =
            [
                new("開機時自動啟動", "登入 Windows 後在系統匣執行，之後可在右鍵選單關閉", Switch: autoStart),
                new($"{(upd ? "更新" : "安裝")}完成後立即啟動", "以一般權限執行，不會要求系統管理員", Switch: launch),
            ],
        };
        items.Add((options, items.Count == 0 ? 0 : 18));
        items.Add((new TextBlock { Text2 = "會做的變更", Muted = true, Inset = 4 }, 18));
        string autoPill = installer.AutoStart ? "你的帳號" : upd && Startup.Enabled ? "移除" : "不設定";
        items.Add((new RowsCard
        {
            RowHeight = 50,
            TitlePt = 10.5f,
            Rows =
            [
                new(upd ? "取代程式檔" : "安裝程式檔", Startup.InstalledExe, MonoDetail: true, Pill: "需系統管理員一次", PillStrong: true),
                new("開機時自動啟動", Installer.RunKeyPath, MonoDetail: true, Pill: autoPill),
                new("開始功能表捷徑", Installer.ShortcutShown, MonoDetail: true, Pill: "你的帳號"),
                new("設定與紀錄檔", Installer.DataDirShown, MonoDetail: true, Pill: upd ? "保留不動" : "首次執行時建立"),
            ],
        }, 6));
        SetBody(items.ToArray());
        SetFooter(upd ? "移除 MemTidy…" : null, () => Go(Screen.ConfirmRemove), "取消", Close, upd ? "更新" : "安裝", Start, shield: true);
    }

    void ShowConfirmRemove()
    {
        installer.Removing = true;
        Text = "移除 MemTidy";
        Head("移除 MemTidy", Installer.InstallDir);
        SetBody(
            (new TextBlock { Text2 = "會先結束 MemTidy 並把被調整過的程式恢復原狀，再移除程式檔、開機啟動設定和開始功能表捷徑。", TextFont = Theme.UI(10.5f) }, 0),
            (new RowsCard { RowHeight = 60, Rows = [new("同時刪除設定與紀錄檔", $"{Installer.DataDirShown}（排除清單也在這裡）", MonoDetail: true, Switch: wipe)] }, 18),
            (new TextBlock { Text2 = "保留的話，之後重新安裝會沿用你的排除清單和其他設定。", Muted = true, Inset = 4 }, 18));
        SetFooter(null, null, "取消", () => Go(Screen.Welcome), "移除", Start, shield: true, destructive: true);
    }

    void ShowError()
    {
        string verb = Verb;
        Head($"無法{verb} MemTidy", "你的電腦沒有被修改");
        string title, text;
        string? code = null;
        bool retry = false;
        switch (failure)
        {
            case InstallFailure.Cancelled:
                title = installer.Removing ? "沒有移除任何東西" : $"沒有{verb}任何東西";
                text = installer.Removing
                    ? "Windows 確認視窗按了「否」。移除 Program Files 裡的程式檔，需要你同意一次系統管理員權限。"
                    : "Windows 確認視窗按了「否」。MemTidy 要寫入 Program Files，需要你同意一次系統管理員權限。";
                retry = true;
                break;
            case InstallFailure.Hash:
                title = $"{verb}已中止，沒有寫入任何檔案";
                text = "複製到 Program Files 的檔案和安裝檔不一致，可能在安裝過程中被更換。請重新建置或下載後再試。";
                code = $"預期 SHA-256  {Installer.ShortHash(installer.ExpectedHash)}\n實際 SHA-256  {Installer.ShortHash(installer.ActualHash)}";
                break;
            case InstallFailure.InUse:
                title = "MemTidy 的檔案仍在使用中";
                text = "「查看快取內容」視窗可能還開著。關閉它之後按「再試一次」，已完成的步驟不會重做。";
                retry = true;
                break;
            case InstallFailure.RiskyEnvironment:
                title = "沒有要求系統管理員權限";
                text = $"你的使用者環境變數設定了 {string.Join("、", installer.RiskyVariables)}。這類變數會讓程式啟動時載入額外的程式碼，" +
                       "可能被用來在系統管理員步驟裡夾帶其他程式。如果不是你自己設定的，建議請資訊人員檢查。";
                break;
            default:
                title = $"{verb}失敗";
                text = $"系統管理員步驟失敗（結束代碼 {installer.ExitCode}）。詳細記錄在 {Installer.DataDirShown}\\memtidy.log。";
                break;
        }
        SetBody((new CenterMessage { Badge = Badge.Warning, Title = title, Body = text, Code = code }, 0));
        if (retry) SetFooter(null, null, "取消", () => Go(installer.Removing ? Screen.ConfirmRemove : Screen.Welcome), "再試一次", Start, shield: true);
        else SetFooter(null, null, null, null, "結束", Close);
    }

    void RefreshSteps()
    {
        if (body.Controls.OfType<StepsCard>().FirstOrDefault() is { } card)
            card.Set(installer.Steps.Select(s => (s.Title, s.Detail, s.State)).ToList());
    }

    async void Start()
    {
        if (busy) return;
        busy = true;
        Go(Screen.Progress);
        try { failure = await Task.Run(installer.Run); }
        catch (Exception ex)
        {
            ActionLog.Add($"安裝程式錯誤：{ex.GetType().Name}: {ex.Message}");
            failure = InstallFailure.Failed;
        }
        finally { busy = false; }
        if (IsDisposed) return;
        Go(failure != InstallFailure.None ? Screen.Error : installer.Removing ? Screen.Removed : Screen.Done);
        Activate();
    }

    // ---- Test switches ----

    /// <summary>--installer-step=…: open on a given screen with sample content (with --demo or --installer-fail).</summary>
    public void Preview(string step, InstallFailure fail)
    {
        switch (step)
        {
            case "confirmRemove": installer.Removing = true; Go(Screen.ConfirmRemove); break;
            case "uac": installer.PreviewSteps(0); Go(Screen.Uac); break;
            case "progress": installer.PreviewSteps(2); Go(Screen.Progress); break;
            case "done": Go(Screen.Done); break;
            case "removed": installer.Removing = true; Go(Screen.Removed); break;
            case "error":
                failure = fail == InstallFailure.None ? InstallFailure.Failed : fail;
                installer.PreviewFailure(failure);
                Go(Screen.Error);
                break;
            default: Go(Screen.Welcome); break;
        }
    }

    /// <summary>Runs the installer window (from Program.Main). Test switches as in the README.</summary>
    public static void Run(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name))?[name.Length..];
        var fail = Arg("--installer-fail=") switch
        {
            "cancel" => InstallFailure.Cancelled, "hash" => InstallFailure.Hash, "inuse" => InstallFailure.InUse, "failed" => InstallFailure.Failed,
            _ => InstallFailure.None,
        };
        bool simulate = args.Contains("--demo") || fail != InstallFailure.None;
        Theme.Preference = Installer.ThemePreference();
        if (args.Contains("--theme=dark") || args.Contains("--theme=light")) Theme.Forced = args.Contains("--theme=dark");
        ApplicationConfiguration.Initialize();
        var installer = new Installer { Simulate = simulate, SimulatedFailure = fail };
        var form = new InstallerForm(installer);
        if (Arg("--installer-step=") is string step) form.Preview(step, fail);
        if (Arg("--snapshot-installer=") is string shot)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { t.Stop(); TrayApp.Snapshot(form, shot); form.Close(); };
            form.Shown += (_, _) => t.Start();
        }
        Application.Run(form);
    }
}
