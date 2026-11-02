#!/bin/sh
# TV loading splash preview (Linux). GSBIN = built app (default GpuHost Release output), GS_ASSETS = assets folder,
# PVHOME = HOME for the run. Usage: run.sh <outDir> <map json | ->
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
ln -s "${GS_ASSETS:-$(pwd)/../../assets}" bin/run/assets
HOME="${PVHOME:-$HOME}" "$DOTNET" bin/run/splash_preview.dll "$@"
