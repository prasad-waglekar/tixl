# Builds TixlWebHost and installs it to %LOCALAPPDATA%\TiXL\WebHost.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

# Ensure no lingering TixlWebHost processes lock binaries during overwrite
Get-Process -Name "TixlWebHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $env:LOCALAPPDATA "TiXL\WebHost"

if (!(Test-Path $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

Write-Host "Publishing TixlWebHost ($Configuration, win-x64) to $target ..."
dotnet publish (Join-Path $here "WebHost.csproj") -c $Configuration -r win-x64 --self-contained false -o $target
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

Write-Host "Done. Installed: $(Join-Path $target 'TixlWebHost.exe')"
