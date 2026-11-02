#!/bin/sh
# Build and run the steady race-frame bench against built app assemblies (GSBIN, default the GpuHost Release output).
#   ./run.sh [frames] [battle]
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/${OUT:-run}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$OUT" >/dev/null; fi
rm -rf "$OUT/assets"; ln -s "${GS_ASSETS:-$(pwd)/../../assets}" "$OUT/assets"
mkdir -p home/Documents
HOME="$(pwd)/home" DOTNET_CLI_HOME="$(pwd)/home" "$DOTNET" "$OUT/race_frame.dll" "$@"
