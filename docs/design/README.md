# 設計交接

給 Claude Code（或任何實作者）看的設計規格。設計稿在 Claude 的設計畫布上：
<https://claude.ai/artifact/6oShtg8C1onG3Ve4hbfeom>，分「排除清單」和「安裝程式」兩頁。

| 檔案 | 內容 |
|---|---|
| `installer.md` | 安裝程式：每個畫面、狀態、文案、錯誤對應與安全性要求（尚未實作） |
| `exclusions.md` | 排除清單視窗：已實作，列出與設計稿還差的部分 |
| `source/*.dc.html` | 設計稿原始檔（HTML），用來查精確的數值、文案和互動邏輯 |
| `source/ios27-kit.css` | 設計稿用的 iOS 27 UI Kit 顏色與元件樣式 |

## 怎麼讀

- **規格（`.md`）是準則**：它已經把設計對應到現有程式（`Theme.cs` 的 `Palette`、`Widgets.cs`、`ExclusionWidgets.cs`）。
- **原始檔是參考**：HTML 裡的 `var(--ios-…)` 顏色請對應到 `Palette`，不要把 iOS 色碼直接寫進 C#。
  對照：`--ios-blue` → `Accent`、`--ios-green/orange/red/indigo` → 同名、`--ios-bg-grouped-primary` → `GroupedBack`、
  `--ios-bg-grouped-secondary` → `GroupedCard`、`--ios-fill-3/4` → `Fill`／`Track`、次要文字 → `Text2`。
- 設計稿是 HTML，這個專案是 WinForms 自繪介面：**不要引入新的 UI 框架或套件**，用現有的繪製元件。
- 設計稿裡的記憶體數字、喚醒次數、雜湊值都是範例資料。

## 給 Claude Code 的建議指令

> 讀 `docs/design/README.md` 和 `docs/design/installer.md`，實作安裝程式視窗。沿用 `Theme.cs` 和 `Widgets.cs`
> 的做法，不要引入新的 UI 框架，安全性要求不能比 `install.ps1` 弱。先列出你打算新增或修改哪些檔案、
> 系統管理員步驟怎麼做，等我確認後再動手。

排除清單的差異比較小，可以另外一次做：

> 依照 `docs/design/exclusions.md` 補上排除清單視窗與設計稿的差異，一項一項做，每項做完用
> `--snapshot-exclusions` 截圖給我看。
