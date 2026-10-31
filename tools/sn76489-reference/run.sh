#!/usr/bin/env bash
# Rebuilds the SN76489 reference outputs in tests/golden/sn76489/ (docs/adr/0057-sn76489-model.md):
# downloads MAME's PSG source and Nuked-PSG at pinned commits, builds tools/sn76489-reference/reference.cpp with g++,
# plays every probe script through both, and writes <probe>.mame.gz and <probe>.nuked.gz next to the scripts.
#
# Run from anywhere: bash tools/sn76489-reference/run.sh
# Needs bash, curl, gzip and a C++17 g++ (MinGW-w64 on Windows). Set CXX to use another compiler.
set -euo pipefail

MAME_COMMIT=d066f16134121f02a2fd9716582a17f8ee66f6d5
NUKED_COMMIT=d15a168c676f4669e23660be9225b34ad7c1764e

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
probes="$repo/tests/golden/sn76489"
cache="$here/.cache"
cxx="${CXX:-g++}"

fetch() { # url file
  if [ ! -f "$2" ]; then
    echo "Downloading $1"
    mkdir -p "$(dirname "$2")"
    curl -sSfL "$1" -o "$2"
  fi
}

# Only MAME's two PSG files: the whole repository is far too big to download for them.
mame="$cache/mame-$MAME_COMMIT"
for file in sn76496.cpp sn76496.h; do
  fetch "https://raw.githubusercontent.com/mamedev/mame/$MAME_COMMIT/src/devices/sound/$file" "$mame/$file"
done

nuked="$cache/nuked-psg-$NUKED_COMMIT"
for file in ympsg.c ympsg.h; do
  fetch "https://raw.githubusercontent.com/nukeykt/Nuked-PSG/$NUKED_COMMIT/$file" "$nuked/$file"
done

exe="$cache/reference"
echo "Building $exe"
"$cxx" -x c -O2 -c "$nuked/ympsg.c" -o "$cache/ympsg.o"
"$cxx" -O2 -std=c++17 -I "$here/shim" -I "$mame" -I "$nuked" -static \
  "$here/reference.cpp" "$mame/sn76496.cpp" "$cache/ympsg.o" \
  -o "$exe"

for script in "$probes"/*.txt; do
  name="$(basename "$script" .txt)"
  echo "Playing $name"
  "$exe" "$script" "$cache/$name.mame.raw" "$cache/$name.nuked.raw"
  gzip -n -9 -c "$cache/$name.mame.raw" > "$probes/$name.mame.gz"
  gzip -n -9 -c "$cache/$name.nuked.raw" > "$probes/$name.nuked.gz"
done
