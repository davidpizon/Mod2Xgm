# pcm-driver-reference

Makes the reference captures for the PCM driver probes in [`tests/golden/pcm-drivers/`](../../tests/golden/pcm-drivers/README.md)
([ADR 0066](../../docs/adr/0066-pcm-drivers-checked-against-sgdk.md)): plays every probe script through **SGDK's real
XGM or XGM2 Z80 driver** on an emulated Z80 and writes every `$2A` / `$2B` write it makes to `<probe>.sgdk.gz` next to
the script.

It runs by hand, once after a probe changes. CI and the tests only read the checked-in files.

```bash
bash tools/pcm-driver-reference/run.sh
```

Needs bash, curl, `ar`, gzip and a C++17 `g++` (on Windows, MinGW-w64 on `PATH`, e.g. from Git Bash). Set `CXX` to
use another compiler. The script downloads SGDK's `lib/libmd.a` and floooh's `chips/z80.h` at pinned commits into
`.cache/` (git-ignored), extracts `drv_xgm.o` and `drv_xgm2.o`, builds `reference.cpp`, and plays the scripts.

## What `reference.cpp` does

- **The drivers** are the `.rodata` of `drv_xgm.o` / `drv_xgm2.o` from SGDK's `libmd.a` (commit `9a78a44`): the bytes
  every SGDK game ships.
- **The machine:** the Z80 (`z80.h`, commit `9e88298`) with 8 KB of RAM; the YM2612's ports with Timer A and its
  status flag, the `$2A` / `$2B` writes logged; the bank register and the 68000 window (each access 3 Z80 cycles of
  bus wait, as Genesis Plus GX has it); the PSG port, ignored; the VBlank interrupt once per NTSC frame (262 × 3420
  master clocks), held for one line.
- **The 68000's side** as SGDK's `xgm.c` / `xgm2.c` do it: load the driver, wait for its ready bit, set the sample
  table, the song and the play command; for XGM, add a pending frame at every VBlank.
- **The songs** are built from the script as SGDK's tools compile them: XGM as xgmtool's `.xgc` (frame length bytes,
  doubled command bytes, 3 lead frames, PCM commands 3 frames ahead), XGM2 with packed streams (literal blocks).

## Licenses

`reference.cpp` and `run.sh` are MIT, like the rest of the repository. SGDK is MIT; `z80.h` is zlib. Neither is in
the repository: they are downloaded and built into a local executable that is never shipped (see
[`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)).
