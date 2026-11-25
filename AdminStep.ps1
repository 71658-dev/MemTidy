# The administrator step of installing or removing MemTidy: copy or delete the program file, nothing else.
# Never run directly. install.ps1 and the installer window (MemTidy.exe, which embeds this file at build time) both
# read it BEFORE asking for administrator rights and hand its text to System32's powershell.exe as -EncodedCommand,
# so nothing is read from a user-writable place after the user has consented.
# It works out every path itself. Exit codes: 0 done, 1 failed, 2 the copy doesn't match the build, 3 a MemTidy file is in use.
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
