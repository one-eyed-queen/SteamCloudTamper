# install.ps1 - universal mount helper for SteamCloudSave.dll
#
# One command mounts SCT's main binary (the DLL) into whatever loader you have.
# It auto-detects the most likely target and installs accordingly:
#
#   PowerShell -ExecutionPolicy Bypass -File install.ps1
#   PowerShell -ExecutionPolicy Bypass -File install.ps1 -GameDir "D:\SteamLibrary\steamapps\common\MyGame"
#   PowerShell -ExecutionPolicy Bypass -File install.ps1 -Mode load_dlls|shim|ost|auto -AppId 91330
#
# The DLL autodetects steamPath + appid + shadowRoot at runtime, so in most cases
# this script only needs to place the DLL where the loader looks for it.

param(
    [ValidateSet('auto','load_dlls','shim','ost')]
    [string]$Mode = 'auto',

    [string]$GameDir = '',
    [string]$AppId = '',
    [string]$ShadowRoot = '',
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$script:Here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:Dll64  = Join-Path $Here 'SteamCloudSave.dll'
$script:cfgAppData = Join-Path $env:LOCALAPPDATA 'SCT'

function Write-Step($m) { if (-not $Silent) { Write-Host "  * $m" } }
function Bail($m)      { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $Dll64)) {
    Write-Host "SteamCloudSave.dll not found next to install.ps1 - build it first:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\build.ps1"
    exit 1
}

# ---------------------------------------------------------------------------
# Locate Steam (same registry order as SteamLocator.cs)
# ---------------------------------------------------------------------------
function Get-SteamPath {
    $candidates = @(
        (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath,
        (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath,
        (Get-ItemProperty 'HKLM:\SOFTWARE\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath,
        'C:\Program Files (x86)\Steam'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

$Steam = Get-SteamPath
if (-not $Steam) { Bail "Could not locate a Steam install. Pass one via -SteamPath." }
Write-Step "Steam: $Steam"

# ---------------------------------------------------------------------------
# Resolve the game folder / appid
# ---------------------------------------------------------------------------
if ($GameDir) {
    if (-not (Test-Path $GameDir)) { Bail "GameDir not found: $GameDir" }
    if (-not $AppId) {
        $appidFile = Join-Path $GameDir 'steam_appid.txt'
        if (Test-Path $appidFile) { $AppId = (Get-Content $appidFile | Select-Object -First 1).Trim() }
    }
}

if ($AppId -and -not ($AppId -match '^\d+$')) { Bail "AppId must be numeric, got: $AppId" }

# ---------------------------------------------------------------------------
# Write the appdata config (overrides everything; the DLL also autodetects)
# ---------------------------------------------------------------------------
function Write-Config {
    New-Item -ItemType Directory -Force -Path $script:cfgAppData | Out-Null
    $lines = @(
        "# SteamCloudSave config (written by install.ps1)",
        "steamPath=$Steam"
    )
    if ($AppId)          { $lines += "appid=$AppId" }
    if ($ShadowRoot)     { $lines += "shadowRoot=$ShadowRoot" }
    $cfg = Join-Path $script:cfgAppData 'steamcloudsave.cfg'
    $lines | Set-Content -Path $cfg -Encoding Ascii
    Write-Step "Config -> $cfg"
}

# ---------------------------------------------------------------------------
# Decide + perform the mount
# ---------------------------------------------------------------------------
function Mount-Shim {
    if (-not $GameDir) { Bail "SHIM mode needs -GameDir (the game folder that holds steam_api64.dll)" }
    $api64 = Join-Path $GameDir 'steam_api64.dll'
    if (-not (Test-Path $api64)) { Bail "No steam_api64.dll in $GameDir - cannot SHIM" }
    $backup = "$api64.orig"
    $isOurs = $false
    if (Test-Path $Dll64) {
        $oursHash = (Get-FileHash $Dll64 -Algorithm SHA256).Hash
        $currentHash = (Get-FileHash $api64 -Algorithm SHA256).Hash
        $isOurs = ($oursHash -eq $currentHash)
    }
    if ($isOurs) {
        # steam_api64.dll is already our shim (from a previous run); real one is .orig — just re-copy.
        Write-Step "SHIM: $api64 is already SteamCloudSave.dll (skipping backup)"
    }
    elseif (-not (Test-Path $backup)) {
        Copy-Item $api64 $backup
        Write-Step "Backed up real steam_api64.dll -> .orig"
    }
    Copy-Item $Dll64 $api64 -Force
    Write-Step "SHIM: $api64 <= SteamCloudSave.dll (real backed up to .orig)"
}

function Mount-LoadDlls {
    if (-not $GameDir) {
        Bail "load_dlls mode needs -GameDir (game folder with steam_settings)"
    }
    $destDir = Join-Path $GameDir 'steam_settings\load_dlls'
    $appidTxt = Join-Path $GameDir 'steam_settings\appid.txt'
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }
    if ($AppId -and -not (Test-Path $appidTxt)) {
        New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'steam_settings') | Out-Null
        Set-Content -Path $appidTxt -Value $AppId -Encoding Ascii
        Write-Step "Wrote steam_settings\appid.txt = $AppId"
    }
    Copy-Item $Dll64 (Join-Path $destDir 'SteamCloudSave.dll') -Force
    Write-Step "load_dlls: $destDir\SteamCloudSave.dll"
}

function Mount-Ost {
    $toml = Join-Path $Steam 'opensteamtool.toml'
    $absDll = $Dll64.Replace('\','\\')
    $block = @"
# SteamCloudSave <-> OpenSteamTool merge (auto-generated by install.ps1)
[cloud]
enabled = true
library = "cloud_redirect.dll"

[inject]
# enable to load SteamCloudSave.dll into every launched game
enabled = false
# library_x64 = "$absDll"
# library_x86 = "$absDll"
"@
    if (-not (Test-Path $toml)) {
        Write-Step "No $toml yet; creating a [cloud] host template."
        $block | Set-Content -Path $toml -Encoding Ascii
    } elseif (-not (Select-String -Path $toml -Pattern '\[inject\]' -Quiet)) {
        # Never clobber an existing OST config — just append the block.
        Add-Content -Path $toml -Value "`n$block`n" -Encoding Ascii
        Write-Step "ost: appended [cloud]/[inject] block to $toml (existing config preserved)"
    } else {
        Write-Step "ost: $toml already has [inject]; leaving it untouched (edit manually if you want the shadow lane)"
    }
    Write-Step "ost: set [inject].enabled=true and uncomment library_x64/x86 to enable the shadow lane"
}

# ---------------------------------------------------------------------------
# Resolve auto mode from what's on disk
# ---------------------------------------------------------------------------
if ($Mode -eq 'auto') {
    if ($GameDir -and (Test-Path (Join-Path $GameDir 'steam_api64.dll'))) {
        $Mode = 'shim'
    } elseif ($GameDir) {
        $Mode = 'load_dlls'
    } else {
        $Mode = 'ost'
    }
    Write-Step "auto -> $Mode"
}

Write-Config

switch ($Mode) {
    'shim'      { Mount-Shim }
    'load_dlls' { Mount-LoadDlls }
    'ost'       { Mount-Ost }
}

Write-Host ""
Write-Host "Done. The DLL autodetects steam path, appid and shadow root at runtime;"
Write-Host "you only need to configure anything if you want a custom shadow root or auto-park."
Write-Host "Default shadow root: %LOCALAPPDATA%\SCT\shadow\<appid>"
Write-Host "Real cloud parking (upload to UFS) is done by the SCT CLI:  SCT park <appid> --lane rpc"
