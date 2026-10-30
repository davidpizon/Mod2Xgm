# ADR 0042: Scoring metrics (T6.2)

- **Status:** Accepted; the onset weight is superseded by [ADR 0049](0049-simple-mapper-baseline.md)
- **Date:** 2026-10-28

## Context

The goal is the most accurate *sounding* rendition ([AGENTS.md](../../AGENTS.md)). Plan T6.2 builds the comparison that
measures it, in pure C# in `src/Scoring`, for the accuracy harness (T6.3) and the optimizer's candidate tournament (M9):
render the MOD, render a candidate output, and say how close they sound. The literature
([fm-matching.md](../references/fm-matching.md)) points to multi-resolution log-mel distance with MFCC distance tracked
alongside, plus an onset/timing check ([testing.md](../testing.md#accuracy-harness-drives-design-decisions)).

## Decision

- **Pure C#, no new package.** An own radix-2 FFT (`FftPlan`), Hann window, mel filterbank (`MelAnalyzer`), DCT and peak
  picking. NWaves is approved (ADR 0038) but its STFT/MFCC code is not needed, and an own implementation is
  deterministic across platforms.
- **Input is mono audio at one sample rate** (`AudioSignal`). Stereo is averaged: the score judges the sound, not the
  stereo position. Signals at different rates are refused (resample first).
- **Log-mel in dB, floored at −80 dB** below a full-scale sine, so silence and near-silence are the same.
- **Main metric: multi-resolution log-mel distance** in dB: the mean absolute difference over frames and bands, at four
  scales (FFT 512/1024/2048/4096, hop ¼ frame, 32/48/64/80 mel bands), averaged. 0 is identical; a signal 6 dB quieter
  scores 6. The shorter signal counts as followed by silence, so missing music is penalized.
- **MFCC distance** tracked alongside: 13 coefficients (c1…c13, c0 left out so loudness is not counted twice) from 40
  mel bands (FFT 1024, hop 256), mean Euclidean distance per frame. It follows timbre and ignores level.
- **Note starts** by spectral flux (the average dB rise per band between frames), peak-picked against the local
  average (+1.5 dB, 0.4 s window, 30 ms apart), matched one-to-one within 50 ms. Reported as precision, recall, F1 and
  mean timing error. Frames running past the end of the audio are ignored: the cut-off is not a note start.
- **Alignment** before comparing: the lag that maximizes the cross-correlation of the two flux curves within ±1 s,
  smaller lags first, to a precision of one curve step (256 samples, 5.8 ms at 44.1 kHz). The candidate is moved by the
  lag found.
- **Combined number** (to minimize) = log-mel distance + 3 dB × (1 − onset F1). This weight is **provisional**: it is
  calibrated on the corpus in T6.4, where the MFCC and log-mel metrics are also compared against listening.
- **Every step is logged** at Debug (`SongScorer.Compare` takes an `ILogger`), so the optimizer can describe each
  candidate's score.

## Options considered

- The chosen set above.
- NWaves for STFT/MFCC: rejected, little saved and floating-point behaviour would depend on the package.
- Phase-aware or waveform distances: rejected, a Genesis rendition never reproduces the waveform, and perception does
  not follow phase.
- Beat-tracking alignment (tempo and phase): rejected for now; a single constant lag covers the renderers' start-up
  offsets. Drift would need a tempo-aware method (revisit with the T6.3 corpus results).
- Real-FFT packing and parallel frames for speed: left for T12.2; a few minutes of 44.1 kHz audio takes seconds.

## Consequences

- `docs/testing.md` describes the scoring and its limits; `docs/architecture.md` lists `Scoring`.
- T6.3 (harness) feeds it WAVs from openmpt123 and DefleMask/emulator captures; T7 renderers and M9 use it directly.
- The weights, scales and thresholds are named constants or `ScoreOptions`, so calibration (T6.4) does not change the API.
