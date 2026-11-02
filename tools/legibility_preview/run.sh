#!/bin/sh
# #340 legibility previews: every on-air graphic at 1920x1080 over a TV picture, from a built app.
#   GSBIN     folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the GpuHost Release output)
#   GS_ASSETS folder laid out like assets/ (fonts/inter, fonts/barlow-condensed, fonts/cormorant-garamond, logos/...)
#   GS_BG     a background picture (any size; scaled to 1920x1080) - default: a flat grey-green
#   OUT       output folder (default ./out)
# Before/after: build the old commit into one folder and the new one into another, run this twice with each GSBIN
# and a different OUT, then compare the PNGs (same file names).
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
B="bin/run_$(echo "${GSBIN:-default}" | md5sum | cut -c1-8)"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o "$B" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$B" >/dev/null; fi
rm -rf "$B/assets"
[ -n "$GS_ASSETS" ] && ln -s "$GS_ASSETS" "$B/assets"
mkdir -p home/Documents
HOME="$(pwd)/home" "$DOTNET" "$B/legibility_preview.dll" "${OUT:-out}" "${GS_BG:-}"
