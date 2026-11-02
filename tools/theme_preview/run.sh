#!/bin/sh
# #601 theme previews / Default pixel check / theme bench, from a built app (Linux or Windows + WSL).
#   GSBIN     folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the GpuHost Release output)
#   GS_ASSETS folder laid out like assets/ (default: the repo's assets)
#   OLD=1     build for assemblies from before #601 (only the "diff" mode)
# Usage: run.sh stills OUT BG.png [--custom themes.json] | video OUT.mp4 BG.png THEME [sec] [fps] | diff OUT | bench THEME [frames] [--nodraw]
# The background: python3 make_bg.py bg.png
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/${OUTBIN:-run}"
"$DOTNET" build -c Release -v q --nologo ${GSBIN:+-p:GsBin="$GSBIN"} ${OLD:+-p:OldBuild=1} -o "$OUT" >/dev/null
rm -rf "$OUT/assets"; ln -s "${GS_ASSETS:-$(pwd)/../../assets}" "$OUT/assets"
mkdir -p home/Documents
HOME="$(pwd)/home" DOTNET_CLI_HOME="$(pwd)/home" "$DOTNET" "$OUT/theme_preview.dll" "$@"
