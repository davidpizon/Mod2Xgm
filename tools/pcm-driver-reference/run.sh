#!/usr/bin/env bash
# Rebuilds the PCM driver reference captures in tests/golden/pcm-drivers/ (docs/adr/0066-pcm-drivers-checked-against-sgdk.md):
# downloads SGDK's lib/libmd.a (the built XGM and XGM2 Z80 drivers) and floooh's z80.h at pinned commits, builds
# tools/pcm-driver-reference/reference.cpp with g++, plays every probe script through the real driver on the emulated
# Z80, and writes <probe>.sgdk.gz next to the scripts.
#
# Run from anywhere: bash tools/pcm-driver-reference/run.sh
# Needs bash, curl, ar, gzip and a C++17 g++ (MinGW-w64 on Windows). Set CXX to use another compiler.
set -euo pipefail

SGDK_COMMIT=9a78a44143a867cd8c5053b95cd061a21f0b73c2
CHIPS_COMMIT=9e88298ce56319953ac7a43213a1120359f7a3a6

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
probes="$repo/tests/golden/pcm-drivers"
cache="$here/.cache"
cxx="${CXX:-g++}"

fetch() { # url file
  if [ ! -f "$2" ]; then
    echo "Downloading $1"
    mkdir -p "$(dirname "$2")"
    curl -sSfL "$1" -o "$2"
  fi
}

sgdk="$cache/sgdk-$SGDK_COMMIT"
fetch "https://raw.githubusercontent.com/Stephane-D/SGDK/$SGDK_COMMIT/lib/libmd.a" "$sgdk/libmd.a"
(cd "$sgdk" && ar x libmd.a drv_xgm.o drv_xgm2.o)

chips="$cache/chips-$CHIPS_COMMIT"
fetch "https://raw.githubusercontent.com/floooh/chips/$CHIPS_COMMIT/chips/z80.h" "$chips/z80.h"

exe="$cache/reference"
echo "Building $exe"
"$cxx" -O2 -std=c++17 -I "$chips" -static "$here/reference.cpp" -o "$exe"

for script in "$probes"/*.txt; do
  name="$(basename "$script" .txt)"
  echo "Playing $name"
  "$exe" "$sgdk" "$script" "$cache/$name.sgdk.raw"
  gzip -n -9 -c "$cache/$name.sgdk.raw" > "$probes/$name.sgdk.gz"
done
