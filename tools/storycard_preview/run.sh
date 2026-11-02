#!/bin/sh
# Story card layouts preview (#349) - Linux. Renders every story card in classic + the four right-third layouts.
#   GSBIN       folder holding ApexHUD.Native.Overlays/Common/Data.dll (default: the GpuHost Release output; build it
#               first: dotnet build src/ApexHUD.Native.GpuHost -c Release -p:EnableWindowsTargeting=true)
#   GS_ASSETS   folder laid out like assets/ (fonts/inter, fonts/barlow-condensed, fonts/cormorant-garamond, maps/,
#               logos/...). Missing fonts / logos fall back (the layout still reads; the look is not final).
#   GS_LANDMARKS a CrewChief trackLandmarksData.json (corner names), optional
#   OUT         output folder (default /mnt/user-data/outputs/card_layouts_real)
# Arguments: [--only RaceSoFar,PitStops] [--layouts classic,flip] [--noalloc] [--fixes  (#398/#399 frames only)] [--tweaks (#410-#412)] [--michelin (#409, #422; GS_MICHELIN_RUN=frames folder)] [--themes (#424)] [--slanted (square-corner preview off)]
# Arguments: [--only RaceSoFar,PitStops] [--layouts classic,flip] [--noalloc] [--mini]
#   --radio (#517): each regular card with a real radio line coming in and going out (full -> split -> full), a card that
#   starts with the radio up, and a 200 ms blip check (frames.txt per card)
#   --mini (#339): every card as a mini battle card over a real battle frame (OUT=/mnt/user-data/outputs/mini_cards)
# Without Angelo's michelin.png a plain grey stand-in (it says LOGO) is put in this tool's own home folder so the
# Michelin tower draws its logo branch and MICHELIN on the pylon; on his PC (run.ps1) his own picture is used.
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
# linked files in real folders, so the stand-in pictures (placeholder logos / cars) land in this copy only
mkdir -p bin/run/assets && cp -rs "$(cd "${GS_ASSETS:-$(pwd)/../../assets}" && pwd)"/. bin/run/assets/
mkdir -p home/Documents
HOME="$(pwd)/home" DOTNET_CLI_HOME="$(pwd)/home" "$DOTNET" bin/run/storycard_preview.dll "${OUT:-/mnt/user-data/outputs/card_layouts_real}" --placeholder-logos "$@"
