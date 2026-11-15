# Builds TixlWebHost and installs it to %LOCALAPPDATA%\TiXL\WebHost,
# which is where the TiXL "HtmlRenderer" operator looks for it (override with the TIXL_WEBHOST environment variable).
# Close TiXL before re-running, otherwise the running host executable cannot be overwritten.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $env:LOCALAPPDATA "TiXL\WebHost"

Write-Host "Publishing TixlWebHost to $target ..."
dotnet publish (Join-Path $here "WebHost.csproj") -c $Configuration -r win-x64 --self-contained false -o $target
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

Write-Host "Done. Installed: $(Join-Path $target 'TixlWebHost.exe')"
