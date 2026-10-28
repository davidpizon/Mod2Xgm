# DMF output format (DefleMask)

**Authoritative spec:** [`DMF_SPECS.txt`](DMF_SPECS.txt) in this folder — "Specs for DMF (DefleMask Module
Format, for DefleMask v1.0.0 and above)", Delek © 2021, copied from https://www.deflemask.com/DMF_SPECS.txt
on 2026-10-27. Older revisions: `DMF_SPECS_0x11.txt`, `_0x12`, `_0x15` on the same site.

> ⚠️ **Same extension, different format.** fileformat.info's `format/dmf/corion.htm` documents the
> X-Tracker / D-Lusion "Digital Music File" (magic `DDMF`). It is **not** DefleMask's format; don't use it.

This page summarizes the spec for the **Genesis** case and fills in what the spec leaves unsaid (plan T4.1). Each
gap is closed from one of these, named in brackets where it's used:

- **[Furnace]** — Furnace's DMF loader and saver, read as a reference only (GPL, no code copied; see
  [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)), commit `bcba71d`: `src/engine/fileOps/dmf.cpp`, plus
  `engine.cpp` (rates, notes), `legacySample.cpp` (DAC sample selection), `playback.cpp` (jumps, speeds, panning),
  `sysDef.cpp` (channel layout) and `platform/genesis.cpp`, `genesisext.cpp`, `fmshared*.h` (operators, detune,
  ext. CH3). Furnace opens and saves DMF files that DefleMask plays, so it is the best record of DefleMask's
  behavior short of DefleMask itself.
- **[Manual]** — DefleMask's effect list ("DefleMask Tracker Effects Commands", Battle of the Bits lyceum,
  https://battleofthebits.com/lyceum/View/DefleMask+Tracker+Effects+Commands).

The probe files (plan T4.3, [`tests/golden/dmf-probes/`](../../tests/golden/dmf-probes/README.md)) confirm these in DefleMask and through VGM export → rescomp; anything they disprove is
fixed here. The test-only `DmfReader` (`tests/Mod2Xgm.Tests/Dmf/`, plan T4.2) follows this page and rejects anything it
doesn't allow.

## Container

- Whole file is **zlib-compressed** → write with `ZLibStream` (zlib header + Adler-32), not raw `DeflateStream`.
- Multi-byte integers are **little-endian** [Furnace: read with its little-endian reader; byte-swapped on big-endian
  hosts].
- Strings: 1-byte length (0–255) + chars; no terminator. Encoding unstated — write ASCII and replace
  anything else with `?` (conversion note).
- Spec notation: "2 Bytes … (-1 = Empty)" means signed 16-bit, `0xFFFF` = empty.

## Layout (in order)

Fields that only exist in other file versions are left out: we write version 24, which has **no** pattern names
(added in 25) and **no** sample cut start/end (added in 27) [Furnace].

### 1. Format flags
| Size | Field |
|---|---|
| 16 | `.DelekDefleMask.` |
| 1 | File version — **24 (`0x18`)** for DefleMask v1.0.0 → the version we write |

### 2. System
| Size | Field |
|---|---|
| 1 | `0x02` Genesis (**10 channels**) or `0x42` Genesis ext. CH3 (**13 channels**) |

Writes `0x02`, or `0x42` when the optimizer uses CH3 special mode (combined instruments).

Channel order — used for the pattern matrix and the patterns [Furnace: a Genesis file is the YM2612 followed by the
SN76489; `sysDef.cpp` channel lists]:

| System | Channels, in file order |
|---|---|
| `0x02` | FM1, FM2, FM3, FM4, FM5, FM6, PSG1, PSG2, PSG3, PSG4 (PSG4 = noise) |
| `0x42` | FM1, FM2, **FM3 OP1, FM3 OP2, FM3 OP3, FM3 OP4**, FM4, FM5, FM6, PSG1, PSG2, PSG3, PSG4 |

