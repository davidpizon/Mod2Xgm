# ADR 0049: Simple-mapper baseline and score calibration (T6.4)

- **Status:** Accepted; changes the onset weight of [ADR 0042](0042-scoring-metrics.md)
- **Date:** 2026-10-30

## Context

Plan T6.4 records how the simple mapper ([ADR 0040](0040-simple-mapper.md)) scores on the corpus
([ADR 0046](0046-mod-corpus.md)). Every later mapper, the optimizer (M9) first, is measured against these numbers. ADR 0042
left the combined score's onset weight (3 dB × (1 − onset F1)) provisional. It also left open whether the log-mel or the
MFCC distance follows listening better. Both were to be settled on the corpus against real ears. The harness
([ADR 0047](0047-accuracy-harness.md)) makes the numbers.

## Decision

- **The baseline is Furnace's render** of each conversion, scored against libopenmpt (David). DefleMask exports are left
  out: they are made by hand, so they can go stale. The new `--no-deflemask` option makes the run Furnace-only.
- **The baseline is committed** in [tests/golden/harness-baseline/](../../tests/golden/harness-baseline/README.md):
  `report.md`, `scores.csv`, `clip-scores.csv`, `durations.csv` and David's ratings, `listening.tsv` (David). Later runs
  are compared with it by hand.
- **Calibration by listening, on 30-second clips** (David). The new `--clip 30` option scores each song's first 30
  seconds on their own. It also writes those seconds as WAVs, so the ears and the numbers judge the same audio. David
  played each pair, the original and then the Genesis version, and rated it 1–10 (10 = sounds almost the same).
- **What the ratings showed** (19 songs; Spearman ρ between the clip measure and the ratings, where positive means the
  measure agrees with the ears):

  | Measure | ρ | Kendall τ |
  |---|---:|---:|
  | Log-mel distance | 0.35 | 0.29 |
  | MFCC distance | 0.29 | 0.21 |
  | 1 − onset F1 | 0.07 | 0.07 |
  | Log-mel + 3 dB × (1 − F1), ADR 0042's combined score | 0.29 | 0.25 |
  | Log-mel + w × (1 − F1), best w | 0.35 at w = 0 | 0.29 |
  | MFCC + w × (1 − F1), best w | 0.30 at w = 12.2 | 0.21 |

  With 19 songs, ρ = 0.35 is about what luck gives (p ≈ 0.14). **No metric follows listening well yet.** The onset term
  made the agreement worse at every weight tried, so:
- **The onset weight is now 0** (`ScoreOptions.OnsetWeight`; David approved recording it this way). The combined score
  is the log-mel distance alone. Onset precision, recall, F1 and timing are still reported. **Log-mel stays the main
  number**: MFCC did no better.
- Claude also tried a few whole-mix measures on the clips: the level difference, log-mel with the overall gain matched,
  log-mel with each band's gain matched, the spectral balance error, and the loudness envelope. None agreed better
  (all |ρ| < 0.2).
- **Why the metric misses what David hears.** David noticed the **relative volume of FM channels** is often far from the
  MOD's. The converter sets an FM note's volume from the MOD volume column alone (`DmfUnits.FmVolume`). It doesn't
  account for how loud the sample is next to how loud the FM patch is. A score of the whole mix hardly sees one
  instrument too loud or too quiet. The best-rated song, `soundtracker15` (7), has the worst log-mel score.

### The baseline (Furnace, whole songs, 44.1 kHz mono)

Averages over the 19 songs: **log-mel 11.07 dB, MFCC 67.87, onset F1 0.66**. Closest: `fx-poly1` (7.02 dB). Farthest:
`soundtracker15` (15.06 dB). Per song: [report.md](../../tests/golden/harness-baseline/report.md). Every song's timeline
is within one tick of libopenmpt. Two reruns gave byte-identical score and length files.

Other findings from the run, recorded for later work:

- `drozerix_-_the_hawks_claw` and `sharpojects_cantpleaseeveryone` render about 30 s and 25 s short: their pattern loops
  (`E6x`) play once, because DMF has none ([ADR 0044](0044-dmf-effects-and-song-grid.md)).
- On a few songs the alignment lag sits near the edge of its ±1 s search (`musix-rubbish` −975 ms, `kc-dancinonamiga`
  743 ms on whole songs). Those songs' log-mel numbers may come from a wrong lag rather than from the sound.

## Options considered

- **Record the baseline and the disagreement; onset weight to 0** (chosen, David). It is honest about what the ratings
  show and changes only what they support.
- **Tune the weight to the best fit and call it calibrated**: overfits 19 noisy ratings. The best fit is 0 anyway.
- **Build a per-instrument balance score in this task** (render each instrument alone, compare levels): it may be what
  the ears follow, but it is new harness work and needs its own design. Left as a follow-up.
- **Fix FM loudness first, then take the baseline**: it changes the thing being baselined. The plan measures the simple
  mapper as it is.
- **DefleMask as the official renderer**: it is the real target, but each export is made by hand and goes stale.
- **Whole-song listening**: over an hour of listening. The 30-second clips keep the ears and the numbers on the same
  audio.

## Consequences

- `src/Scoring/SongScorer.cs`: `ScoreOptions.OnsetWeight` defaults to 0. [testing.md](../testing.md) and the harness
  report say the combined score is the log-mel distance and agrees only weakly with listening.
- `tools/Mod2Xgm.Harness`: `--clip <seconds>` (clip scores, `clip-scores.csv`, `wav/*.clip.wav`) and `--no-deflemask`
  ([harness.md](../harness.md#options)).
- Follow-ups: **FM loudness** (match each FM voice's level to its sample's), and a **score that hears balance**
  (per-instrument levels), checked against these ratings. Until then, a better corpus score is a hint, not proof, that a
  conversion sounds closer. Changes that matter should get a listening check too.
- A mapper change compares its run with `tests/golden/harness-baseline/`. A change that lowers the corpus score needs a
  recorded reason ([testing.md](../testing.md)).
