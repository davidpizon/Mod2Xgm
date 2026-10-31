# MOD input format

All multi-byte values are **big-endian**. Lengths/offsets in sample headers are in **words** (×2 for bytes).

## Variant detection

Read the 4-byte tag at offset **1080** (`0x438`):

| Tag | Channels | Samples | v1 |
|---|---|---|---|
| `M.K.`, `M!K!`, `FLT4`, `4CHN` | 4 | 31 | supported |
| `6CHN`, `8CHN`, `FLT8`, `CD81`, `OKTA`, `xxCH` | 6–32 | 31 | **rejected** (exit 3, names the tag) |
| `N.T.`, `M&K!`, `FEST`, `LARD`, `NSMS` | 4 | 31 | **rejected for now** (exit 3, names the tag and tracker) |
| anything else | 4 | **15** (Soundtracker) | supported, see below |

A 15-sample file has no tag, so "anything else" also matches garbage. Detection is deliberately **relaxed**
(David, 2026-10-27): the hard checks are only what's needed to read the file at all, and every sign of junk is a
conversion note instead (see *Damaged files*).

- **Rejected (exit 3)** if the file is shorter than the 600-byte header, the song length (offset 470) is outside
  1–128, a played order is ≥ 128, or the file ends inside the pattern data. The message gives both reasons: what was
  found at offset 1080 instead of a tag, and why the 15-sample reading failed, with the byte offset.
- **Accepted** otherwise, with a conversion note saying the file is assumed to be a 15-sample Soundtracker module
  (it shows the bytes found at offset 1080).
- Ultimate Soundtracker (the first version) and later Soundtrackers can't be told apart from the header. Byte 471 is
  the tempo in Ultimate Soundtracker and the restart position later, and effects 1/2 differ. The reader keeps the
  bytes as stored and always adds a note; the song model (plan M3) decides.

## Layout (31-sample)

| Offset | Size | Field |
|---|---|---|
| 0 | 20 | Song title (ASCII, space/NUL padded) |
| 20 | 31 × 30 | Sample headers |
| 950 | 1 | Song length (number of orders, 1–128) |
| 951 | 1 | Restart position (often 127; treat ≥ song length as 0) |
| 952 | 128 | Order table (pattern indices) |
| 1080 | 4 | Tag |
| 1084 | N × 1024 | Patterns, N = highest order-table entry + 1 (see *Pattern count*) |
| … | | Sample data, in sample order, 8-bit **signed** PCM |

### Pattern count

ProTracker stores every pattern up to the highest entry in the **whole** 128-entry order table, but some
trackers leave garbage in entries past the song length. `ModReader` computes both readings (all 128 entries vs.
only the played orders) and picks the one whose expected size — header + patterns + sample data — is closest
to the real file size; ties go to all entries.

### Damaged files (implemented in `src/Mod/ModReader.cs`)

| Problem | Handling |
|---|---|
| File shorter than the header (1084 bytes; 600 for 15-sample), or ends inside the pattern data | Rejected, exit 3, with byte offset |
| Song length 0 or > 128 | Rejected, exit 3 |
| 15-sample: a played order ≥ 128 | Rejected, exit 3 |
| Order-table entries past the song end point at patterns the file doesn't hold | Ignored (see *Pattern count*); conversion note |
| Bytes after the last sample's data | Ignored; conversion note |
| Title or sample name with control characters | Kept as stored; conversion note |
| Volume above 64 | Kept as stored (players cap it at 64); conversion note |
| Sample longer than 64 KB | Kept; conversion note |
| Finetune byte with its high 4 bits set | Low 4 bits used; conversion note |
| 15-sample: finetune not 0 (Soundtracker had none) | Kept as stored; conversion note |
| Pattern cells with a sample number above the sample count, or a period outside 108–907 | Kept as stored; one conversion note each, with the count and the first location |
| Sample data cut short (common in old rips) | Kept up to the end of the file; conversion note |
| Loop runs past the sample end | Loop shortened; conversion note |
| Loop starts past the sample end | Loop removed; conversion note |
| Loop length ≤ 2 bytes | No loop (ProTracker convention) |
| Sample 1 word (2 bytes) long | Read as empty and its bytes left to the next sample, as OpenMPT does ([ADR 0062](../adr/0062-one-word-samples.md)); conversion note |
| Names | Latin-1, cut at the first NUL, trailing spaces removed, leading spaces kept |

