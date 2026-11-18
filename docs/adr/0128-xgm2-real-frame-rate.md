# ADR 0128: XGM2 frames are the Genesis's real VBlanks

- **Status:** Proposed
- **Date:** 2026-11-18

## Context

David heard the notes of a converted song (FunkyChristmas, played through SGDK's `XGM2_play` in BlastEm and Gens) lag
behind the WAV preview, more and more as the song goes on (2026-11-18).

SGDK's XGM2 driver plays one frame of the song each VBlank. With the default tempo, `XGM2_setMusicTempo` sets 50 frames
a second on PAL and 60 on NTSC, and the 68000 adds `adjTempo = tempo × 256 / (PAL ? 50 : 60)` per VBlank. A frame
therefore lasts one VBlank. A VBlank is not exactly 1/50 or 1/60 s, though. It is the master clock over 3420 clocks a
line times the lines in a frame:

| Genesis | Master clock | Lines | VBlank rate |
|---|---|---|---|
| NTSC | 53 693 175 Hz | 262 | 59.9227 Hz |
| PAL | 53 203 424 Hz | 313 | 49.7015 Hz |

`GenesisStream` placed each MOD tick at `round(seconds × 50)` (or 60), so on hardware the whole song ran slow:
0.13 % on NTSC (about 0.08 s a minute) and 0.60 % on PAL (about 0.36 s a minute). The WAV render plays MOD time, so the
console drifted further and further behind it. The ADR 0121 attack and the ADR 0124 early release don't delay notes;
they were checked and kept.

## Decision

- **Place ticks on real VBlanks.** `GenesisStream.RecordXgm2` puts each tick in the frame
  `round(seconds × GenesisChips.FrameRateHz(region))`. The rate is computed from the YM3438 clock (master / 7), 3420
  master clocks a line and 262 (NTSC) or 313 (PAL) lines. A song converted for the console it plays on now keeps the
  MOD's tempo to within half a frame for its whole length.
- The same rate is used everywhere the XGM2 path counts frames in seconds: the PCM lead
  (`Xgm2PcmDriver.LeadFrames` / `LeadBeats`, ADR 0119), the late-PCM note and the writer's "still sounding" checks.
- Asked for by David ("Yes, fix it here", 2026-11-18).

## Options considered

- **Real VBlank rate (chosen).** Exact for the console the file is made for, with no change to the file format or to
  the game's code.
- **Keep 50 / 60 Hz.** This is what SGDK's tempo names. It loses up to 0.6 % of the tempo, which is audible against the
  original after a minute.
- **Ask the game to set a tempo that makes up for it.** `XGM2_setMusicTempo` takes a whole number of frames a second,
  so 49.70 can't be asked for. It would also put the fix in every game instead of in the file.

## Consequences

- A PAL-made MOD (50 ticks a second) on a PAL Genesis has a little fewer than one frame per tick. About every 170th
  tick shares a frame with the tick before, and the existing conversion note counts those ticks. The note now says
  "1/49.7 s frame".
- The file still assumes the console of `--output-region`. A PAL file on an NTSC console is played by SGDK at
  213/256 frames a VBlank, so it keeps its tempo only roughly (frames are skipped and repeated). Use
  `--output-region ntsc` for NTSC consoles and for emulators set to region U, BlastEm's default.
- XGM v1 isn't written yet (T10.2). It should use `GenesisChips.FrameRateHz` the same way.
- docs/formats/xgm.md, docs/genesis-audio.md and docs/cli.md give the real rates.
