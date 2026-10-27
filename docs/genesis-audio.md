# Genesis audio notes

## YM2612 (OPN2)
- 6 FM channels, 4 operators each, 8 algorithms. Two register ports (port 0: ch1–3, port 1: ch4–6).
- **DAC mode**: register `$2B` bit 7 replaces ch6 with an 8-bit DAC written via `$2A` (unsigned, centred
  at `$80`). Only one hardware PCM stream; drivers like XGM mix several in software on the Z80.
- Channel 3 "special mode" (`$27` bit 6) gives each ch3 operator its own frequency → DefleMask "Genesis
  ext. CH3" system.
- Panning per channel: left / right / both / none (no fine pan).
- Master clock: NTSC 7 670 453 Hz, PAL 7 600 489 Hz.

## SN76489 (PSG)
- 3 square tone channels (10-bit period) + 1 noise channel; 4-bit attenuation (2 dB steps).
- Clock: NTSC 3 579 545 Hz, PAL 3 546 893 Hz.

## Timing
- Frame rate: NTSC 60 Hz, PAL 50 Hz. XGM/XGM2 advance per frame.
- MOD base tick = BPM×2/5 Hz → 50 Hz at BPM 125. On PAL with BPM 125 one MOD tick = one frame exactly.
  On NTSC, or any other BPM, ticks must be spread onto frames (DMF: custom Hz / tick times; XGM:
  fractional accumulator in the tick player).

## Pitch conversion
MOD period → Hz (see [formats/mod.md](formats/mod.md)) → FM block/F-number, PSG period, or PCM rate:
- FM: `fnum = freq × 2^20 / (fM / 144) / 2^(block−1)` — compute and clamp to 11 bits, choose block.
- PSG: `N = clockPSG / (32 × freq)`, clamp to 1–1023.
