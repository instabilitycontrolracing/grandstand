#!/bin/sh
# Build and run the transition / car-panel preview. GS_FONTS = a folder laid out like assets/fonts
# (inter/Inter-SemiBold.ttf, barlow-condensed/BarlowCondensed-Black.ttf, ...); default: ../../assets/fonts.
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
"$DOTNET" build -c Debug -v q --nologo >/dev/null
OUT=bin/Debug/net8.0
mkdir -p "$OUT/assets"
FONTS="${GS_FONTS:-$(pwd)/../../assets/fonts}"
rm -rf "$OUT/assets/fonts"; ln -s "$FONTS" "$OUT/assets/fonts"
[ -d ../../assets/logos ] && { rm -rf "$OUT/assets/logos"; ln -s "$(pwd)/../../assets/logos" "$OUT/assets/logos"; }
"$DOTNET" "$OUT/transition_preview.dll"
