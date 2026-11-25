# 排除清單視窗：與設計稿的差異

設計稿：<https://claude.ai/artifact/6oShtg8C1onG3Ve4hbfeom>（「排除清單」頁），原始檔 `source/Exclusions.dc.html`。

`ExclusionsForm`／`AddAppForm`（commit 5bd9602）已實作大部分設計：標題、搜尋、全部／執行中／未執行篩選、
建議加入、清單、移除、新增、還原預設、在 settings.json 中編輯。以下是還沒做到的部分，依建議順序排列。

## 1. 移除後可以「復原」

設計：按列上的移除鈕後，視窗下方中央出現浮動提示「已移除 Spotify　[復原]」，5 秒後消失。
按「復原」把該程式加回清單（`settings.Exclude(exe)`）。還原預設清單後也用同一個提示，「復原」回到還原前的清單。

- 提示：膠囊形，`p.GroupedCard` 底＋陰影，文字 `Theme.Body`，右側小型一般按鈕「復原」，距視窗底部 64 px。
- 同時只顯示一個，新的取代舊的。
- 有了復原之後，`RestoreDefaults()` 的確認對話框可以拿掉（或保留，請自行判斷，並說明理由）。

## 2. 「系統保護的程式」區塊

設計：清單卡片下方 14 px，另一張卡片，一列可點擊、可展開（預設收合）：

- 左：灰色圖示方塊（`p.Gray`）＋盾牌圖示。
- 標題「系統保護的程式」，小字「64 個程式名稱，另依發行者與安裝資料夾判斷 · 無法移除」。
- 右：箭頭，展開時轉 90°。
- 展開後每組一列（左側縮排對齊文字），標題「組名 · 個數」，下方一行範例名稱（超出以 … 截斷）：

| 組 | 個數 | 來源 |
|---|---|---|
| Windows 核心 | 34 | `ProcessNapper.SystemExclusions` 的 Windows 段 |
| 資安軟體 | 9 | 同上 Security 段 |
| 憑證簽章 | 5 | 同上 Certificate signing 段 |
| 音訊 | 4 | 同上 Audio 段 |
| 輸入法與協助工具 | 12 | 同上 Input methods 段 |
| 依發行者與安裝資料夾 | — | `ProtectedPublishers`、`ProtectedFolders` |

個數請從程式裡的集合計算，不要寫死。若要分組，建議把 `SystemExclusions` 改成具名分組的資料結構（例如
`(string Group, string[] Names)[]`），`IsExcluded` 用攤平後的集合，行為不變。

目的：讓使用者知道防毒、HiPKI／醫事憑證、輸入法本來就不會被動到，不需要自己加。

## 3. 和系統保護重複的項目

`rtkauduservice64.exe` 同時在預設排除清單（`Settings.cs`）和 `SystemExclusions`（`ProcessNapper.cs`）裡。設計：這類列在狀態左側
顯示灰底膠囊標籤「系統已保護，可移除」。另外建議直接把它從 `DefaultExclusions` 拿掉。

## 4. 建議卡片的說明文字

截圖 `docs/screenshots/exclusions-light.png` 中，說明文字被截成一行（「…可能在背景播放或視…」）。設計是最多兩行、完整顯示，
卡片高度隨內容調整（目前固定 84 px）。

## 不需要改的地方

- 「新增程式」做成獨立視窗（設計稿是視窗內的面板）：對桌面程式來說合理，保留。
- `AddAppForm` 用一個搜尋框兼手動輸入，沒有做設計稿的「從執行中的程式選擇／手動輸入檔名」分段切換：更簡單，保留。
- 執行中但正在休眠的程式顯示「休眠中」（靛色圓點）：設計稿沒有，但比設計好，保留。