**Ext. CH3** (`0x42`): each FM3 OP channel has its own note (frequency) and plays one operator of FM3 — channel
*OP n* plays operator *n* (Yamaha numbering) **of the instrument on that channel**. Algorithm, LFO FMS and AMS
come from the last instrument played on any OP channel; feedback only from the one on **OP1** [Furnace:
`genesisext.cpp`]. So a combined CH3 instrument gives every OP channel the same instrument and the algorithm it
needs, with each channel playing its own operator.

### 3. Visual information
Song name (len + chars), song author (len + chars), highlight A (1), highlight B (1).
Suggest highlight A = 4, B = 16 (MOD convention).

### 4. Module information
| Size | Field | Notes |
|---|---|---|
| 1 | Time base | Write **0**. Tick times and speed effects are multiplied by time base + 1 [Furnace] |
| 1 | Tick time 1 | ticks per row on the first row and every other one after it (rows 0, 2, 4, …) |
| 1 | Tick time 2 | ticks per row on rows 1, 3, 5, … (MOD speed → both equal) |
| 1 | Frames mode | **0 = PAL (50 Hz)**, 1 = NTSC (60 Hz) [spec; Furnace: 0 → 50 Hz, otherwise 60 Hz] |
| 1 | Using custom Hz | 1 = the custom Hz value replaces the PAL/NTSC rate |
| 3 | Custom Hz value | The tick rate in Hz as **decimal ASCII digits**, left-aligned, padded with `0x00`: 50 → `35 30 00`, 125 → `31 32 35` [Furnace: saved with `"%d"` into 3 bytes, read with `stoi`]. Integer Hz only, 1–999. Written even when custom Hz is off |
| 4 | Rows per pattern | MOD = 64 (Furnace rejects more than 256) |
| 1 | Rows in pattern matrix | MOD song length (≤ 128). Furnace rejects more than 127 — keep it ≤ 127 |

