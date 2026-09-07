# Build SteamCloudSave.dll (steam_api64 shim + RemoteStorage shadow lane) with GCC/MinGW-w64.
# Requires: gcc on PATH, or set GCC (e.g. $env:GCC = "...\mingw64\bin\gcc.exe").

$ErrorActionPreference = "Stop"
$exe = $env:GCC
if (-not $exe) { $exe = "gcc" }

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& $exe -shared -O2 -o "$dir\SteamCloudSave.dll" "$dir\SteamCloudSave.c" -static-libgcc -ladvapi32 -lshell32
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "built $dir\SteamCloudSave.dll"