#!/bin/sh
# Corner theme previews, base vs refined (2026-10-27, #115 / #118).
#   GSBIN     folder with ApexHUD.Native.Overlays/Common.dll (default: the Overlays Release output; build it first:
#             dotnet build src/ApexHUD.Native.Overlays/... -c Release -p:EnableWindowsTargeting=true)
#   GS_ASSETS folder laid out like assets/ (fonts/inter, fonts/barlow-condensed, fonts/cormorant-garamond, logos/manufacturers)
#   PVHOME    HOME for the run: Documents/iRacing/Grandstand/corner_logos is read from there (Angelo's own pictures)
#   OUT       output folder (default ./out)        extra args: --each (one PNG per corner and variant)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
[ -n "$GS_ASSETS" ] && ln -s "$GS_ASSETS" bin/run/assets
HOME="${PVHOME:-$HOME}" "$DOTNET" bin/run/corner_preview.dll --out "${OUT:-out}" "$@"
