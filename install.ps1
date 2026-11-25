<#
.SYNOPSIS
    把 MemTidy 安裝到 C:\Program Files\MemTidy，或用 -Uninstall 移除。

.DESCRIPTION
    只有系統管理員能修改 Program Files 裡的檔案，所以 MemTidy 唯一以系統管理員執行的部分（「快取內容」視窗，
    要先在 Windows 確認視窗同意）不會被你帳號下的其他程式掉包，旁邊也放不進冒名的檔案。MemTidy 本身仍以一般權限執行。

    這個腳本只在「複製或刪除程式檔」這一步要求一次系統管理員權限，其餘都以你的身分執行。複製前先記下
    dist\MemTidy.exe 的 SHA-256，系統管理員步驟只安裝內容完全相同的檔案。系統管理員步驟的程式碼（AdminStep.ps1）
    在要求權限之前就讀進來，直接放在命令列裡交給 Windows，不會在你同意之後才去讀一個可能被改過的腳本檔。

    雙擊 dist\MemTidy.exe 開啟的安裝視窗做的事和這個腳本相同（同一份 AdminStep.ps1，建置時嵌入）。

.EXAMPLE
    .\install.ps1               # 需要時先建置，安裝或更新，設定開機時自動啟動、開始功能表捷徑，並立即啟動
.EXAMPLE
    .\install.ps1 -Uninstall    # 結束並移除；設定與紀錄檔保留在 %LOCALAPPDATA%\MemTidy
.EXAMPLE
    .\install.ps1 -Uninstall -RemoveSettings   # 同上，並刪除設定與紀錄檔（包括排除清單）
.EXAMPLE
    .\install.ps1 -WhatIf       # 只顯示會做什麼，不做任何變更
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Uninstall, [switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MemTidy'
$installed = Join-Path $installDir 'MemTidy.exe'
$dist = Join-Path $PSScriptRoot 'dist\MemTidy.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MemTidy.lnk'
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MemTidy'

# The administrator step (copy or delete the program file, nothing else), read now, before any consent is asked for.
$adminStep = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AdminStep.ps1') -Raw

function Stop-MemTidy {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    if (-not (Get-Process MemTidy -ErrorAction SilentlyContinue)) { return }
    $exe = @($installed, $dist) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $exe) { return }
    if ($PSCmdlet.ShouldProcess('MemTidy', '結束執行中的 MemTidy（會先還原被調整的程式）')) {
        & $exe --exit
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Process MemTidy -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    }
}

function Invoke-AdminStep {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Mode, [string]$What, [string]$Sha256 = '')
    if (-not $PSCmdlet.ShouldProcess($installDir, $What)) { return }
    $quote = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $command = "& { $adminStep } -Mode $(& $quote $Mode) -Source $(& $quote $dist) -Sha256 $(& $quote $Sha256)"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    try {
        $p = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru `
                           -ArgumentList "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded"
    }
    catch { throw '已取消：需要在 Windows 確認視窗按「是」才能繼續。' }
    switch ($p.ExitCode) {
        0 { }
        2 { throw '安裝中止：複製出去的檔案和 dist\MemTidy.exe 不一致（可能在安裝過程中被更換）。' }
        3 { throw 'MemTidy 的檔案仍在使用中（例如「快取內容」視窗還開著），請關閉後再試一次。' }
        default { throw "系統管理員步驟失敗（結束代碼 $($p.ExitCode)）。" }
    }
}

if ($Uninstall) {
    Stop-MemTidy
    Invoke-AdminStep -Mode 'uninstall' -What '移除 MemTidy（系統管理員步驟）'
    if ($PSCmdlet.ShouldProcess('開機時自動啟動與開始功能表捷徑', '移除')) {
        Remove-ItemProperty -Path $runKey -Name MemTidy -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
    }
    if ($RemoveSettings) {
        if ((Test-Path -LiteralPath $dataDir) -and $PSCmdlet.ShouldProcess($dataDir, '刪除設定與紀錄檔')) {
            Remove-Item -LiteralPath $dataDir -Recurse -Force
        }
        if (-not $WhatIfPreference) { Write-Host '已移除 MemTidy，設定與紀錄檔也已刪除。' }
    }
    elseif (-not $WhatIfPreference) { Write-Host "已移除 MemTidy。設定與紀錄檔仍在 $dataDir，不需要可加 -RemoveSettings 或自行刪除。" }
    return
}

if (-not (Test-Path -LiteralPath $dist)) {
    if ($WhatIfPreference) { Write-Host '假設：先執行 build.ps1 建置 dist\MemTidy.exe，再安裝。'; return }
    Write-Host '找不到 dist\MemTidy.exe，先建置…'
    & (Join-Path $PSScriptRoot 'build.ps1')
    if (-not (Test-Path -LiteralPath $dist)) { throw '建置失敗，沒有產生 dist\MemTidy.exe。' }
}
$hash = (Get-FileHash -LiteralPath $dist -Algorithm SHA256).Hash

Stop-MemTidy
Invoke-AdminStep -Mode 'install' -What "安裝或更新 $installed（系統管理員步驟）" -Sha256 $hash

# Start at sign-in from the installed copy, with your normal rights, and start it now.
if ($PSCmdlet.ShouldProcess('開機時自動啟動', "指向 $installed")) {
    Set-ItemProperty -Path $runKey -Name MemTidy -Value "`"$installed`""
}
if ($PSCmdlet.ShouldProcess($shortcut, '建立開始功能表捷徑')) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = $installed
    $lnk.WorkingDirectory = $installDir
    $lnk.Description = 'MemTidy 記憶體整理'
    $lnk.Save()
}
if ($PSCmdlet.ShouldProcess($installed, '以一般權限啟動 MemTidy')) {
    Start-Process -FilePath $installed
    Write-Host "MemTidy 已安裝到 $installDir，並以一般權限啟動。"
}