15-sample layout: same but 15 headers, song length at 470, restart at 471, orders at 472, **no tag**,
patterns at 600.

### Sample header

| Offset | Size | Field |
|---|---|---|
| 0 | 22 | Name |
| 22 | 2 | Length in words |
| 24 | 1 | Finetune: low nibble, signed 4-bit (−8…+7, 1/8 semitone steps) |
| 25 | 1 | Volume 0–64 |
| 26 | 2 | Loop start in words (some 15-sample files store **bytes**: see below) |
| 28 | 2 | Loop length in words; ≤ 1 means no loop |

The first 2 bytes of each sample are traditionally ignored by the Amiga player for looping samples.

**A sample 1 word long (2 bytes) is read as empty, without its bytes**, as OpenMPT reads it
([ADR 0062](../adr/0062-one-word-samples.md)): the samples after it start 2 bytes earlier in the file for each such
sample, and the file's last bytes go unread; one conversion note lists them. ProTracker reads the 2 bytes as stored.

**Loop start in bytes (15-sample files only).** Ultimate Soundtracker stored the loop start in bytes. If a loop only
fits inside the sample when its start is read in bytes, it's read that way, with a conversion note. If it fits
either way, it's read as words (as stored by later Soundtrackers) and one note counts such samples; the song model
(M3) can halve those starts if it decides the file is from Ultimate Soundtracker. If it fits neither way, the normal
damaged-loop rules apply. 31-sample files always use words.

### Pattern cell (4 bytes, 64 rows × 4 channels per pattern)

```
byte0: ssss pppp   s = sample high nibble, p = period bits 11..8
byte1: pppp pppp   period bits 7..0
byte2: ssss eeee   s = sample low nibble, e = effect
byte3: xxxx yyyy   effect parameter
```

Sample number 0 = no new instrument. Period 0 = no new note.

## Pitch

Code: `src/Model/AmigaPitch.cs` (plan T3.2).

