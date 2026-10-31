# Genesis audio notes

## YM2612 (OPN2)
- 6 FM channels, 4 operators each, 8 algorithms. Two register ports (port 0: ch1–3, port 1: ch4–6).
- **DAC mode**: register `$2B` bit 7 replaces ch6 with an 8-bit DAC written via `$2A` (unsigned, centred
  at `$80`). Only one hardware PCM stream; drivers like XGM mix several in software on the Z80.
- **DAC flaw** (David, 2026-10-28; source he gave: [Genesis: Audio Chip Notes](https://consolemods.org/wiki/Genesis:Audio_Chip_Notes)
  on ConsoleMods Wiki, which the agent couldn't open (bot check), so the wording below is David's and **Verify**
  stays until someone reads the page or measures a capture): the YM2612's DAC has a known defect. Low-volume sounds suffer unusual quantization distortion (few of the DAC's steps are in use), and
  stereo playback can leak faint duplicates. For the mapper and the sample processor this means: keep PCM samples
  loud (normalize them, and bake the MOD volume in only as far as the sound stays well above the quantization steps,
  rather than relying on a quiet sample), and expect a quiet note on the DAC to sound worse than the same note on
  FM. The accuracy harness should score DAC notes at the volumes the song really uses.
  The in-app FM model is the clean YM3438 ([ADR 0056](adr/0056-ym3438-model.md)), so it doesn't reproduce this flaw.
- Channel 3 "special mode" (`$27` bit 6) gives each ch3 operator its own frequency → DefleMask "Genesis
  ext. CH3" system.
- Panning per channel: left / right / both / none (no fine pan).
- Master clock: NTSC 7 670 453 Hz, PAL 7 600 489 Hz.

## SN76489 (PSG)
- 3 square tone channels (10-bit period) + 1 noise channel; 4-bit attenuation (2 dB steps).
- Clock: NTSC 3 579 545 Hz, PAL 3 546 893 Hz.

## Timing
- Frame rate: NTSC 60 Hz, PAL 50 Hz. XGM/XGM2 advance per frame.
- MOD base tick = BPM×2/5 Hz → 50 Hz at BPM 125 for a PAL-made song, ×6/5 (60 Hz) with `--input-region ntsc`
  ([cli.md](cli.md#regions)). When the source and output regions match at BPM 125, one MOD tick = one frame
  exactly. Otherwise, or at any other BPM, ticks are spread onto frames so the source speed is kept (DMF: custom Hz /
  tick times; XGM: fractional accumulator in the tick player).

## Pitch conversion
MOD period → Hz (see [formats/mod.md](formats/mod.md)) → FM block/F-number, PSG period, or PCM rate:
- FM: `fnum = freq × 2^20 / (fM / 144) / 2^(block−1)` — compute and clamp to 11 bits, choose block.
- PSG: `N = clockPSG / (32 × freq)`, clamp to 1–1023.
