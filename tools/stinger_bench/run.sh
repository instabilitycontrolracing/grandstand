#!/bin/sh
# Build and run the class-transition bench (see Program.cs). Env:
#   StingerSrc = absolute path of the ProceduralStinger.cs to bench (default: the repo's)
#   OUT        = output folder name under bin/ (use different ones for a before and an after build)
#   GS_ASSETS  = a folder laid out like assets/ with fonts/ and logos/ (default: ../../assets)
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
OUT="bin/${OUT:-run}"
if [ -n "$StingerSrc" ]; then "$DOTNET" build -c Release -v q --nologo -p:StingerSrc="$StingerSrc" -o "$OUT" >/dev/null
else "$DOTNET" build -c Release -v q --nologo -o "$OUT" >/dev/null; fi
A="${GS_ASSETS:-$(pwd)/../../assets}"
rm -rf "$OUT/assets"; ln -s "$A" "$OUT/assets"
"$DOTNET" "$OUT/stinger_bench.dll" "$@"
