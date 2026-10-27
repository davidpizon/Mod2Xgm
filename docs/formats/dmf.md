# DMF output format (DefleMask)

**Authoritative spec:** [`DMF_SPECS.txt`](DMF_SPECS.txt) in this folder — "Specs for DMF (DefleMask Module
Format, for DefleMask v1.0.0 and above)", Delek © 2021, copied from https://www.deflemask.com/DMF_SPECS.txt
on 2026-10-27. Older revisions: `DMF_SPECS_0x11.txt`, `_0x12`, `_0x15` on the same site.

> ⚠️ **Same extension, different format.** fileformat.info's `format/dmf/corion.htm` documents the
> X-Tracker / D-Lusion "Digital Music File" (magic `DDMF`). It is **not** DefleMask's format; don't use it.

This page summarizes the spec for the **Genesis** case and lists what the spec leaves unsaid
(marked **Verify** — confirm by saving a file from DefleMask and inspecting it).

## Container

- Whole file is **zlib-compressed** → write with `ZLibStream` (zlib header + Adler-32), not raw `DeflateStream`.
- Multi-byte integers: the spec doesn't state endianness — **little-endian** (Verify).
- Strings: 1-byte length (0–255) + chars; no terminator. Encoding unstated — write ASCII and replace
  anything else with `?` (conversion note).
- Spec notation: "2 Bytes … (-1 = Empty)" means signed 16-bit, `0xFFFF` = empty.

## Layout (in order)

### 1. Format flags
| Size | Field |
|---|---|
| 16 | `.DelekDefleMask.` |
| 1 | File version — **24 (`0x18`)** for DefleMask v1.0.0 → the version we write |

### 2. System
| Size | Field |
|---|---|
| 1 | `0x02` Genesis (**10 channels**: FM1–6, PSG1–4) or `0x42` Genesis ext. CH3 (**13 channels**) |

Writes `0x02`, or `0x42` when the optimizer uses CH3 special mode (combined instruments). Channel order: FM1–FM6 then PSG1–PSG4 (Verify ordering against a saved file).

### 3. Visual information
Song name (len + chars), song author (len + chars), highlight A (1), highlight B (1).
Suggest highlight A = 4, B = 16 (MOD convention).

### 4. Module information
| Size | Field | Notes |
|---|---|---|
| 1 | Time base | |
| 1 | Tick time 1 | ticks per row on even rows |
| 1 | Tick time 2 | ticks per row on odd rows (MOD speed → both equal) |
| 1 | Frames mode | **0 = PAL**, 1 = NTSC |
| 1 | Using custom Hz | 1 = ignore PAL/NTSC |
| 1 ×3 | Custom Hz value 1, 2, 3 | Encoding unstated — **Verify** (likely three digits of the Hz value) |
| 4 | Rows per pattern | MOD = 64 |
| 1 | Rows in pattern matrix | MOD song length (≤ 128; spec field allows ≤ 255) |

(The old 1-byte arpeggio tick speed was removed in ver 11.1 — don't write it.)

**MOD timing mapping:** tick time 1 = tick time 2 = MOD speed. Tick rate = BPM×2/5 Hz; BPM 125 → PAL with
no custom Hz; any other BPM → custom Hz. Mid-song `Fxx` changes become DMF speed / tempo effects (Verify
effect codes in DefleMask's effect list — not part of this spec).

### 5. Pattern matrix
Channel-major: for each of the 10 channels, for each matrix row, 1 byte = pattern index.

### 6. Instruments
1 byte count, then per instrument: name (len + chars), mode (1 byte: **0 = STD**, **1 = FM**).

**FM** (FM1–6): ALG, FB, LFO (FMS), LFO2 (AMS) — 1 byte each; then 4 operators × 12 bytes:
AM, AR, DR, MULT, RR, SL, TL, DT2, RS, DT, D2R, SSGMODE (bit 4 enable, bits 0–2 mode).
Operator order in the file vs. YM2612 slot order — **Verify**.

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
- 1 byte: effect-column count for this channel (1–4; Verify max)
- for each **matrix row** (not each unique pattern), for each row (64):

| Size | Field |
|---|---|
| 2 | Note: 1 = C#, 2 = D, … 11 = B, **12 = C**; 0 (with octave 0) = empty; 100 = note off |
| 2 | Octave |
| 2 | Volume (−1 empty) |
| 2 × 2 × N | N × (effect code, effect value), −1 empty |
| 2 | Instrument (−1 empty) |

Notes:
- C is 12, not 0 — C of octave *n* is probably stored as note 12 with octave *n−1*. **Verify** by
  saving a C note in DefleMask.
- Because pattern data is written once per matrix row, give each matrix row of a channel its **own**
  pattern index (0, 1, 2, …) — simplest and avoids ambiguity when MOD orders repeat a pattern. Duplicate
  data is the cost; it compresses well.

### 9. PCM samples
1 byte count (so **≤ 255 samples**), then per sample:

| Size | Field |
|---|---|
| 4 | SAMPLE_SIZE (number of samples, not bytes) |
| 1 + N | Name |
| 1 | Sample rate code — values **not given in this spec**; Verify (commonly 1–5 = 8000 / 11025 / 16000 / 22050 / 32000 Hz) |
| 1 | Sample pitch — semantics unstated (Verify) |
| 1 | Sample amp — semantics unstated (Verify) |
| 1 | Sample bits: 8 or 16 |
| 2 × SAMPLE_SIZE | Sample data — **always 2 bytes per sample**, even when bits = 8 (Verify signed 16-bit, and value range for 8-bit) |

## Not covered by the spec (need another source)

- Effect codes (e.g. DAC enable on FM6, panning, speed, pitch slides). Use DefleMask's in-app effect list /
  manual and record the ones we use here.
- How a DAC note on FM6 selects a sample (note → sample index mapping).
- Custom Hz encoding, endianness, operator order, sample rate codes — see Verify items above.

## Genesis rules for the writer

- One sample at a time on FM6/DAC → see [../channel-mapping.md](../channel-mapping.md).
- Panning: L / R / both only.
- A writer change is "done" only when the output **opens in DefleMask** and plays correctly.
