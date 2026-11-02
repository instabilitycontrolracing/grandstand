#!/bin/sh
# Build and run the highlight reel plan checks against the built Common assembly.
#   GSBIN = folder with ApexHUD.Native.Common.dll (default: the Common Release output; build it first:
#           dotnet build src/ApexHUD.Native.Common -c Release -p:EnableWindowsTargeting=true)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/run"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$OUT" >/dev/null; fi
"$DOTNET" "$OUT/reel_check.dll" "$@"
