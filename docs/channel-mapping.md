# Channel mapping

> Status: **decided** (2026-10-27). First DMF release uses a **simple mapper** (busiest percussive channel →
> DAC, other channels → FM from a built-in patch library). It is then replaced by the **multi-pass
> optimizer** ([multi-pass.md](multi-pass.md)), which chooses FM / PSG / PCM per instrument and per section
> by measured accuracy. Both sit behind `IChannelMapper`. The options below are kept as background.

**Implemented (plan T5.3, [ADR 0040](adr/0040-simple-mapper.md)):** the simple mapper decides per instrument: a sample
that loops goes to FM (one of five built-in patches, by brightness), a sample that plays once goes to the DAC, notes
from any MOD channel; a clash on the DAC is won by the louder note and the loser moves to the PSG noise channel (if
noise) or FM. `--no-dac` keeps everything off the DAC. Mixing clashing drums into one sample is left to the optimizer
as a separate candidate.

## Decision criterion

The project goal is the **most accurate *sounding*** result (see [README.md](README.md#goal)). Strategies are
compared by measured and listened accuracy (see [testing.md](testing.md)). ROM size and DefleMask editability
are secondary. Sample capacity and consolidation are covered in [sample-capacity.md](sample-capacity.md).

## The problem

A 4-channel MOD plays **4 PCM samples at once** at arbitrary pitch and volume. The Genesis has:

| Target | Hardware PCM | Notes |
|---|---|---|
| DMF (DefleMask, Genesis) | **1** sample at a time on FM6/DAC | fixed pitch per sample (the note picks the sample, [formats/dmf.md](formats/dmf.md#dac-samples-on-fm6)); FM1–5 + PSG free |
| XGM | **4** mixed PCM channels (~14 kHz, fixed pitch) | FM1–5 + PSG free |
| XGM2 | **3** mixed PCM channels (13.3 / 6.65 kHz, fixed pitch) | FM1–5 + PSG free |

XGM/XGM2 PCM plays samples at a fixed rate — **no pitch shifting**. A MOD melody played by one sample at
many pitches needs either one pre-pitched copy per note (ROM cost) or a non-PCM substitute.

## Options

### A. Hybrid (percussion on PCM, melody on FM/PSG)
Classify each MOD instrument: short / unpitched / few distinct notes → PCM; looped / many pitches → FM
patch (or PSG for simple square-like tones).
- ✅ Sounds idiomatic on Genesis; small ROM; fits all three targets.
- ❌ Needs instrument classification and FM patch approximation (hard to do well automatically);
  timbre changes.

### B. All PCM
Every MOD channel stays a sample.
- DMF: only one DAC → need **priority/stealing** (e.g. most recent note, or lowest channel wins) or offline
  **pre-mixing** of channels into one rendered sample stream (loses editability).
- XGM/XGM2: 4 / 3 channels; pitches done by **pre-rendered sample variants per (sample, note)**.
- ✅ Closest to the original sound on XGM. ❌ DMF heavily lossy; ROM size explodes with many pitches;
  XGM2 has only 3 channels for 4 MOD channels.

### C. All FM/PSG
Every instrument approximated by an FM patch or PSG.
- ✅ Tiny output, fully editable in DefleMask. ❌ Drums and voice samples sound wrong.

### D. User-guided
Auto-mapping (A) plus an optional per-instrument override file (`song.mod2xgm.json` — must use
System.Text.Json source generation for AOT) to force PCM/FM/PSG and pick FM patches.

## Things to decide

1. Default strategy per output format (may differ: DMF vs XGM).
2. DAC collision policy for DMF.
3. Max ROM budget for pitched PCM copies (XGM/XGM2).
4. Where FM patches come from: built-in library, `.dmp`/`.tfi` patch files, or generated.
5. Whether XGM2's 3-channel limit takes channel 1 for music and leaves 2–3 to SFX (driver convention) or uses all 3.

## Recommendation to evaluate first

Build the accuracy harness first, then compare:
- **XGM/XGM2:** **B (all PCM)** with consolidation as the baseline — closest to the original sound — against **A**.
- **DMF:** **A (hybrid)** is the likely winner, since DMF has a single DAC.

C is still useful as a quick end-to-end check of writers and timing, but it isn't a candidate default given the goal.
