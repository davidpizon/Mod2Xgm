# ADR 0066: The PCM driver models follow SGDK's real XGM and XGM2 drivers, run on an emulated Z80 (T7.9)

- **Status:** Accepted; closes the known differences of [ADR 0058](0058-pcm-driver-models.md#known-differences-from-the-drivers)
  (1–3 measured, fixed or recorded; 4 recorded; 5 left for a DefleMask export)
- **Date:** 2026-10-31

## Context

[ADR 0058](0058-pcm-driver-models.md) built the XGM, XGM2 and DMF DAC models from reading the drivers' source, and
listed five places where that reading might not be what the drivers do: the beat wobble, the start delay of their
buffers, when `$2B` changes, an XGM sound that starts and ends within one block, and DefleMask's own DAC playback.
Plan task T7.9 measures them against the real drivers, then fixes or records each. The goal is the most accurate
sound, so where a model and its driver disagree, the driver wins (David: "copy the driver"; where a behavior is too
messy to copy, measure it and write it down).

There is no SGDK player ROM yet (T10.2), and DefleMask does not run in the cloud machine this task was done on.

## Decision

- **A reference tool runs SGDK's real drivers on an emulated Z80** (David), `tools/pcm-driver-reference`, like the
  YM3438 and SN76489 reference tools: run by hand, never shipped, its captures checked in.
  - **The driver bytes are the ones SGDK ships** (David): `drv_xgm.o` and `drv_xgm2.o` from `lib/libmd.a` at SGDK
    commit `9a78a44` (MIT), the `.rodata` of each object. No assembler, so nothing can differ from what a game gets.
  - **The Z80 is floooh's `chips/z80.h`** (David), commit `9e88298`, zlib license, cycle-exact. Downloaded by
    `run.sh`, not in the repository, used only by the tool.
  - **The machine is what the drivers touch:** 8 KB of Z80 RAM; the YM2612's ports, with Timer A and its flag (XGM2
    paces its beats with it) and every `$2A` / `$2B` write logged with its time; the bank register and the 68000
    window, each access costing an average of **3 Z80 cycles** of bus wait, the figure Genesis Plus GX uses from
    hardware measurements (**Verify** on hardware); the PSG port (ignored); the VBlank interrupt once per NTSC frame,
    held for one line (XGM2 counts its frames with it).
  - **The 68000's side is SGDK's**, done instantly: `Z80_loadDriverInternal`, `XGM_startPlay` / `XGM2_load` +
    `XGM2_playTrack`, and `XGM_nextXFrame` from the VBlank for XGM. The bus requests' few cycles are not modeled.
  - **The songs are compiled as SGDK compiles them.** XGM, as xgmtool makes `.xgc`: a length byte per frame, command
    bytes doubled for the driver's jump table, 3 empty frames in front and every PCM command moved 3 frames earlier on
    NTSC (xgmtool's `XGC_shiftSamples`: "because of PCM buffer length"). XGM2, as xgm2tool makes it: streams packed in
    chunks of literal blocks ending in `$00` (the driver always unpacks), PCM commands where they are. Captures are
    timed from the VBlank of the song's frame 0.
- **Probes** (`tests/golden/pcm-drivers/`, built by `PcmDriverProbes`): start sweeps for each driver (24 XGM sounds
  stepping 60 YM3438 samples through its blocks, 12 XGM2 sounds), loud mixes that clip, a half-speed sound, a stop.
- **The models are buffered by default** (`SoftwarePcmDriver`, `buffered: true`): a play or a stop is held until the
  beat the driver's buffer puts it on, and done there, its priority check included. `buffered: false` keeps the old
  mixer-only timing for the mixer's unit tests. What changed, each measured below:
  1. **XGM starts on the first 256-beat block at least 70 beats after the song frame** (`XgmPcmDriver.StartLeadBeats`).
  2. **XGM2 starts 443 beats after the frame** (`Xgm2PcmDriver.StartDelayBeats`), the mean of its buffer's fill level.
  3. **XGM2 plays the first 64 beats of every sound twice** (`Xgm2PcmDriver.RepeatedBeats`; 32 bytes at half speed).
     After a play the driver's next fill sees the play's "parameters changed" flag and, as `updatePCM` is written,
     drops its own advance (`; don't update addr/len`), so the next fill reads the same 64 bytes. The flag is there for
     a play that lands mid-fill; a music play is done at the VBlank, before the next fill, so it hits every sound.
     The sound also ends 64 beats later.
  4. **XGM2's DAC goes on 173 beats before the sound** (`DacLeadBeats`) **and off at the end of the first 64-beat
     block in which nothing plays**, not three blocks after: the driver decides `$2B` for the block it mixes, ahead of
     what is heard.
  5. **An XGM add that clips costs 11 Z80 cycles**, the driver's overflow fix (`LD A, C` / `ADC $FF` in
     `readAndMix2`), and every beat after it is that much later. A loud mix plays slow and flat: four channels
     clipping on every beat fall from 14 087 to 13 545 Hz (3.8 %, 67 cents).
- **The beat wobble is not modeled**: its pattern depends on what the driver's code is doing at each beat. It stays
  under one YM3438 sample on XGM and three on XGM2.

### Measured

