<#
.SYNOPSIS
    把 MemTidy 安裝到 C:\Program Files\MemTidy，或用 -Uninstall 移除。

.DESCRIPTION
    只有系統管理員能修改 Program Files 裡的檔案，所以 MemTidy 唯一以系統管理員執行的部分（「快取內容」視窗，
    要先在 Windows 確認視窗同意）不會被你帳號下的其他程式掉包，旁邊也放不進冒名的檔案。MemTidy 本身仍以一般權限執行。

    這個腳本只在「複製或刪除程式檔」這一步要求一次系統管理員權限，其餘都以你的身分執行。複製前先記下
    dist\MemTidy.exe 的 SHA-256，系統管理員步驟只安裝內容完全相同的檔案。系統管理員步驟的程式碼直接放在
    命令列裡交給 Windows，不會在你同意之後才去讀一個可能被改過的腳本檔。

.EXAMPLE
    .\install.ps1               # 需要時先建置，安裝或更新，設定開機時自動啟動，並立即啟動
.EXAMPLE
    .\install.ps1 -Uninstall    # 結束並移除；設定與紀錄檔保留在 %LOCALAPPDATA%\MemTidy
.EXAMPLE
    .\install.ps1 -WhatIf       # 只顯示會做什麼，不做任何變更
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MemTidy'
$installed = Join-Path $installDir 'MemTidy.exe'
$dist = Join-Path $PSScriptRoot 'dist\MemTidy.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# The administrator step: copy or delete the program file, nothing else. It works out every path itself.
# Exit codes: 0 done, 1 failed, 2 the copy doesn't match the build, 3 a MemTidy file is in use.
$adminStep = @'
param([string]$Mode, [string]$Source, [string]$Sha256)
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MemTidy'
$exe = Join-Path $dir 'MemTidy.exe'
$icacls = Join-Path ([Environment]::SystemDirectory) 'icacls.exe'
try {
    if ($Mode -eq 'uninstall') {
        if (Test-Path -LiteralPath $dir) {
            try { Remove-Item -LiteralPath $dir -Recurse -Force } catch { exit 3 }
        }
        exit 0
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # Program Files' own permissions: administrators change, everyone else only reads and runs.
    & $icacls $dir /reset /T /Q | Out-Null
    $staging = "$exe.new"
    Copy-Item -LiteralPath $Source -Destination $staging -Force
    if ((Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash -ne $Sha256) {
        Remove-Item -LiteralPath $staging -Force
        exit 2
    }
    try { Move-Item -LiteralPath $staging -Destination $exe -Force }
    catch { Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue; exit 3 }
    & $icacls $exe /reset /Q | Out-Null
    exit 0
}
catch { exit 1 }
'@

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
    Remove-ItemProperty -Path $runKey -Name MemTidy -ErrorAction SilentlyContinue
    if (-not $WhatIfPreference) { Write-Host "已移除 MemTidy。設定與紀錄檔仍在 $env:LOCALAPPDATA\MemTidy，不需要可自行刪除。" }
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
if ($PSCmdlet.ShouldProcess($installed, '以一般權限啟動 MemTidy')) {
    Start-Process -FilePath $installed
    Write-Host "MemTidy 已安裝到 $installDir，並以一般權限啟動。"
}
