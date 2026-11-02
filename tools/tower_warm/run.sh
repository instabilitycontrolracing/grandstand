#!/bin/sh
# Build and run the first-show tower bench (cold vs pre-warmed) against built app assemblies.
#   GSBIN     folder with ApexHUD.Native.Overlays/Common.dll (default: the Overlays Release output; build it first:
#             dotnet build src/ApexHUD.Native.Overlays/... -c Release -p:EnableWindowsTargeting=true)
#   GS_ASSETS folder laid out like assets/ (fonts/inter ..., logos/manufacturers, logos/classes, logos/iracing-brand,
#             logos/series) - on the PC: the real assets folder
# Runs both modes, each in its own process (JIT is per process). Extra args go to both (e.g. --png).
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
[ -n "$GS_ASSETS" ] && ln -s "$GS_ASSETS" bin/run/assets
"$DOTNET" bin/run/tower_warm.dll cold "$@"
"$DOTNET" bin/run/tower_warm.dll warm "$@"
