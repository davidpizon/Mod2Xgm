# ADR 0058: PCM driver models: XGM, XGM2 and the DMF DAC as DAC register streams, from the drivers' source (T7.4)

- **Status:** Accepted
- **Date:** 2026-10-30

## Context

Plan task T7.4: the optimizer has to hear what its PCM candidates sound like when a Genesis plays them, inside the
converter and in pure C# ([ADR 0010](0010-multi-pass-optimizer-sgdk-compliance-pure-csharp.md)). There are three
players:

- **XGM (v1)**: SGDK's `src/snd/drv_xgm.s80` mixes up to four channels of 8-bit signed PCM on the Z80 and sends the
  sum to the YM2612's DAC in place of FM channel 6.
- **XGM2**: SGDK's `src/snd/xgm2/drv_xgm2.s80` does the same with three channels, at full or half speed.
- **The DMF DAC**: DefleMask plays one sample at a time on FM6 at the sample's own rate
  ([formats/dmf.md](../formats/dmf.md#dac-samples-on-fm6)).

The plan asked for "tests vs emulator captures". There are no XGM/XGM2 writers yet (M10, M11), so there are no files
to capture, and the SGDK player ROM is T10.2.

What the drivers do, read from SGDK at commit `9a78a44` (MIT):

| | XGM | XGM2 |
|---|---|---|
| Beat (one byte to the DAC) | every 254 Z80 cycles: master clock ÷ 3810, **14 093 Hz** NTSC / 13 964 Hz PAL; not tied to the FM chip's samples | YM Timer A at 1020: every **4 YM3438 samples**, 13 317 Hz NTSC / 13 195 Hz PAL |
| Mixing | channels 0–3 added in order, each sum stuck at +127 / −128 (`JP PO` on overflow, then `$7F`/`$80`), then `+$80` | the same over channels 0–2 |
| Half speed | — | each byte written twice (6.65 kHz) |
| Length | address and size in 256-byte steps: a sample plays to the end of its padding | the same |
| Priority | 0–15; a play or a stop wins at **≥** the playing priority; back to 0 at the sound's end; a stop sets 0 | music plays at 7 (`$1x` bit 3 clear) or 15 (set); a stop always works |
| `$2B` (DAC on) | checked after every 256-beat block: on while a channel plays, then on for 3 more blocks (`YM_2B_CNT`), then the music's own value | checked after every 64-beat block: on if a channel played in it (`DAC_ENABLED_CNT` = 4, counted down once in the same block), so on for 3 blocks after the last one with sound |
| Silence | `$80` | `$80` |

And DefleMask, as Furnace plays a DMF (`src/engine/platform/genesis.cpp`; Furnace sets `brokenDACMode` and
`noOPN2Vol` for every DMF), which matched DefleMask's own exports of the DAC probes: on every chip sample the
sample's rate is added to a counter, and each time it passes the chip's rate (`chipClock / 144` in whole hertz) the
next point goes to the DAC as `data16 >> 8`. Between points, and after the end, the DAC keeps its byte. A note turns
`$2B` on; a note-off or the sample's end turns it off; `17xx` sets it. No volume.

## Decision

- **The models output the DAC registers, not sound** (David). `IDacSource.Generate(data, enable)` gives, for every
  YM3438 output sample, the byte in `$2A` and in `$2B`. The Genesis renderer (T7.5) writes them into `Ym3438`, which
  already models the DAC: its 9-bit value, channel 6's panning and the level against the FM channels. A test plays
  the bytes through `Ym3438` to check the hand-off.
- **`$2B` is modeled too** (David), with each driver's rule above, so T7.5 can write it as the driver does.
- **The checks are tests built from the drivers' source** (David), not captures: every expected value in
  `tests/Mod2Xgm.Tests/Render/Pcm/` comes from reading the Z80 code (and Furnace's, for DMF). Checking against the
  real driver is new plan task **T7.9**.
- **Even beats** (David). XGM's beats are exactly 3810 master clocks apart (3.78 YM3438 samples), XGM2's exactly 4
  samples. A play lands on the first beat after it. The drivers' small timing wobble and their buffering delay are
  not modeled (known differences 1 and 2).
- **The DMF model is DefleMask's playback** (David), what someone editing the DMF hears, not what rescomp makes of
  the VGM export (which would be the XGM model at 14 kHz).
- `src/Render/Pcm/`: `SoftwarePcmDriver` (mixing, padding, priority, blocks), `XgmPcmDriver`, `Xgm2PcmDriver`,
  `DmfDac`. Samples come in as the sample processor makes them: `sbyte[]` for XGM/XGM2, `short[]` for DMF.
- No code is copied: SGDK's driver is Z80 assembly, modeled from what it does; Furnace (GPL) is read only, as
  for the DMF spec.

## Known differences from the drivers

T7.9 measures each against the real driver, then fixes or keeps it.

1. **The beat wobble.** XGM's mixing loop sends some bytes 1 to 10 Z80 cycles early or late ("too soon" / "too late"
   in `drv_xgm.s80`) and catches up; XGM2's beat waits for Timer A but is sent when the code gets to it. The model's
   beats are even. The wobble is under 3 µs, far above hearing, but it adds a little noise.
2. **The start delay.** Both drivers mix into a ring buffer ahead of the DAC, so a play is heard one to a few blocks
   after its frame (XGM: up to about 3 × 256 beats, 54 ms; XGM2 was rewritten to cut this). The model plays from the
   next beat. That decides where PCM sits against FM and PSG notes.
3. **When `$2B` changes.** The drivers decide `$2B` for the block they are mixing, which is heard later; the model
   turns the DAC on at the play and off after the counted blocks, on its own timeline.
4. **XGM v1 and a sound that starts and ends within one block.** Read literally, the driver could find no channel
   playing at that block's check and leave `$2B` off for it. The model turns the DAC on with every play.
5. **DefleMask itself.** The DMF model follows Furnace; DefleMask's own WAV export was not captured sample by sample.
   **Verify** with a DefleMask export of a DAC probe.

## Options considered

- **DAC register streams into `Ym3438`, tests from the source, even beats, DefleMask playback (chosen).**
- **Finished sound at the driver's rate.** Simpler to test alone, but T7.5 would have to redo the DAC's 9-bit step,
  panning and level against FM.
- **Run SGDK's Z80 driver in an emulated Z80 in a reference tool**, like `tools/ym3438-reference`. Bit-exact timing,
  but the largest job; moved to T7.9 as one way to check.
- **BlastEm / Genesis Plus GX captures of SGDK test ROMs.** Needs the SGDK toolchain and emulators and hand-made XGM
  files before the writers exist; also a T7.9 option.
- **Model the wobble and the buffer delay now.** Only checkable with one of the two options above.
- **Model the DMF after rescomp (XGM at 14 kHz).** Closer to a cartridge, but then the DMF model is the XGM one, and
  the editor plays something else.
- **Leave `$2B` out.** Music never uses FM6, so DAC off and DAC on at 0 sound the same; but the DMF's DAC turns off at
  every sample end, and T7.5 would have to guess.

## Consequences

- T7.5 drives one `IDacSource` per song, calls `Play` / `Stop` (and `SetDacMode` for DMF) at the right YM3438
  sample, and writes the `$2A` / `$2B` streams into `Ym3438` before each sample.
- The exact rates matter for the sample processor: XGM samples should be resampled to 14 093 Hz (NTSC) and XGM2's to
  13 317 / 6 658 Hz; "14 kHz" would be 11 cents flat. Recorded in [formats/xgm.md](../formats/xgm.md).
- Saturating, ordered mixing means loud overlaps distort differently from a plain sum: the optimizer hears that, and
  pre-mixed PCM seams ([ADR 0052](0052-pre-mixed-pcm.md), T8.5) are measured with these models.
- New plan task T7.9 checks the models against SGDK's real driver.