**Playback rate.** Paula plays a sample at `rate_Hz = PaulaClock / period`. PaulaClock is the Amiga crystal ÷ 8:
PAL 28 375 160 / 8 = **3 546 895 Hz**, NTSC 28 636 360 / 8 = **3 579 545 Hz** (often quoted as the CPU clock ÷ 2:
7 093 789.2 / 2 and 7 159 090.5 / 2, within 0.2 ppm). The clock comes from `--input-region` (default PAL,
[cli.md](../cli.md#regions)); NTSC plays every period 15.9 cents higher.

**Note + cents.** Period 428 on the PAL clock is exactly **C-2** at 0 cents (semitone 24, counting from C-0 as
trackers name octaves; C-1 = 856, C-3 = 214). Any other period and clock is measured from it:
`semitones = 24 + 12 × log2((clock / PALclock) × 428 / period)`, rounded to the nearest semitone, with the rest kept
as unrounded cents (−50 to just under +50). Periods between table entries keep their exact pitch.

**Finetune** (signed, −8…+7 eighths of a semitone; `Instrument.FineTuneCents` = finetune × 12.5) is applied the way
ProTracker 2's `setPeriod` does it, not by formula: ProTracker has **16 period tables** (C-1…B-3, 36 notes each,
rounded by hand, up to 8.75 cents off perfect tuning). The first finetune-0 entry that is **not above** the written
period picks the note, and the finetune's table gives the period played. So a period between entries snaps to the
higher note, even at finetune 0; 428 at finetune +1 plays 425, at −8 plays 453. Periods below 113 (B-3 at finetune 0)
make ProTracker 2 read its tables' zero padding (period 0). That is ProTracker's rule; Mod2Xgm plays OpenMPT's
(below), which starts from the same tables: ProTracker 2.3 data via pt2-clone (BSD-3-Clause,
[THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)). Pattern cells keep the note **as written** (finetune 0),
since a cell often doesn't name its instrument; the tick player (T3.4) applies the playing instrument's finetune.

The **tick player** plays finetune as **OpenMPT** does, measured with libopenmpt 0.8.9
([ADR 0060](../adr/0060-openmpt-pitch-and-loops.md)): periods are kept in **quarters**; a finetuned note is its
finetune's C-1 period (from the same tables) halved for each octave up, the quarters truncated (C-3 at +1 is
850 ÷ 4 = 212.5, where ProTracker's table has 213); finetune 0 uses the table, except B-3, which plays at 113¼; no
note plays below 113. That is up to about 7 cents from ProTracker's tables on most notes, and more on the top notes
of positive finetunes, which ProTracker plays below 113. The tick player keeps each written note's tuned pitch, the first
time its row plays (`Playback.NotePitches`), and the mapper plans the note there, so the **DMF** plays the same pitch
([ADR 0061](../adr/0061-dmf-pitch-from-tick-player.md); before T7.10 the DMF ignored the instrument's finetune).

## Timing

Default speed 6 (ticks/row), BPM 125. Tick rate = `BPM × 2 / 5` Hz (50 Hz at 125) on a PAL Amiga. With
`--input-region ntsc` the song is treated as made on an NTSC Amiga, whose screen-timed ticks run at 60 Hz: tick rate
= `BPM × 2 / 5 × 6 / 5` (60 Hz at 125). See [cli.md](../cli.md#regions).

The tick player (`src/Mod/ModTickPlayer.cs`, T3.4) uses ProTracker's exact CIA rate, `709 379 / (1 773 447 / BPM + 1)` Hz
with the division truncated (49.9985 Hz at 125 BPM), and applies a new BPM one tick late as ProTracker does. Under
Ultimate Soundtracker rules the rate is `BPM × 2 / 5` from byte 471. A pass through the song ends the way pt2-clone's
MOD2WAV export decides it: after the tick that would move to a row already played this pass (`E6x` repeats don't
count) or past the song's end. The row it would move to is the loop point; `F00` ends without a loop.
`Fxx`: xx < 0x20 → speed, else BPM (some old players treat all as speed — emit a conversion note on ambiguity).

## Effects (ProTracker)

| Fx | Name | Notes for conversion |
|---|---|---|
| 0xy | Arpeggio | DMF 00xy equivalent |
| 1xx / 2xx | Portamento up / down | period-space slide; convert to pitch units |
| 3xx | Tone portamento | |
| 4xy | Vibrato | |
| 5xy | Tone porta + vol slide | |
| 6xy | Vibrato + vol slide | |
| 7xy | Tremolo | likely approximate or drop (conversion note) |
| 8xx | Panning (non-standard) | Genesis has L/C/R only |
| 9xx | Sample offset | hard for DAC/XGM; conversion note |
| Axy | Volume slide | |
| Bxx | Position jump | defines loop |
| Cxx | Set volume (0–64) | |
| Dxx | Pattern break (BCD row) | |
| Exy | Extended (E1/E2 fine slides, E6 pattern loop, E9 retrigger, EA/EB fine vol, EC note cut, ED note delay, EE pattern delay) | |
| Fxx | Speed / BPM | |

Amiga channels 1 and 4 are left, 2 and 3 are right (hard stereo).

### Decoding (T3.3)

Code: `src/Mod/ModEffects.cs` (one cell's command → song-model effects) and `src/Mod/ModToSong.cs` (the whole song).
The table gives **ProTracker 2** meanings, used for 31-sample files unless `--player` says otherwise; Ultimate
Soundtracker's are under [Players](#players).

| Command | Decoded as | Zero parameter |
|---|---|---|
| `0xy` | `Arpeggio(x, y)` semitones | `000` = no effect |
| `1xx` / `2xx` | `PitchSlide(+xx / −xx)` periods, every tick after the first | no effect (no memory) |
| `3xx` | `TonePortamento(xx)` periods per tick | memory |
| `4xy` | `Vibrato(x / 64 cycles per tick, 255 × y / 128 periods peak)` | each half: memory |
| `5xy` / `6xy` | tone portamento / vibrato with memory + volume slide as `Axy` | only the first part |
| `7xy` | `Tremolo(x / 64, 255 × y / 64 / 64 of full volume peak)` | each half: memory |
| `8xx` | ignored by ProTracker 2: `UnsupportedEffect` + conversion note | |
| `9xx` | `SampleOffset(xx × 256)` frames | memory |
| `Axy` | `VolumeSlide(+x / 64)` if x > 0, else `(−y / 64)`; every tick after the first | no effect |
| `Bxx` | `PositionJump(xx)` | |
| `Cxx` | the cell's `Volume` = min(xx, 64) / 64; above 64 → conversion note | |
| `Dxx` | `PatternBreak(x × 10 + y)`; a digit above 9 → note; a row above 63 → row 0 + note | |
| `E0x` | `SetLowPassFilter`: E00 on, E01 off (bit 0) | |
| `E1x` / `E2x` | `PitchSlide(±x)` periods, once at the row start | no effect |
| `E3x` | `SetGlissando(x ≠ 0)` | |
| `E4x` / `E7x` | wave x & 3: 0 sine, 1 ramp down, 2 **and 3** square (ProTracker 2 plays "random" as square); x & 4 set = don't restart on new notes | |
| `E5x` | `SetFineTune(signed x × 12.5)` cents | |
| `E6x` | `PatternLoopStart` (E60) / `PatternLoop(x)` | |
| `E8x` | unused by ProTracker 2: `UnsupportedEffect` + conversion note | |
| `E9x` | `Retrigger(x)` ticks | no effect |
| `EAx` / `EBx` | `VolumeSlide(±x / 64)`, once at the row start | no effect |
| `ECx` | `NoteCut(x)`; EC0 cuts at once | |
| `EDx` | `NoteDelay(x)` | no effect (ProTracker plays the note normally) |
| `EEx` | `PatternDelay(x)` rows | no effect |
| `EFx` | `InvertLoop(x)`; EF0 stops it | |
| `Fxx` | `StopSong` (F00), `SetSpeed(xx)` below 0x20, else `SetTempo(xx)` | |

A 15-sample file read with `--player protracker` reads effects 1 and 2 as slides (later Soundtrackers, ProTracker),
with a conversion note, since Ultimate Soundtracker used them for arpeggio and pitch bend. A cell naming a sample
above the module's count names no sample (note).

## Players

Players disagree about the same bytes, so the file is read **by one player's rules** from the reader onward
(`src/Mod/ModPlayer.cs`, plan T3.3a): `--player protracker|ust` ([cli.md](../cli.md#players)), defaulting to
**Ultimate Soundtracker for 15-sample files** and **ProTracker 2 for 31-sample files** (David). Every run prints which
player it emulates. Ultimate Soundtracker's rules follow OpenMPT's Soundtracker loader (`soundlib/Load_stk.cpp`,
BSD-3-Clause; read, not copied):

| | ProTracker 2 | Ultimate Soundtracker |
|---|---|---|
| Loop start | words (15-sample files: bytes when only bytes fit, see [Sample header](#sample-header)) | always **bytes**; a looped sample starts at its loop start (the sound before it is skipped, as OpenMPT plays it) |
| Byte 471 | restart position (T3.4) | **tempo**: 0 and 0x78 → 125 BPM, else 709 379 × 125 / 50 / ((240 − b) × 122) BPM; 240+ → 125 with a note |
| Finetune | used | ignored (note) |
| `0xy` | arpeggio | arpeggio from 03 up, else nothing |
| `1xy` | slide up | **arpeggio** |
| `2xy` | slide down | **pitch bend**: y up, or else x down |
| `3xx`–`Fxx` | as above | **don't exist**: ignored, with one conversion note (including `Cxx`, so no volume changes) |

The sample-usage scan (report section 2) follows the same rules, so under Ultimate Soundtracker `Bxx`/`Dxx`/`E6x`/`F00`
don't steer it and `3xx` notes count. OpenMPT's own MOD rules (its guesses about panning, 7-bit panning, random
waves, NoiseTracker `Dxx` and vblank timing) are a later plan task (T3.3b).

Reference: ProTracker module format description (`MOD` / "Noisetracker/Soundtracker/Protracker Module Format", various authors); verify edge cases against a reference player such as OpenMPT or libxmp.
