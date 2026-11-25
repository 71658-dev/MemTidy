# Builds dist\MemTidy.exe (single file, needs the .NET 10 Desktop Runtime). ReadyToRun: precompiled, so the first
# panel open does not wait for the JIT.
# To install it, or update the installed copy in C:\Program Files\MemTidy, run .\install.ps1 afterwards.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dist = Join-Path $PSScriptRoot 'dist\MemTidy.exe'

# Only a copy running from dist\ locks the file being rebuilt: ask it to quit cleanly (it restores every app it
# adjusted) and start it again afterwards. An installed copy keeps running.
$fromDist = @(Get-Process MemTidy -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $dist) })
if ($fromDist) {
    & $dist --exit
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process MemTidy -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $dist }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
}

# The .NET CLI sends usage telemetry to Microsoft unless opted out, and checks nuget.org for workload updates about
# once a day: turn both off for this build, and leave the calling session's own settings as they were.
$savedOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$savedWorkloadCheck = $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
try {
    dotnet publish MemTidy.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o dist
}
finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $savedOptOut
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = $savedWorkloadCheck
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($fromDist) { Start-Process -FilePath $dist }
Get-Item $dist | Select-Object Name, Length, LastWriteTime
if (Test-Path (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MemTidy\MemTidy.exe')) {
    Write-Host 'To update the installed copy in Program Files, run .\install.ps1'
}
