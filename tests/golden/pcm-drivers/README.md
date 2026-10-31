# PCM driver probes

Songs of PCM commands that check the converter's XGM and XGM2 driver models (`src/Render/Pcm/`, plan T7.9,
[ADR 0066](../../../docs/adr/0066-pcm-drivers-checked-against-sgdk.md)) against SGDK's real Z80 drivers, and what the
drivers send to the DAC for each.

| Probe | What it plays |
|---|---|
| `xgm-starts` | XGM: 24 short sounds 13 frames apart, each landing 60 YM3438 samples further into the driver's 256-beat blocks |
| `xgm-mix` | XGM: four loud sounds in one frame (the mix clips), then a lower-priority play and stop (ignored) and a stop |
| `xgm2-starts` | XGM2: 12 short sounds 7 frames apart |
| `xgm2-mix` | XGM2: three loud sounds 2 frames apart, one at half speed; then a lower-priority play (ignored) |
| `xgm2-half` | XGM2: one sound at half speed, alone |
| `xgm2-stop` | XGM2: a long sound stopped after 5 frames |

## Files

- **`<probe>.txt`** — the script, built in code by `PcmDriverProbes` (`tests/Mod2Xgm.Tests/Render/Pcm/`). One step per
  line: `driver xgm|xgm2`; `sample id length step` (byte i is `(int8)(uint8)((i + 1) × step)`); `play channel id
  priority [half]`; `stop channel priority`; `frame count` ends the frame and waits `count` frames; `#` starts a comment.
- **`<probe>.sgdk.gz`** — every `$2A` and `$2B` write SGDK's driver makes playing it on the emulated Z80
  (`tools/pcm-driver-reference`): gzipped 8-byte records, int32 time in master clocks from the VBlank of the song's
  frame 0, then the register and the value, then 2 zero bytes.

## The checks (`PcmDriverProbeTests`)

- The checked-in script is the one `PcmDriverProbes` builds, and reads back the same.
- XGM: the driver starts every sound on the first of its blocks at least 70 beats after the sound's frame, in its own
  block phase; the model does the same in its phase. XGM2: the model starts every sound within 30 beats of the driver.
- The mixed values match the driver's while a sound plays alone or all of XGM's channels clip together, XGM2's
  doubled first 64 beats included.
- Beat rates: idle within 0.2 % of the driver's; XGM's clipping slowdown within 0.3 % of the driver's beat count.
- XGM2's `$2B` changes within 256 YM3438 samples of the driver's; the drivers' beat wobble within its limits.

## Updating

After changing a probe:

1. `UPDATE_GOLDEN=1 dotnet test --filter FullyQualifiedName~PcmDriverProbeTests` rewrites the scripts.
2. `bash tools/pcm-driver-reference/run.sh` remakes the captures (needs a C++17 g++ and `ar`; on Windows, MinGW-w64
   on `PATH`). See [its README](../../../tools/pcm-driver-reference/README.md).
3. Run the tests again, and look at any changed measurement before moving a tolerance.