| | XGM | XGM2 |
|---|---|---|
| Beat, idle (model) | 14 087 Hz (14 093, −0.04 %) | 13 330–13 345 Hz (13 317, +0.1 %) |
| Wobble (largest step off the usual) | 0.98 YM3438 samples ("too soon" / "too late") | 3.0 samples, around the VBlank interrupt |
| Start, from the song frame | first block ≥ 247–280 samples later: 0.35–1.37 frames over the probe | 1 682–1 865 samples (1.89–2.10 frames); model 443 beats ± 23 |
| First bytes of a sound | as written | first 64 beats twice, on 12 of 12 plays |
| `$2B` on | 1.18 frames *before* the song frame | 0.73–0.78 frames before the sound |
| `$2B` off | 1.8–2.3 frames after the sound | 29–77 beats after the sound or the stop |
| Clipping | +11 cycles per clipped add: 4 loud channels 13 545 Hz | 3 loud channels 13 218 Hz (−0.9 %) |

The tests (`PcmDriverProbeTests`): XGM's starts follow the block rule on the driver (in its own block phase, ± 2
samples) and on the model; XGM2's are within 30 beats; the mixed values match the driver's while a sound plays alone
or the mix clips (all four XGM channels together); idle beat rates within 0.2 %; XGM's clipping slowdown within
0.3 % of the driver's beat count; XGM2's `$2B` changes within 256 samples; the wobble within its limits.

Without the wait states, XGM's idle beat would be 14 813 Hz: the driver's hand-counted 254 cycles include them.

### What is left

- **XGM's `$2B` comes on early** (1.18 frames before the song frame): the model gets a play on its frame and can't
  look ahead, so it turns the DAC on at the play. FM6 is reserved for the DAC in XGM, so this is silent; T11.1
  confirms FM6 is off-limits.
- **A play racing a fill (XGM2).** In a frame with plays on all three channels, channel 0's fill had already started
  with the old parameters, so it started one fill (64 beats) late instead of repeating its first 64. Which way a play
  goes depends on where the driver's fills are; the model always repeats, what 12 of 12 single plays did. The
  `xgm2-mix` probe starts its sounds two frames apart for that reason.
- **XGM2's clipping slowdown** is not modeled: the driver waits for Timer A, so it loses a beat only when the work
  runs past a period, which isn't linear in the clips.
- **XGM2's start jitter** (± 23 beats with the buffer's fill level) is not modeled; the model uses the mean.
- **ADR 0058's difference 4** (an XGM sound that starts and ends within one block) only touches `$2B`, which is
  silent for XGM (above); not probed.
- **DefleMask's own DAC playback** (difference 5) stays **Verify**. To check it on a PC with DefleMask:
  1. Open `tests/golden/dmf-probes/dac-sample.dmf` in DefleMask.
  2. File → Export → WAV, 44.1 kHz, 16-bit, stereo, no loop; save it as `tests/golden/dmf-probes/wav/dac-sample.wav`.
  3. Run `dotnet test --project tests/Mod2Xgm.Harness.Tests`: `DefleMaskDacExportTests` compares the export's first
     beep with `DmfDac` playing the same sample (it skips until the file is there). Its 3 dB bar is a first guess;
     set it from the first export, and remove this item's **Verify** once it passes.
- **The bus wait** (3 cycles a 68000 access, from Genesis Plus GX) is **Verify** on hardware; the XGM start rule and
  the XGM2 delay were measured with it. The 68000's bus requests (a few cycles per frame) and DMA are not modeled.

## Options considered

- **Run SGDK's drivers on an emulated Z80, copy what they do into the models (chosen).** Exact for the driver's code;
  needs nothing but a C++ compiler; checks every behavior with probes.
- **Wait for emulator captures of the SGDK player ROM (T10.2).** Closer to hardware (a real 68000, VDP and bus), but
  the ROM doesn't exist yet and BlastEm / Genesis Plus GX captures need a desktop; the emulated Z80 now, the ROM later.
- **Assemble the drivers from source** (sjasmplus): the same code, but an assembler other than SGDK's sjasm might
  differ; the shipped bytes can't.
- **z80ex** (Ubuntu package): GPL-2, which would need David's approval even for a test tool; `z80.h` is zlib.
- **Run the real driver in the converter** (a C# Z80 and SGDK's driver bytes). Every timing exact, the wobble and
  races included, but a Z80 core to port and far slower; recorded as a possibility if the rules above aren't enough.
- **Keep the models as they were and record the differences.** Simplest, but XGM2's doubled starts, the start delays
  and the clipping slowdown are all audible.

## Consequences

- The Genesis renderer's XGM and XGM2 renders (`--render-wav` with `--format xgm` / `xgm2`, the optimizer later) hear
  PCM when SGDK's drivers play it: later against FM and PSG (XGM2 by about 2 frames), XGM2's starts doubled, XGM's
  loud mixes slower. The DMF model is unchanged.
- The XGM2 writer (T10.1) should know that every PCM play is heard about 2 frames late and with its first 64 beats
  twice; moving PCM commands ahead, as xgmtool does for XGM, or adding the first 64 bytes' worth of silence, are for
  that task to decide (docs/formats/xgm.md#driver-behavior-measured).
- `THIRD-PARTY-NOTICES.md` lists SGDK's `libmd.a` and floooh's `z80.h` (test tool only, downloaded, not in the
  repository). AGENTS.md lists `z80.h` with the other reference-tool libraries.
- Changing a probe means: `UPDATE_GOLDEN=1 dotnet test` to rewrite the scripts, then `bash tools/pcm-driver-reference/run.sh`.
