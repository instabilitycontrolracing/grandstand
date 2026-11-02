#!/bin/sh
# Build and run the session review preview against built app assemblies.
#   GSBIN = folder with ApexHUD.Native.Overlays/Common/Data.dll (default: the Overlays Release output; build it first:
#           dotnet build src/ApexHUD.Native.Overlays/... -c Release -p:EnableWindowsTargeting=true)
#   ASSETS = an assets folder (fonts, maps, logos/tracks) copied next to the tool
#   PVHOME = HOME for the run (Documents/iRacing/Grandstand/terrain is read from there)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/run"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$OUT" >/dev/null; fi
[ -n "$ASSETS" ] && mkdir -p "$OUT/assets" && cp -r "$ASSETS"/. "$OUT/assets/"
HOME="${PVHOME:-$HOME}" "$DOTNET" "$OUT/session_review_preview.dll" "$@"
