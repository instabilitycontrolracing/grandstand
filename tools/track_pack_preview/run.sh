#!/bin/sh
# Track pack preview (2026-10-28) - Linux. Per track: corner_labels.png (every named stop as the onboard label with its
# theme + heritage line), track_card_*.png + track_card_contact.png (story, facts, lap walk), stops.txt.
#   GSBIN       folder with ApexHUD.Native.Overlays/Common/Data.dll (default: the Overlays Release output)
#   GS_ASSETS   folder laid out like assets/ (fonts/...)
#   GS_LANDMARKS a CrewChief trackLandmarksData.json (corner names and positions) - required for the corners
#   PVHOME      HOME for the run (Documents/iRacing/Grandstand/corner_logos read from there)
#   OUT         output root (default ./out)      extra args: track slugs (mexico suzuka silverstone sebring monza)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
if [ -n "$GSBIN" ]; then "$DOTNET" build -c Release -v q --nologo -p:GsBin="$GSBIN" -o bin/run >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o bin/run >/dev/null; fi
rm -rf bin/run/assets
[ -n "$GS_ASSETS" ] && ln -s "$GS_ASSETS" bin/run/assets
HOME="${PVHOME:-$HOME}" "$DOTNET" bin/run/track_pack_preview.dll "${OUT:-out}" "$GS_LANDMARKS" "$@"
