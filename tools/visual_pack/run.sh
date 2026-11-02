#!/bin/sh
# Build and run the visual-pack preview against built app assemblies.
#   GSBIN     folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the GpuHost Release output; build it
#             first: dotnet build src/ApexHUD.Native.GpuHost/... -c Release -p:EnableWindowsTargeting=true)
#   GS_ASSETS folder laid out like assets/ (fonts/inter, fonts/barlow-condensed, fonts/cormorant-garamond, maps/...)
#   GS_BG     folder with 3840x2160 background pictures (bg_trees.png, bg_crowd.png, bg_pits.png, bg_straight.png)
#   GS_IBT    the Le Mans race .ibt (W11 runs on its real sun / heading telemetry)
#   OUT       output folder (default ./out)
# Windows: run.ps1 (same harness, your Documents\iRacing\Grandstand and the build's assets).
# Arguments: --only V1,W12   --novideo   --nokeys   --noalloc   --dirbench
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
ln -s "${GS_ASSETS:-$(pwd)/../../assets}" bin/run/assets
mkdir -p home/Documents
HOME="$(pwd)/home" DOTNET_CLI_HOME="$(pwd)/home" "$DOTNET" bin/run/visual_pack.dll "$@"
