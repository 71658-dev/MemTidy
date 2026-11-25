# 安裝程式介面規格

設計稿：<https://claude.ai/artifact/6oShtg8C1onG3Ve4hbfeom>（「安裝程式」頁），原始檔 `source/Installer.dc.html`。
設計稿用 HTML 畫，實作時**不要**引入新的 UI 框架：沿用 `Theme.cs` 的 `Palette`／字型、`Widgets.cs` 與
`ExclusionWidgets.cs` 的繪製元件（`MacButton`、`PageHeader`、`ListFrame`、`Glyphs.Tile` 等），風格與 `ExclusionsForm` 一致。

## 目標

把 `install.ps1` 做的事變成一個視窗：雙擊 `dist\MemTidy.exe`（不在 Program Files 時）或 `MemTidy.exe --install`
開啟。`install.ps1` 保留給命令列與 `-WhatIf` 使用，兩者行為必須一致。
（已實作：`InstallerForm.cs`、`Installer.cs`、`InstallerWidgets.cs`；系統管理員步驟在 `AdminStep.ps1`，兩邊共用。
不在 Program Files 的版本要直接常駐時用 `--tray`，`build.ps1` 與開機啟動項目都會帶上。）

**安全性要求不能比 `install.ps1` 弱**（README「安全性」一節）：

- 只有「複製／刪除程式檔」以系統管理員執行，其餘（Run 機碼、啟動 MemTidy）都是一般權限。
- 系統管理員步驟的程式碼要直接交給 System32 的 `powershell.exe`（`-EncodedCommand`，同 `install.ps1` 的 `$adminStep`），
  **不要**用 `runas` 重新啟動 `dist\MemTidy.exe` 本身：那個檔案在使用者可寫的位置，同意之後可能已被換掉。
- 複製前先算來源 SHA-256，系統管理員步驟複製到 `MemTidy.exe.new`、比對雜湊一致才換上。
- 要求權限前呼叫 `Startup.RiskyEnvironment()`（同「查看快取內容」的檢查），有問題就不開確認視窗並說明原因。
- 以系統管理員身分直接執行安裝程式時，提示改用一般權限開啟（同 `Startup.IsAdmin` 的處理）。

## 視窗

- 用戶端大小 600 × 640（DPI 縮放同其他視窗），不可調整大小，`StartPosition = CenterScreen`。
- 標題列文字：`MemTidy 安裝程式`／`MemTidy 更新程式`／`移除 MemTidy`，深色模式套用 `Theme.ApplyTitleBar`。
- 背景 `p.GroupedBack`，卡片 `p.GroupedCard`，內距 上 26、左右 28、下 22，區塊間距 18。
- 頂部固定：64 px App 圖示（`app.ico`）＋ 標題（`Theme.Huge`）＋ 副標（`Theme.Body`、`p.Text2`）。
- 底部固定按鈕列，右對齊：次要按鈕（`MacButton`，一般樣式）在左、主要按鈕（`Primary`）在右。
  需要系統管理員的主要按鈕前加盾牌圖示（Segoe Fluent Icons `Shield`）。

## 畫面與狀態

偵測方式：`C:\Program Files\MemTidy\MemTidy.exe` 存在 → 更新；否則 → 全新安裝。

### 1. 開始（welcome）

| 項目 | 全新安裝 | 更新 |
|---|---|---|
| 標題 | 安裝 MemTidy 記憶體整理 | 更新 MemTidy 記憶體整理 |
| 副標 | 仿 macOS 的記憶體管理：只讓你沒在用的背景程式讓出資源，不關閉任何程式 | 將以這個版本取代已安裝的 MemTidy，設定與排除清單會保留 |
| 提示列 | — | MemTidy 正在執行時才顯示：「MemTidy 正在執行。更新前會先結束它，被調整過的程式都會先恢復原狀，更新完再重新啟動。」（藍色資訊圖示、`p.Fill` 底） |
| 主要按鈕 | 🛡 安裝 | 🛡 更新 |
| 左下角 | — | 「移除 MemTidy…」（紅色文字連結）→ 移除確認 |

**選項卡片**（兩列，每列 52 px，右側 iOS 樣式開關，預設皆開）：

1. 開機時自動啟動 — 「登入 Windows 後在系統匣執行，之後可在右鍵選單關閉」
2. 安裝（更新）完成後立即啟動 — 「以一般權限執行，不會要求系統管理員」

需要一個開關元件（目前 Widgets 沒有）：64×28 膠囊，開 = `p.Green`、關 = `p.Track`，白色圓形把手 24 px。

**「會做的變更」卡片**（標題 `p.Text2` 13 px，三列，路徑用 `Theme.Mono`，右側膠囊標籤）：

| 變更 | 路徑 | 標籤 |
|---|---|---|
| 安裝程式檔（更新時「取代程式檔」） | `C:\Program Files\MemTidy\MemTidy.exe` | 需系統管理員一次 |
| 開機時自動啟動 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 你的帳號（開關關閉時：不設定） |
| 設定與紀錄檔 | `%LOCALAPPDATA%\MemTidy` | 首次執行時建立（更新時：保留不動） |

### 2. 等待確認（uac）

置中：72 px 圓角方塊內藍色盾牌勾勾、「請在 Windows 確認視窗按「是」」（粗體 17 px）、說明文字、小型轉圈＋「等待確認…」。
說明：「只有這一步需要系統管理員權限：把程式檔放進 Program Files，讓其他程式無法掉包它。其他步驟都以你的帳號執行，MemTidy 平常也是一般權限。」
（移除時改為「刪除 Program Files 裡的程式檔」。）此畫面沒有按鈕。

