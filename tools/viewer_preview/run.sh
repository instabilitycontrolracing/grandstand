#!/bin/sh
# #494 viewer cards + themes previews at 1920x1080, from a built app (Linux or Windows).
#   GSBIN     folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the GpuHost Release output)
#   GS_ASSETS folder laid out like assets/ (default: the repo's assets)
#   GS_BG     a background picture (any size; scaled to 1920x1080) - default: a flat grey-green
# Usage: run.sh [outDir] [--tiers] [--bench|--noclips]   (--tiers: the #576 / #603 tiered looks + thank-you roll at 3840x2160 - stills, mp4 clips
#   with their stingers, edge cases, timelines; --bench: the 4K frame cost of each look; needs ffmpeg)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
ln -s "${GS_ASSETS:-$(pwd)/../../assets}" bin/run/assets
mkdir -p home/Documents home/.local/share   # (LocalApplicationData: the thank-you roll file stays in here)
HOME="$(pwd)/home" "$DOTNET" bin/run/viewer_preview.dll "${1:-out}" "${GS_BG:-}" ${2:-} ${3:-} ${4:-}
