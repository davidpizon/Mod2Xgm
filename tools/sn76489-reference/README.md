# sn76489-reference

Makes the reference outputs for the SN76489 probes in [`tests/golden/sn76489/`](../../tests/golden/sn76489/README.md)
([ADR 0057](../../docs/adr/0057-sn76489-model.md)): plays every probe script through **MAME's Sega PSG** (the code the
converter's model is ported from) and **Nuked-PSG** (a gate-level model of the YM7101 VDP's PSG, the independent
check), and writes `<probe>.mame.gz` and `<probe>.nuked.gz` next to each script.

It runs by hand, once after a probe changes. CI and the tests only read the checked-in files.

```bash
bash tools/sn76489-reference/run.sh
```

Needs bash, curl, gzip and a C++17 `g++` (on Windows, MinGW-w64 on `PATH`, e.g. from Git Bash). Set `CXX` to use
another compiler. The script downloads MAME's `sn76496.cpp` and `sn76496.h` and Nuked-PSG's `ympsg.c` and `ympsg.h`
at pinned commits into `.cache/` (git-ignored), builds `reference.cpp` against them, and plays the scripts.

## What `reference.cpp` does

- **MAME** (`segapsg_device`, commit `d066f16`): `sn76496.cpp` compiles unchanged against `shim/emu.h`, the least of
  MAME's device framework it needs; the shim's stream keeps the integers the chip puts in it. MAME's stream runs at
  clock ÷ 2 and the chip steps every 8th stream sample, so each output is the stream sample right after a step, and
  writes land between steps.
- **Nuked-PSG** (commit `d15a168`): clocked at the PSG clock and read after every 16th clock, ×8192 as its
  `YMPSG_Generate` does. A write needs 2 clocks to land, so each run of writes is scheduled to finish on the sample
  where the script puts it. The chip ignores writes for about 30 clocks after reset, so the pre-roll (whose output is
  thrown away) starts 1024 clocks before the first write.

## Licenses

`reference.cpp`, `shim/emu.h` and `run.sh` are MIT, like the rest of the repository. MAME's `sn76496.cpp` is
BSD-3-Clause; Nuked-PSG is GPL-2.0-or-later (David approved it for this tool only). Neither is in the repository:
they are downloaded, built into a local executable that is never shipped, and none of Nuked-PSG's code is copied
into the converter (see [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)).
