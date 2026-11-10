# ADR 0099: The XGM2 writer records the Genesis player

- **Status:** Accepted
- **Date:** 2026-11-09

## Context

Plan T10.1 is the first writer of a file SGDK plays: XGM2, a stream of register writes per frame (1/50 s PAL,
1/60 s NTSC) plus PCM play commands ([formats/xgm.md](../formats/xgm.md)). The converter already turns a
`GenesisPlan` into register writes tick by tick: `GenesisPlanPlayer`, which `--render-wav` and the optimizer's renders
use (ADR 0059). David moved T10.1 ahead of the FM-confidence tasks (T9.4e–T9.6) to get files that play on a real
Genesis, and chose each point below (2026-11-09).

## Decision

- **Record the player.** `GenesisPlanPlayer` writes through `GenesisChips.Ports` / `WritePsg` and tells an
  `IChipRecorder` every YM3438 write, PSG byte and PCM play or stop. `GenesisStream.RecordXgm2` runs the player on
  XGM2's driver without rendering sound and puts each tick's events in the frame the tick starts in (the nearest frame
  start); `Xgm2Writer` encodes the frames. The file plays what the XGM2 WAV render plays. (David)
- **Ticks that share a frame** play their changes together, in order; one conversion note counts them (a 60 Hz MOD on a
  PAL Genesis, or a BPM above the frame rate).
- **Loudness in 3 dB steps, the WAV too.** XGM2 can't change a recording's volume, so each loudness is a stored copy.
  Copies are made in 3 dB steps, as the capacity check counts them (ADR 0086), on the file and on the XGM and XGM2 WAV
  render, so they still sound the same; a note is at most 1.5 dB off. (David)
- **Music PCM at the low priority** (`$1x` bit 3 clear, priority 7 in the driver), as SGDK's xgm2tool writes music,
  so a game's sound effect can take a channel. (David)
- **The song's end:** a song that loops goes back to its loop frame; one that ends stops, after a frame that lets
  every key up, turns every PSG channel off and stops every PCM channel. (David) The loop (or end) command ends the
  frame it is in (`FMCom_loop`, `PSGCOM_loop`), so it goes in the last frame, not after a wait. A recording still
  sounding at the end is stopped there, so none plays on into the loop's opening.
- **At the loop frame** the writer sets every register the song has written back to its value on the first time
  through, then LFO and CH3 mode, every channel's key (up where none was set yet), and every PSG channel's tone and
  volume (off where none was set yet), so the second time sounds like the first. A recording still sounding at the
  loop frame can't start partway: a conversion note counts them.
- **Recordings stored as heard:** copies with the same bytes share one sample id, as the capacity check counts them,
  and each is stored only as far as any play of it reaches (to its channel's next stop or play, or the song's end or
  jump, plus two frames). The player unrolls a looped sample to 10 s; the file keeps what is heard. `--max-pcm-bytes`
  is checked again against the bytes written.
- **Simple commands first, the driver's state kept right:** a channel whose whole patch is written together goes as
  one load instrument (`$2x`, 31 bytes instead of about 64 raw), every other total level as `$9x`, the rest as raw
  writes (`$Ex`, 8 per command, one part); `$F8` key, `$F9` LFO, `$FA`/`$FB` for the CH3 bit of `$27` (the driver
  owns its timer bits), `$1x` PCM; PSG tone (`$2x`) and volume (`$8x`–`$Bx`); one wait per frame. SGDK's driver keeps
  each slot's total level to add the game's music volume to; `$2x` and `$9x` update it, a raw `$4x` write wouldn't.
  Every write to the noise register is sent, since each restarts the noise. The DAC registers are the driver's; a
  write to one is a bug. Delta and combined commands are left for later, guarded by the byte-exact tests.
- **Frames under 225 bytes:** xgm2tool's packer needs every frame under 256 bytes and, through a gap in its block
  ending, 31 bytes to spare after a tiny frame ([formats/xgm.md](../formats/xgm.md)). A longer frame is split with
  `$F0`; the driver then plays the rest a frame late without counting it, so the writer takes the frame off the next
  wait and the FM stream stays in step with the PSG. A conversion note counts the splits.
- **No CH3 special mode yet:** nothing in the conversion uses it; the writer passes the bit through if it ever comes.
  (David)
- **Checked against SGDK itself:** tests run SGDK's xgm2tool (compile to `.xgc`) and rescomp (`XGM2` resource) on
  converted songs, where SGDK and Java are installed; CI skips them. (David)

## Options considered

- **Record the player (chosen).** One path for what is heard and what is written: every fix to the render reaches
  the file.
- **A separate encoder from the plan.** It would duplicate the player's pitch, level, pan and note rules, and the
  file could drift from the WAV the listening rounds judge.
- **Exact loudness per note.** Could need more copies than the capacity check counted, so a song that fits might not.
- **3 dB steps in the file only.** The WAV would no longer be what the file plays.
- **Music at the high priority.** A game couldn't play a sound effect over the music's channel.
- **Always loop.** Games usually want it, but a MOD that ends should end; `--loop` can come later if asked.

## Consequences

- SGDK's rescomp accepts the file as an `XGM2` resource when it is named `.xgm` (rescomp hands it to xgm2tool, which
  picks the reader by extension: `Launcher.java`). That answers the first half of T10.2's question; the file is still
  written as `.xgm2` with the NOTE line (ADR 0045). Playing it in BlastEm stays T10.2.
- xgm2tool's XGM → VGM conversion fails on every XGM2 file, its own too (`VGM.java` casts the parsed loop command), so
  VGM can't be used to check the file.
- The XGM and XGM2 WAV renders change slightly (recordings in 3 dB steps); listening-round clips made before differ.
- All 19 corpus songs compile with xgm2tool and build as rescomp resources (simple mapping, 2026-11-09).
- **Open: loops past the start in SGDK.** rescomp always has xgm2tool pack the song (`.xgc`). Reading an XGM2 file,
  xgm2tool keeps no loop-start marker (`XGM.parseFMMusic`), so it packs the loop command with the file's unpacked
  offset and never moves it (`getPackedFMMusicDataArray`). A loop back to frame 0 is right; a loop to a later frame
  jumps to the wrong place in an SGDK game. A conversion note says so for each such song.
- Open for T10.2: whether a frame of up to 224 bytes takes the Z80 too long, heard on BlastEm. The key off and on of a
  retriggered FM note land in the same frame.
- T11.2 (XGM v1) reuses `GenesisStream` with XGM's driver and its own encoder.
