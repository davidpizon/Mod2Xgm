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
make ProTracker 2 read its tables' zero padding (period 0); Mod2Xgm keeps the written period instead and adds a
conversion note. Pattern cells keep the note **as written** (finetune 0); the tick player (T3.4) applies the playing
instrument's finetune, since a cell often doesn't name its instrument. Tables: ProTracker 2.3 data via pt2-clone
(BSD-3-Clause, [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)).

## Timing

Default speed 6 (ticks/row), BPM 125. Tick rate = `BPM × 2 / 5` Hz (50 Hz at 125) on a PAL Amiga. With
`--input-region ntsc` the song is treated as made on an NTSC Amiga, whose screen-timed ticks run at 60 Hz: tick rate
= `BPM × 2 / 5 × 6 / 5` (60 Hz at 125). See [cli.md](../cli.md#regions).
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

Reference: ProTracker module format description (`MOD` / "Noisetracker/Soundtracker/Protracker Module Format", various authors); verify edge cases against a reference player such as OpenMPT or libxmp.
