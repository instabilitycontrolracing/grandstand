#!/bin/sh
# Build and run the race-opener bench against built app assemblies.
#   GSBIN = folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the Overlays Release output;
#           build it first: dotnet build src/ApexHUD.Native.GpuHost/... -c Release -p:EnableWindowsTargeting=true)
#   OUT   = bench output folder name under bin/ (default: run) - use different ones for a before and an after build
# Arguments go to the bench: --label L --sheet PNG --frames DIR --scale 2 --csv FILE | --compare A B
# HOME / XDG_DATA_HOME point into the bench folder while it runs, so nothing is written outside the repo
# (GlobeIntro caches per-track dots under LocalApplicationData).
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/${OUT:-run}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$OUT" >/dev/null; fi
mkdir -p home/.local/share home/Documents
BENCH="$(pwd)"
# run from inside home/ too: an empty "My Documents" resolves relative to the working folder on Linux
cd home
HOME="$BENCH/home" XDG_DATA_HOME="$BENCH/home/.local/share" XDG_DOCUMENTS_DIR="$BENCH/home/Documents" DOTNET_CLI_HOME="$BENCH/home" \
    "$DOTNET" "$BENCH/$OUT/intro_bench.dll" "$@"
