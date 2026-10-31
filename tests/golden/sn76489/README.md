# SN76489 probes

Write scripts that check the converter's PSG model (`src/Render/Psg/`, plan T7.3,
[ADR 0057](../../../docs/adr/0057-sn76489-model.md)), and what MAME and Nuked-PSG play for each.

| Probe | What it plays |
|---|---|
| `tone` | Tone 1 alone at 440 Hz, the loudest volume, then off |
| `volumes` | Tone 2 at 330 Hz through all sixteen volumes |
| `periods` | Tone 3 at periods from `$3FF` down to 1 and 0, then the period changed one byte at a time during the note |
| `chord` | A chord on the three tones at different volumes, then all four channels at the loudest volume |
| `white-noise` | White noise at rates 0–2, then following tone 3 at two periods, then tone 3's period changed |
| `periodic-noise` | The same with periodic noise |
| `noise-restart` | The noise register written again every 2 ms (each write restarts the noise), then the noise at every volume |
| `write-quirks` | Data bytes after a volume, noise and period latch; a latch byte alone |
| `power-on-latch` | A data byte before any latch byte, which goes where the latch points after power-on |

## Files

- **`<probe>.txt`** — the script, built in code by `Sn76489Probes` (`tests/Mod2Xgm.Tests/Render/Psg/`). One step per
  line: `w value` writes a byte to the PSG port (hex), `s count` plays that many samples (decimal), `#` starts a
  comment. A sample is 16 PSG clocks, 223 722 Hz on NTSC.
- **`<probe>.mame.gz`** — what MAME's Sega PSG (`segapsg_device` in `src/devices/sound/sn76496.cpp`) plays, at commit
  `d066f16`.
- **`<probe>.nuked.gz`** — what Nuked-PSG plays, at commit `d15a168`: its output read after every 16th clock, ×8192.

Both outputs are gzipped little-endian int32s, one per sample. MAME's are 0 down to −32 764 (its Sega PSG negates the
output); Nuked's are 0 up to 32 768.

## The checks (`Sn76489ProbeTests`)

- The checked-in script is the one `Sn76489Probes` builds.
- The port plays every probe sample for sample as MAME does, except the probes a chip difference the port models
  reaches ([ADR 0065](../../../docs/adr/0065-sn76489-chip-differences.md)): `periods` and `write-quirks` (the tone
  counters), `chord`, `white-noise`, `periodic-noise` and `write-quirks` (the noise's timing). Those must play
  differently from MAME and come closer to Nuked-PSG than MAME does. A new difference needs its own entry in
  `Sn76489ProbeTests.ChipDifferences`.
- The port is within a log-mel distance of Nuked-PSG, after averaging every four samples: 0.3 dB, or a probe's own
  bar where the port still differs from the chip on purpose: MAME's volume levels (`volumes`, `noise-restart`,
  `chord`) and MAME's power-on latch (`power-on-latch`)
  ([ADR 0065](../../../docs/adr/0065-sn76489-chip-differences.md#what-is-left)).

## Updating

After changing a probe:

1. `UPDATE_GOLDEN=1 dotnet test --filter FullyQualifiedName~Sn76489ProbeTests` rewrites the scripts.
2. `bash tools/sn76489-reference/run.sh` remakes both outputs for every script (needs a C++17 g++; on Windows,
   MinGW-w64 on `PATH`). See [its README](../../../tools/sn76489-reference/README.md).
3. Run the tests again, and look at any Nuked distance that changed before moving a bar.
