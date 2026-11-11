# ADR 0100: Perceptual features for the learned listening score

- **Status:** Proposed
- **Date:** 2026-11-10

## Context

[ADR 0097](0097-learned-listening-score.md) fits a listening score from David's blind A/B answers: a pairwise
logistic model over distances between a render and the original. Its feature list adds four perceptual measures to what
`src/Scoring` already has (log-mel, MFCC, onset F1, balance): a PEAQ-style noise-to-mask ratio, NSIM on an ERB
spectrogram, envelope modulation and harmonicity. Plan T9.4k held them, with the fit, the pair picker and the
pretrained-model trial.

David (2026-11-10) split T9.4k into four pull requests: **T9.4k1** these features, **T9.4k2** the fit and its report next
to log-mel, **T9.4k3** the pair picker and a round of about 10 pairs it picks, **T9.4k4** pretrained embeddings in the
harness through ONNX Runtime. The answers file has only 5 answers so far, all one way (round 20), so no weight can be
fitted yet. The k3 round gets the first real data. The plan's budget for the features is **20 ms per pitch region**,
so they can later rank FM fits (T9.4m) as well as clips.

David judges by ear, not by DSP detail (he asked that technical choices be made and recorded, not put to him). So the
choices below are Claude's, for David to review here.

## Decision

`PerceptualMetrics.Compare(reference, candidate)` in `src/Scoring` returns a `PerceptualReport` with four distances
from the original, lower closer. `SongScorer` measures them after alignment, so every harness score has them. They are
**features only**: nothing in the converter reads them, no plan changes, and log-mel stays the main number until the
learned score is adopted (T9.4l).

All four share one **ERB spectrogram** (`ErbSpectrogram`): 40 triangular bands evenly spaced in ERB numbers
(Glasberg and Moore) from 50 Hz to 16 kHz (or 95 % of half the rate), a Hann frame of the power of two nearest 46 ms
(2048 points at 44.1 kHz), hop a quarter frame, powers scaled like `MelAnalyzer` (a full-scale sine's peak bin is 1).
Frames more than 60 dB below the loudest frame on both sides, or below −80 dB, are not counted.

| Feature | What it measures | How |
|---|---|---|
| `nmr_db` | A difference the original doesn't mask | Per frame and band, the difference between the two **magnitude** spectra (a chip render isn't in phase with the original, so a sample-wise difference would be all noise), over the original's masked threshold: its band powers spread 15 dB/ERB down and 6 dB/ERB up (PEAQ's Bark slopes, 1 Bark ≈ 1.6 ERB), 5 dB under. The mean over bands and counted frames, in dB (PEAQ's total NMR). −80 when nothing differs; above 0 a difference is likely heard |
| `nsim_distance` | Local structure of the spectrogram | 1 − NSIM (Hines et al., as in ViSQOL) on both spectrograms in dB above −80: intensity × structure over a 3×3 Gaussian (σ = 0.5 cell), C1 = (0.01·80)², C3 = (0.03·80)²/2; the mean over counted cells |
| `modulation_db` | How each band's loudness moves (tremolo, roughness, rhythm inside a note) | Per band with sound, the amplitude envelope's spectrum over the whole signal, mean removed before a Hann window, as power in octaves 0.5–32 Hz over the mean's power. That is depth, so a level change alone doesn't count. The mean absolute dB difference |
| `harmonicity_db` | How tonal the sound is: a pitched note turned buzzy or noisy | Per frame (no overlap), Boersma's harmonics-to-noise ratio: the highest peak of the normalized autocorrelation from 1 kHz down to a third of the frame, over the window's own, in dB within −10…30. Above 32 kHz the signals are first halved in rate (pairs averaged), since pitch needs no more. The mean absolute difference over frames with sound |

**Speed.** Both signals of a comparison go through one complex FFT (one in the real part, one in the imaginary part),
for the spectrogram and for both autocorrelation transforms. One second at 44.1 kHz takes 8.7 ms (median, Release,
David's machine), under the 20 ms budget. A test checks the budget. It is tagged `Category=Performance`, which the CI
coverage job skips, since instrumentation makes code about 8 times slower.

**Where they show.** The harness's `scores.csv` and `clip-scores.csv` gain `nmr_db`, `nsim_distance`, `modulation_db`,
`harmonicity_db`. `--remake` also writes `listening/features.tsv`, every row's two clips measured against the
original's clip: the table T9.4k2 fits on.

## Options considered

- **The four features, simplified and in pure C# (chosen).** Each takes the idea of a published measure and keeps
  what applies to two renders that aren't in phase. They are deterministic, AOT-safe and within the time budget. The
  learned weights (T9.4k2) say whether each one earns its place.
- **Full PEAQ (ITU-R BS.1387) or ViSQOL as published.** PEAQ's model is large (FFT and filter-bank ears, 11 output
  variables, a neural network trained on codec tests), and it assumes the test signal is the reference plus coding
  noise, in phase. ViSQOL is C++ with its own patch alignment. Both are tuned to codecs and speech, not chip
  renditions (ADR 0097). Their best ideas are here as features instead.
- **A gammatone filter bank instead of FFT triangles.** It is closer to the ear, but costs a filter per band per sample,
  far over the 20 ms budget. Triangles on an FFT are the usual stand-in (ViSQOL v3 also works from a spectrogram).
- **Time-domain noise (render minus original) for the NMR.** It is meaningless here: FM and PSG renders never line up
  in phase with the MOD's samples, so every render would read as all noise.

## Consequences

- New `src/Scoring/ErbSpectrogram.cs` and `src/Scoring/PerceptualMetrics.cs`; `ScoreReport` gains `Perceptual`.
- [harness.md](../harness.md): the new `scores.csv` columns and `features.tsv`. [testing-principles.md](../testing-principles.md):
  the `Category=Performance` trait.
- Plan: T9.4k becomes T9.4k1–k4 (David). The constants here (slopes, offset, octave edges, frame) are first guesses.
  They change only if the fit (T9.4k2) shows a feature is hurt by one, and that change is recorded in a new ADR.
- Nothing in the converter's output changes: every listening row still remakes byte for byte.
