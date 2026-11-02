#!/bin/sh
# Build and run the synthetic trigger checks against built app assemblies.
#   GSBIN = folder with ApexHUD.Native.Overlays/Common.dll (default: the Overlays Release output; build it first:
#           dotnet build src/ApexHUD.Native.Overlays/... -c Release -p:EnableWindowsTargeting=true)
#   BEFORE=1 builds the variant for the pre-fix assemblies (no sound-gate section) to compare.
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/run"
EXTRA=""
[ -n "$BEFORE" ] && EXTRA="-p:DefineConstants=BEFORE" && OUT="bin/run_before"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" $EXTRA -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo $EXTRA -o "$OUT" >/dev/null; fi
"$DOTNET" "$OUT/trigger_check.dll" "$@"
