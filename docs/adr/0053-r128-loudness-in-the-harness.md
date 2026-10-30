# ADR 0053: EBU R128 loudness in the harness (R128Net)

- **Status:** Proposed
- **Date:** 2026-10-30

## Context

Loudness keeps coming up: David hears FM voices far off the MOD's levels ([ADR 0050](0050-voice-levels.md)), and the
balance distance ([ADR 0051](0051-instrument-balance-metric.md)) measures it per instrument with a K-weighting written
in `src/Scoring/BalanceMetrics.cs`. The harness has no standard loudness figure for a whole render, no loudness range
(does the conversion flatten the dynamics?) and no true peak (does it clip?). Its overall gain is an ungated mean power,
so silences and fades count.

A search of NuGet (2026-10-30) for audio, tracker and comparison packages found one that fits: **R128Net** 1.1.2, a
C# port of libebur128 (the reference EBU R128 / ITU-R BS.1770 meter), MIT, net10.0, no dependencies, annotated for
Native AOT, and checked bit for bit against libebur128 built from C. It is new (October 2026, about 2 000 downloads, one
maintainer). David asked for a trial in the harness.

## Decision

**R128Net is added to the harness only** (`tools/Mod2Xgm.Harness` and its tests), never the converter, while it is
on trial. What it measures:

- **Every render and clip** (`Loudness.cs`), in stereo: gated integrated loudness (LUFS), loudness range (LU) and true
  peak (dBTP), for the original and each rendering. In `loudness.csv` and a report section.
- **Every balance stem:** a second offset, `r128_offset_lu` in `balance-instruments.csv`: the stem's gated loudness
  next to its mix in the conversion, minus the same in the original. Gating skips the moments an instrument is silent.
- **A cross-check test** of `BalanceMetrics`' K-weighting against libebur128's momentary loudness.

### The trial (2026-10-30)

Two corpus runs with `--clip 30 --balance`, Furnace:

- **Rated converter:** `main` with `src/Dmf/DmfPatternTranslator.cs` from `fdcd743`, i.e. before the voice-level fix
  ([ADR 0050](0050-voice-levels.md)), which is the converter whose clips David rated (`listening.tsv`). It reproduces the
  committed baseline exactly: all 19 clip log-mel scores and every balance offset are identical.
- **Current converter:** `main` at `da8034a`.

Spearman ρ between −measure and David's ratings of the rated clips (19 songs, positive = agrees with the ears; p by
permutation):

| Measure | ρ | p |
|---|---:|---:|
| Log-mel distance (today's combined score) | +0.35 | 0.14 |
| \|Δ integrated loudness\| of the clip (R128) | +0.22 | 0.36 |
| \|Δ loudness range\| of the clip (R128) | −0.10 | 0.69 |
| \|Overall gain\| (BalanceMetrics, ungated) | −0.03 | 0.90 |
| Balance distance (BalanceMetrics, ADR 0051's primary) | +0.02 | 0.93 |
| **Mean \|R128 stem offset\|** (gated, per instrument) | **+0.42** | 0.07 |
| Log-mel + w × mean \|R128 stem offset\|, w = 0.25 / 0.5 / 1 / 2 | +0.40 / +0.37 / +0.36 / +0.44 | |

- **Whole-mix loudness doesn't follow the ears**, any more than the ungated gain did (ADR 0051).
- **The gated per-instrument offset is the best single measure tried so far** (+0.42, above log-mel's +0.35), and the
  first loudness measure that agrees at all. It is not established: it was picked after looking (six measures tried
  here), 19 songs is few, p = 0.07, and adding it to log-mel doesn't help reliably. It is a candidate for the paired
  test ADR 0051 proposes, next to the balance distance.
- **Per instrument the two offsets agree** (134 audible stems, Pearson r = 0.85). They part on sparse or quiet stems:
  R128 gates each stem on its own and has no floor, so an instrument heard briefly near the floor can read +18 LU
  (`jam_-_latenightcoffee` sample 13, −40 dB in the original), where BalanceMetrics clamps it at 30 dB below the mix.
- **ADR 0050's effect shows up** on the clips: mean \|Δ loudness\| 3.4 → 1.5 LU, mean \|R128 stem offset\| 3.70 → 2.54 LU,
  balance distance 5.31 → 4.48 dB. Whole songs: the rated converter was 2.4 LU louder than the original on average
  (−5.0 … +9.0) and its true peak went above 0 dBTP in 5 songs; the current one is 1.0 LU quieter (−5.3 … +1.7) and
  peaks at −0.4 dBTP at most. libopenmpt's own renders go above 0 dBTP in 3 songs (`cancan`, `drozerix_-_neon_techno`,
  `hit`, up to +0.7), so the reference clips a little too.

### BalanceMetrics' K-weighting against libebur128

A test (`LoudnessTests`) feeds the same 400 ms to both. BalanceMetrics reads **up to 0.3 dB low**, for two reasons:

- BS.1770's high pass, as libebur128 builds it, passes +0.04 dB above unity (the 48 kHz coefficients in the standard
  do too); BalanceMetrics' cookbook high pass is normalized to 0 dB. A constant, so it cancels in every offset.
- BalanceMetrics' shelf is the audio-EQ-cookbook shelf with pyloudnorm's parameters; libebur128 builds it differently.
  The two curves part by up to 0.22 dB around 1 kHz (0.17 at 3 kHz, under 0.01 below 100 Hz and above 6 kHz).

Both sides of a balance offset are the same instrument, so this hardly moves the offsets. BalanceMetrics is left as it
is; matching libebur128's filter design would be a small change in `src/Scoring` if it ever matters.

## Options considered

- **R128Net in the harness** (chosen): the reference meter's numbers, in-process, no native library.
- **R128Net in the converter's scoring** (`src/Scoring`): the optimizer (M9) could use it. Not yet: a new package with
  one maintainer goes in the shipped binary only once the harness shows it earns its place.
- **Writing gating and LRA by hand** next to `BalanceMetrics`: no dependency, but a second hand-written meter to keep
  right. The point of the trial is an independent reference.
- **libebur128 itself** (native, MIT): the original, but native builds for three platforms, which R128Net avoids.
- **NAudio, FftSharp, SoundFingerprinting, MathNet.Numerics**: looked at in the same search. Not needed here: the harness
  already reads and writes WAV and has its own FFT ([ADR 0042](0042-scoring-metrics.md) chose that), and fingerprinting
  answers "the same recording?", not "how close?".

## Consequences

- `Directory.Packages.props` (a Harness group), `THIRD-PARTY-NOTICES.md`, AGENTS.md's list of approved libraries.
- `tools/Mod2Xgm.Harness`: `Loudness.cs`, `loudness.csv`, a report section, `r128_offset_lu`. [harness.md](../harness.md)
  describes them.
- No change to the converter, the combined score or the committed baseline.
- Accepting this ADR (David) keeps R128Net in the harness. Moving it into `src/Scoring` for the optimizer (M9) needs its
  own ADR, and the paired listening test first: if the gated stem offset follows the ears within a song, it is the
  loudness term to try in the combined score.
