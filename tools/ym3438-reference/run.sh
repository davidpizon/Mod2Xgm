#!/usr/bin/env bash
# Rebuilds the YM3438 reference outputs in tests/golden/ym3438/ (docs/adr/0056-ym3438-model.md):
# downloads ymfm and Nuked-OPN2 at pinned commits, builds tools/ym3438-reference/reference.cpp with g++, plays every
# probe script through both, and writes <probe>.ymfm.gz and <probe>.nuked.gz next to the scripts.
#
# Run from anywhere: bash tools/ym3438-reference/run.sh
# Needs bash, curl, tar, gzip and a C++17 g++ (MinGW-w64 on Windows). Set CXX to use another compiler.
set -euo pipefail

YMFM_COMMIT=81aec25ccbb98f4873a255f7551ac4dadac59b4a
NUKED_COMMIT=335747d78cb0abbc3b55b004e62dad9763140115

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
probes="$repo/tests/golden/ym3438"
cache="$here/.cache"
cxx="${CXX:-g++}"

fetch() { # name url dir
  if [ ! -d "$3" ]; then
    echo "Downloading $1"
    mkdir -p "$3"
    curl -sSfL "$2" | tar xz -C "$3" --strip-components=1
  fi
}

fetch ymfm "https://github.com/aaronsgiles/ymfm/archive/$YMFM_COMMIT.tar.gz" "$cache/ymfm-$YMFM_COMMIT"
fetch Nuked-OPN2 "https://github.com/nukeykt/Nuked-OPN2/archive/$NUKED_COMMIT.tar.gz" "$cache/nuked-$NUKED_COMMIT"

ymfm="$cache/ymfm-$YMFM_COMMIT/src"
nuked="$cache/nuked-$NUKED_COMMIT"
exe="$cache/reference"
echo "Building $exe"
"$cxx" -x c -O2 -c "$nuked/ym3438.c" -o "$cache/ym3438.o"
"$cxx" -O2 -std=c++17 -I "$ymfm" -I "$nuked" -static \
  "$here/reference.cpp" "$ymfm/ymfm_opn.cpp" "$ymfm/ymfm_adpcm.cpp" "$ymfm/ymfm_ssg.cpp" "$cache/ym3438.o" \
  -o "$exe"

for script in "$probes"/*.txt; do
  name="$(basename "$script" .txt)"
  echo "Playing $name"
  "$exe" "$script" "$cache/$name.ymfm.raw" "$cache/$name.nuked.raw"
  gzip -n -9 -c "$cache/$name.ymfm.raw" > "$probes/$name.ymfm.gz"
  gzip -n -9 -c "$cache/$name.nuked.raw" > "$probes/$name.nuked.gz"
done
