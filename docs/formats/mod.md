# MOD input format

All multi-byte values are **big-endian**. Lengths/offsets in sample headers are in **words** (×2 for bytes).

## Variant detection

Read the 4-byte tag at offset **1080** (`0x438`):

| Tag | Channels | Samples | v1 |
|---|---|---|---|
| `M.K.`, `M!K!`, `FLT4`, `4CHN` | 4 | 31 | supported |
| `6CHN`, `8CHN`, `FLT8`, `CD81`, `OKTA`, `xxCH` | 6–32 | 31 | **rejected** (exit 3, clear message) |
| anything else | 4 | **15** (Soundtracker) | supported, after sanity checks |

A 15-sample file has no tag, so "anything else" also matches garbage. Accept it as Soundtracker only if:
file length ≥ 600 bytes, song length 1–128, every order < 128, sample volumes ≤ 64, and
`600 + patterns×1024 + Σ sample bytes` ≈ file length (allow a small tail). Otherwise exit 3.

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
| File shorter than the 1084-byte header, or ends inside the pattern data | Rejected, exit 3, with byte offset |
| Song length 0 or > 128 | Rejected, exit 3 |
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
| 26 | 2 | Loop start in words (some 15-sample files store **bytes**; detect if start+len > length) |
| 28 | 2 | Loop length in words; ≤ 1 means no loop |

The first 2 bytes of each sample are traditionally ignored by the Amiga player for looping samples.

### Pattern cell (4 bytes, 64 rows × 4 channels per pattern)

```
byte0: ssss pppp   s = sample high nibble, p = period bits 11..8
byte1: pppp pppp   period bits 7..0
byte2: ssss eeee   s = sample low nibble, e = effect
byte3: xxxx yyyy   effect parameter
```

Sample number 0 = no new instrument. Period 0 = no new note.

## Pitch

`freq_Hz = AmigaClock / (2 × period)`, PAL clock 7 093 789.2 Hz, NTSC 7 159 090.5 Hz.
Standard ProTracker periods cover C-1…B-3 (856…113). Map period → nearest note + cents, and apply finetune.

## Timing

Default speed 6 (ticks/row), BPM 125. Tick rate = `BPM × 2 / 5` Hz (50 Hz at 125).
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
