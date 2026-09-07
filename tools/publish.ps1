# Builds the ONE self-contained SteamCloudTamper binary into dist/ (gitignored).
# Double-click = TUI, flags = CLI, no .NET runtime needed.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-c Release] [-r win-x64|linux-x64]
#   -r linux-x64 produces SteamCloudTamper (ELF) for Linux Steam + the SLS-era
#      buckets; the C sidecar (SteamCloudSave.dll) stays Windows-only.

param(
    [ValidateSet("Release", "Debug")] [string] $c = "Release",
    [ValidateSet("win-x64", "linux-x64")] [string] $r = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root "dist\$r"

# The repo needs a .NET 10 SDK; it lives at a custom path on this machine and is
# usually NOT on PATH - prefer the bundled dotnet, fall back to whatever dotnet resolves.
$candidate = Join-Path $env:USERPROFILE "dotnet10\dotnet.exe"
if (Test-Path $candidate) { $dotnet = $candidate } else { $dotnet = "dotnet" }

Write-Host "publishing $c single-file self-contained -> $out" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\SteamCloudTamper") `
    -c $c -r $r --self-contained `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -o $out

if (-not $?) { exit 1 }

$exe = if ($r -eq "linux-x64") { Join-Path $out "SteamCloudTamper" } else { Join-Path $out "SteamCloudTamper.exe" }
if (-not (Test-Path $exe)) { Write-Error "publish did not produce $exe"; exit 1 }

$size = (Get-Item $exe).Length / 1MB
Write-Host "OK: $exe ($([math]::Round($size, 1)) MB)"
Write-Host "  double-click  -> TUI"
Write-Host "  exe <command> -> CLI (--cli forces it, --tui forces TUI, --version prints info)"