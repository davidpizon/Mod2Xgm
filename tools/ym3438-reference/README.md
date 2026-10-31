# ym3438-reference

Makes the reference outputs for the YM3438 probes in [`tests/golden/ym3438/`](../../tests/golden/ym3438/README.md)
([ADR 0056](../../docs/adr/0056-ym3438-model.md)): plays every probe script through **ymfm** (the code the converter's
model is ported from) and **Nuked-OPN2** (a cycle-accurate model of the chip, the independent check), and writes
`<probe>.ymfm.gz` and `<probe>.nuked.gz` next to each script.

It runs by hand, once after a probe changes. CI and the tests only read the checked-in files.

```bash
bash tools/ym3438-reference/run.sh
```

Needs bash, curl, tar, gzip and a C++17 `g++` (on Windows, MinGW-w64 on `PATH`, e.g. from Git Bash). Set `CXX` to
use another compiler. The script downloads both projects at pinned commits into `.cache/` (git-ignored), builds
`reference.cpp` against them, and plays the scripts.

## What `reference.cpp` does

- **ymfm** (`ymfm::ym3438`, commit `81aec25`): resets the chip, writes each register through its address and data
  ports, and generates one sample per `s` step. No timers or interface callbacks.
- **Nuked-OPN2** (YM3438 mode, commit `335747d`): one sample is 24 internal clocks; the output is the sum of the pins
  over those clocks. Writes take time on the real chip, so each run of writes is scheduled to finish on the sample
  where the script puts it: the address, one clock, the data, then 1 clock for system registers or 24 for `$28`,
  channel and operator registers. Setup writes before the first sample run in a pre-roll whose output is thrown
  away. The pre-roll starts a whole period of Nuked's 12-bit envelope timer, plus two samples, before the first sample:
  the timer then wraps (from 4095 to 1, skipping 0; [ADR 0063](../../docs/adr/0063-ym3438-chip-differences.md)) on the
  first sample, as ymfm's envelope counter leaves 0, and both models' envelopes step on the same samples from there.

## Licenses

`reference.cpp` and `run.sh` are MIT, like the rest of the repository. ymfm is BSD-3-Clause; Nuked-OPN2 is LGPL-2.1.
Neither is in the repository: they are downloaded, built into a local executable that is never shipped, and none of
their code is copied into the converter (see [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)).
