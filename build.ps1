# Builds dist\MemTidy.exe (single file, needs the .NET 10 Desktop Runtime).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# Ask a running copy to quit cleanly (restores every app it adjusted) so the exe can be replaced.
if (Get-Process MemTidy -ErrorAction SilentlyContinue) {
    if (Test-Path dist\MemTidy.exe) { & dist\MemTidy.exe --exit }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process MemTidy -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
}
dotnet publish MemTidy.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item dist\MemTidy.exe | Select-Object Name, Length, LastWriteTime