### 3. 進行中（progress）

步驟卡片，每列：左側狀態圖示（完成＝綠底白勾、進行中＝轉圈、未開始＝灰色空心圓、文字 60% 不透明）、
標題＋小字說明、右側「完成／進行中」。下方 4 px 進度條（`p.Accent`）。無按鈕、不可關閉視窗。

| 步驟 | 說明 | 何時出現 |
|---|---|---|
| 結束執行中的 MemTidy | 先把被調整過的程式恢復原狀 | MemTidy 正在執行（`MemTidy.exe --exit`，最多等 10 秒） |
| 複製程式檔 | `C:\Program Files\MemTidy\MemTidy.exe` | 一定 |
| 確認檔案完整 | SHA-256 和安裝檔一致才換上新檔 | 一定 |
| 設定開機時自動啟動 | 以你的帳號，不需系統管理員 | 開關開啟 |
| 啟動 MemTidy | 以一般權限執行 | 開關開啟 |

「等待確認」在「結束 MemTidy」之後、「複製程式檔」之前出現。

### 4. 完成（done）

置中綠色圓形白勾、標題、內文：

- 有啟動：標題「MemTidy 已在系統匣執行」，內文「記憶體寬裕時它什麼都不做；開始吃緊時，才會讓閒置的背景程式讓出資源，切回去立刻恢復。」，
  下方小卡「圖示顏色＝記憶體壓力，左鍵開面板、右鍵開選單」。
- 沒啟動：標題「MemTidy 已安裝」，內文「可從開始功能表啟動 MemTidy。」
  （已實作：安裝時在你帳號的開始功能表建立 `MemTidy.lnk`，移除時刪除；`install.ps1` 相同。）
- 按鈕：完成（Primary）。

### 5. 錯誤（error）

標題「無法安裝 MemTidy」（更新時「無法更新 MemTidy」），副標一律是「你的電腦沒有被修改」，置中橘色圓形驚嘆號。
對應 `install.ps1` 系統管理員步驟的結束代碼：

| 情況 | 內文標題 | 說明 | 按鈕 |
|---|---|---|---|
| 確認視窗按「否」（`Start-Process` 例外） | 沒有安裝任何東西 | Windows 確認視窗按了「否」。MemTidy 要寫入 Program Files，需要你同意一次系統管理員權限。 | 取消／🛡 再試一次 |
| 代碼 2：雜湊不一致 | 安裝已中止，沒有寫入任何檔案 | 複製到 Program Files 的檔案和安裝檔不一致，可能在安裝過程中被更換。請重新建置或下載後再試。下方等寬字顯示預期／實際 SHA-256（前 4 + 後 4 碼）。 | 結束 |
| 代碼 3：檔案使用中 | MemTidy 的檔案仍在使用中 | 「查看快取內容」視窗可能還開著。關閉它之後按「再試一次」，已完成的步驟不會重做。 | 取消／🛡 再試一次 |
| 代碼 1／其他 | 安裝失敗 | 系統管理員步驟失敗（結束代碼 N）。詳細記錄在 `%LOCALAPPDATA%\MemTidy\memtidy.log`。 | 結束 |

「你的電腦沒有被修改」必須是真的：Run 機碼要在系統管理員步驟成功之後才寫，失敗時不寫。
若「結束 MemTidy」已做、之後失敗，結束前要把原本在執行的 MemTidy 重新啟動。

### 6. 移除確認（confirmRemove）→ 移除完成（removed）

- 標題「移除 MemTidy」，副標 `C:\Program Files\MemTidy`。
- 說明：「會先結束 MemTidy 並把被調整過的程式恢復原狀，再移除程式檔和開機啟動設定。」
- 開關「同時刪除設定與紀錄檔」，小字 `%LOCALAPPDATA%\MemTidy（排除清單也在這裡）`，**預設關閉**。
  下方：「保留的話，之後重新安裝會沿用你的排除清單和其他設定。」
  → 新功能：`install.ps1 -Uninstall` 目前一律保留，請一併加 `-RemoveSettings` 參數讓兩邊一致。
- 按鈕：取消／🛡 移除（紅色 Primary，`p.Red`）。
- 進度步驟：結束 MemTidy → 移除程式檔 → 移除開機時自動啟動 →（開關開啟）刪除設定與紀錄檔。
- 完成畫面：標題「已移除 MemTidy」，副標依開關「程式、開機啟動與設定都已刪除」或「設定與紀錄檔仍保留在 %LOCALAPPDATA%\MemTidy」，
  內文「所有被調整過的程式都已恢復原狀。」

## 文案原則

- 每個需要系統管理員的動作都說明「為什麼」。
- 錯誤先講結果（電腦有沒有被改），再講原因，最後講怎麼做。
- 不顯示版本號：`MemTidy.csproj` 還沒有 `<Version>`。若要在更新畫面顯示「舊版 → 新版」，先加 `<Version>`，
  再用 `FileVersionInfo` 讀已安裝檔案的版本。

## 測試參數（建議）

比照現有 `--snapshot-*`：`--install --snapshot-installer=檔名.png --installer-step=welcome|progress|done|error|confirmRemove`
`--installer-fail=cancel|hash|inuse`，用 `--demo` 時不做任何實際變更，方便拍 README 截圖。

## 不在範圍內

- .NET 10 Desktop Runtime 檢查：缺少時 `MemTidy.exe` 根本打不開，安裝視窗也看不到。若要處理，需要另一個不依賴
  .NET 的啟動器或在 `install.ps1` 檢查，請先提出做法再實作。