(The old 1-byte arpeggio tick speed was removed in ver 11.1 — don't write it.)

**MOD timing mapping:** tick time 1 = tick time 2 = MOD speed. Tick rate = BPM×2/5 Hz; BPM 125 → PAL with
no custom Hz; any other BPM → custom Hz, rounded to whole Hz (conversion note when it isn't whole). Mid-song speed
changes use `09xx` + `0Fxx` (see [Effects](#effects)). **DMF has no tick-rate effect**: the custom Hz value is
the one rate for the whole song, so a mid-song tempo (BPM) change can only be approximated through speeds
(conversion note) — plan T5.4.

### 5. Pattern matrix
Channel-major: for each channel (10 or 13, in the order above), for each matrix row, 1 byte = pattern index
(Furnace rejects more than `0x7F`).

### 6. Instruments
1 byte count, then per instrument: name (len + chars), mode (1 byte: **0 = STD**, **1 = FM**).

**FM** (FM1–6): ALG, FB, LFO (FMS), LFO2 (AMS) — 1 byte each; then 4 operators × 12 bytes:
AM, AR, DR, MULT, RR, SL, TL, DT2, RS, DT, D2R, SSGMODE.

- **Operator order = YM2612 register order** [Furnace: operator *j* in the file is written to register offset
  +4·*j*]: the four operators in the file are, in Yamaha numbering, **OP1, OP3, OP2, OP4** (slots S1, S3, S2, S4 —
  register offsets `+0`, `+4`, `+8`, `+C`). Effects that name an operator (`12xx`–`15xx`, `1Axx`–`1Dxx`) and the
  ext. CH3 OP channels use Yamaha numbering, not file order.
- **DT** is stored **0–7 with 3 = no detune**: 0, 1, 2 = −3, −2, −1; 4, 5, 6 = +1, +2, +3 (7 = "−0"). The register
  value is `{7, 6, 5, 0, 1, 2, 3, 4}[DT]` [Furnace: `dtTable`].
- **SSGMODE** is the register's low 4 bits: bit 3 (`0x08`) = enable, bits 0–2 = mode [Furnace writes it to
  `0x90` as `ssgEnv & 15`]. The spec's "bit 4" counts from 1.
- DT2 is unused on the YM2612 — write 0.

**STD** (PSG on Genesis): four macros, each = size byte (0–127) + size × 4-byte values + loop position
byte **only if size > 0** (−1 = no loop):
1. Volume macro (present for Genesis; skipped only on Game Boy)
2. Arpeggio macro (values signed, offset 12) + **1 extra byte: arpeggio mode** (0 normal, 1 fixed) — always written
3. Duty/noise macro
4. Wavetable macro

No per-system extra block for Genesis (only C64 and Game Boy have one).

### 7. Wavetables
1 byte count — write **0** for Genesis.

### 8. Patterns
For each channel:
- 1 byte: effect-column count for this channel, **1–4** [Furnace rejects anything else]
- for each **matrix row** (not each unique pattern), for each row (64):

| Size | Field |
|---|---|
| 2 | Note: 1 = C#, 2 = D, … 11 = B, **12 = C**; 0 (with octave 0) = empty; 100 = note off |
| 2 | Octave |
| 2 | Volume (−1 empty): **FM 0–`7F`**, **PSG 0–`F`** [Furnace: older files stored 0–`FF` and are scaled to these] |
| 2 × 2 × N | N × (effect code, effect value), −1 empty |
| 2 | Instrument (−1 empty) |

Notes:
- C is 12, not 0, and belongs to the octave **below**: **C of octave *n* is stored as note 12, octave *n−1***
  [Furnace: a note's pitch is note + 12 × octave, so note 12 of octave *n−1* = C of octave *n*]. C#–B of octave
  *n* are notes 1–11 with octave *n*.
- Because pattern data is written once per matrix row, give each matrix row of a channel its **own**
  pattern index (0, 1, 2, …) — simplest and avoids ambiguity when MOD orders repeat a pattern. Duplicate
  data is the cost; it compresses well.

### 9. PCM samples
1 byte count (so **≤ 255 samples**), then per sample:

| Size | Field |
|---|---|
| 4 | SAMPLE_SIZE (number of samples, not bytes) |
| 1 + N | Name |
| 1 | Sample rate code: **0 = 4000, 1 = 8000, 2 = 11025, 3 = 16000, 4 = 22050, 5 = 32000 Hz** [Furnace: `fileToDivRate`] |
| 1 | Sample pitch, **0–10**: resamples the sample when the file is loaded, by a step of 1/6, 1/5, 1/4, 1/3, 1/2, **1 (code 5)**, 2, 3, 4, 5, 6 — a step above 1 shortens the sample, so it plays higher [Furnace] |
| 1 | Sample amp, **0–100**: gain = amp / 50, so **50 = unchanged**, applied on load and clipped [Furnace] |
| 1 | Sample bits: 8 or 16 |
| 2 × SAMPLE_SIZE | Sample data — always 2 bytes per sample. **16-bit: signed little-endian.** 8-bit: each 2-byte value holds an *unsigned* 0–255 sample (Furnace subtracts `0x80`) |

**What we write:** rate code for the rate the sample was resampled to, **pitch 5, amp 50, bits 16**, signed 16-bit
data — exactly what Furnace writes, so no load-time resampling, no gain, and no 8-bit ambiguity. The sample
processor (plan T5.1) does the resampling and level itself.

## Effects

DMF effects are raw codes and values; DefleMask applies them. Values are **hex** (`0Dxx` row `xx` = `0x10` is row
16) [Furnace passes values through unchanged]. These are the ones the writer may use (plan T5.5); everything else
in the MOD becomes a conversion note there.

| Code | Effect | Notes |
|---|---|---|
| `00xy` | Arpeggio | +x, +y semitones |
| `01xx` / `02xx` | Pitch slide up / down | `00` stops it |
| `03xx` | Portamento to note | `00` stops it |
| `04xy` | Vibrato | x speed, y depth |
| `05xy` / `06xy` | Portamento / vibrato continued + volume slide `0Axy` | |
| `07xy` | Tremolo | x speed, y depth |
| `08xy` | Output L / R | **x ≠ 0 → left on, y ≠ 0 → right on** (`11` both, `10` left, `01` right); on/off only, no levels [Manual; Furnace]. FM channels only — the Genesis PSG is mono |
| `09xx` | Speed 1 (tick time 1) | `00` ignored [Furnace] |
| `0Axy` | Volume slide | x up, y down |
| `0Bxx` | Jump to order `xx` | goes to **row 0**; only the **first** `0Bxx` in a row counts [Furnace: DefleMask jump rule] |
| `0Cxx` | Retrigger every `xx` ticks | |
| `0Dxx` | Next order, at row `xx` | **ignored on the last order**; overrides a `0Bxx` on the same row [Furnace: DefleMask jump rule] |
| `0Fxx` | Speed 2 (tick time 2) | `00` ignored |
| `E1xy` / `E2xy` | Note slide up / down | x speed, y semitones |
| `E5xx` | Channel fine pitch | `80` = none |
| `EBxx` | DAC sample bank | `00`–`0B` (banks 0–11); higher values do nothing [Manual] — see [DAC](#dac-samples-on-fm6) |
| `ECxx` | Note cut after `xx` ticks | |
| `EDxx` | Note delay `xx` ticks | |
| `10xy` | LFO | x on/off, y rate 0–7 (FM) |
| `11xx` | Feedback | 0–7 (FM) |
| `12xx`–`15xx` | TL of operator 1–4 | 0–`7F`, Yamaha numbering (FM) |
| `17xx` | **DAC on/off** on FM6 | `01` on, `00` off [Manual; Furnace `legacySample.cpp`] |
| `19xx` / `1Axx`–`1Dxx` | Attack rate of all operators / of operator 1–4 | 0–`1F` (FM) |
| `20xy` | PSG noise mode | x = 0: 3 fixed rates, x ≠ 0: rate from PSG3; y = 0: periodic, y ≠ 0: white (PSG4) |

Jump consequences for the writer: MOD `Bxx` + `Dxx` on one row ("order *x*, row *y*") can't be written as one DMF
row, and a MOD `Dxx` on the last order is dropped by DefleMask. The writer lays out orders so neither is needed
(plan T5.5). DMF has **no stop effect** — DefleMask always loops (to order 0 unless a `0Bxx` sends it elsewhere),
so a MOD that stops (`F00`) gets a conversion note.

## DAC samples on FM6

[Furnace: `legacySample.cpp`, `platform/genesis.cpp`; Manual]

- `17 01` on FM6 switches it to the DAC; `17 00` switches it back to FM. While the DAC is on, FM6's notes play
  samples.
- **The note picks the sample, not the pitch:** a note plays sample **12 × bank + semitone**, where the semitone
  is C = 0, C# = 1, … B = 11 and the bank is set by `EBxx` (default 0). **The octave is ignored**, and the sample
  always plays at its own rate (the rate code). The instrument column plays no part in choosing the sample.
- So **12 banks × 12 = 144 samples** can be reached from patterns, not 255.
- Every pitch a MOD sample is played at needs its **own pre-resampled copy** in the DMF (see
  [../sample-capacity.md](../sample-capacity.md)).
- `17xx` and `EBxx` work only on FM6 (ext. CH3: the 9th channel, FM6). Bank changes stay in effect until the next
  `EBxx`.

## Genesis rules for the writer

- One sample at a time on FM6/DAC → see [../channel-mapping.md](../channel-mapping.md).
- Panning: L / R / both only.
- A writer change is "done" only when the output **opens in DefleMask** and plays correctly.
