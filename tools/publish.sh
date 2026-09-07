#!/usr/bin/env bash
# Linux self-contained publish of the whole tool (TUI + CLI, one ELF) -> dist/linux-x64
# Usage:  ./tools/publish.sh [-c Release|Debug]
set -euo pipefail
c="Release"
while [[ $# -gt 0 ]]; do
  case "$1" in
    -c) c="$2"; shift 2;;
    *) echo "usage: $0 [-c Release|Debug]"; exit 1;;
  esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/dist/linux-x64"
dotnet=(dotnet)
if [[ -x "$HOME/dotnet10/dotnet" ]]; then dotnet=("$HOME/dotnet10/dotnet"); fi

echo "publishing $c single-file self-contained -> $out" >&2
"${dotnet[@]}" publish "$root/src/SteamCloudTamper" \
  -c "$c" -r linux-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -o "$out"

exe="$out/SteamCloudTamper"
if [[ ! -x "$exe" ]]; then echo "publish did not produce $exe" >&2; exit 1; fi
echo "OK: $exe"
echo "  ./SteamCloudTamper          -> TUI"
echo "  ./SteamCloudTamper <cmd>    -> CLI"
echo "The C sidecar (SteamCloudSave.dll) is Windows-only."