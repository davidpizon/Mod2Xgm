# ADR 0096: FM confidence per sample

- **Status:** Proposed
- **Date:** 2026-11-09

## Context

With `--mapping optimizer` and no `--max-pcm-bytes` ([ADR 0093](0093-optional-pcm-budget.md)), every sample that has a
recording plays it. The allocator counts a recording's distance as 0 ([ADR 0087](0087-greedy-allocator.md)): the
log-mel ruler read FM as closer (fitted FM median 6.68 dB, plain recordings 7.98–9.09 dB) and David's ears disagreed.
FM only plays a sample when a limit runs out, a note bends, or `--require-fm` / `--no-dac` asks for it.

David (2026-11-09) wants recordings used only when FM is clearly worse. That is the cheap rendering, and he expects porting
an FM-like instrument as FM to cost least. He asked for three things:

- a ranked list of the samples with the confidence that FM can play each;
- a threshold to set it by;
- `--max-pcm-bytes` staying the hard wall.

The scores agree only weakly with his ears (Spearman ρ 0.35 for log-mel, [ADR 0049](0049-simple-mapper-baseline.md)).
So the confidence is learned from his answers rather than read off the ruler. He also asked for a better measure than
loudness and log-mel to predict it.

The work is split into plan tasks T9.4d–T9.4i. This ADR covers T9.4d (features and plumbing, no change to any plan) and
fixes what the later tasks build on.

## Decision

- **What the confidence means (David).** It is the chance that David picks the FM version, or can't tell the two apart.
  The comparison is the whole song mixed against the original, with that one sample on FM in one version and as a
  recording in the other. "Can't tell" counts for FM, because FM saves space.
- **One number per sample (David).** Each pitch region gets a chance from its patch's features. The sample's chance is
  their mean, weighted by the importance ([sample-capacity.md](../sample-capacity.md#importance)) of the notes the song
  plays in each region. A sample the song never plays weighs its regions alike. A region without a patch takes no part.
- **Features (T9.4d).** Each region has:
  - the fit's own score: log-mel, MFCC and harmonic distances;
  - the attack distance: loudness per 10 ms frame over the first 100 ms, level-matched, as the mean absolute dB;
  - the envelope distance: the same over the region's whole second;
  - the brightness distance: the mean absolute difference of the power-weighted mean mel band, over frames both sound in.

  Each sample also carries:
  - its tonal, noisy and loop traits, its pitch deviation, and whether it is a chord;
  - its share of the song's importance (exposure);
  - the share of its importance in bent notes.

  These are measured from the render the menu already makes to set the patch's level, so no extra FM is rendered. They
  are not cached, so `CandidateCache` is unchanged. T9.4h adds perceptual measures: a PEAQ-style noise-to-mask ratio,
  NSIM on an ERB spectrogram, an envelope-modulation distance and harmonicity.
- **Provisional model (T9.4d).** `p = 1 / (1 + e^((MelDb − 6.96) / 1.5))`.
  - 6.96 dB is the corpus median of the fits without the LFO ([ADR 0069](0069-fm-patch-fitting.md#on-the-corpus)),
    which is what the plan plays.
  - It ranks samples until the listening rounds of T9.4i fit the model to David's answers. It decides nothing yet.
- **Reasons the confidence doesn't decide.** These are, in order:
  1. `--require-fm`, `--require-dac`, `--require-psg`, which stay hard constraints;
  2. no steady pitch;
  3. no patch;
  4. no recording (`--no-dac`, or none could be made).
- **Planned for T9.4e–T9.4f.**
  - With `--fm-confidence <0–100>` (θ), a sample with `p ≥ θ` wants FM, and its recording becomes the fallback before
    the PSG.
  - When the recordings wanted still don't fit `--max-pcm-bytes` after the gentle shrink moves, the least-needed
    recording is moved to FM first. "Least needed" means the lowest `(1 − p) · importance / cost`, where cost is the
    larger of its share of the bytes and its share of the slots.
  - After that, consolidation works as now: swap first, shrink, pre-mix steals, rescue.
  - The ranked table prints whenever the optimizer runs.
  - The default θ is 100 % (today's behaviour) until T9.4i sets it.
- **Calibration (T9.4g, T9.4i, David).**
  - About 40 blind either/or questions in 4 rounds of about 10, on samples from at least 8 songs.
  - Each round after the first asks about the samples the current model is least sure of (p nearest 0.5).
  - A final round on 5 songs never used for tuning checks the model once.
  - Clips are 20 s of the whole song mixed, pitch-checked and level-checked, with versions unnamed until graded.

## Options considered

- **A probability learned from David's answers, starting from the ruler (chosen).** It is the only number that follows
  what he hears. Active learning keeps the listening to about 40 questions.
- **A fixed formula on the ruler's numbers.** Faster and needs no listening, but the ruler ranked recordings behind FM
  where his ears ranked them first (ADR 0087). That is the disagreement this work fixes.
- **A threshold only, or a size only.** David chose both, with the size as the wall. A size alone hides why a sample went
  to FM. A threshold alone can't keep a song inside a budget.
- **A confidence per pitch region.** It is more exact, but the list is longer and harder to read. David chose one per
  sample.
- **Clips of the sample alone.** These make differences easier to hear, but are stricter than the song is: a difference
  the mix hides doesn't matter. David chose the whole song mixed.

## Consequences

- `RegionSounds` gains `Features` and `SampleSounds` gains `Traits`. `SoundMenu` measures them from the render that sets
  each patch's level.
- `OptimizerMapper.Confidence` gives every sample's confidence after `Map`, even when the song can't fit. It changes no
  plan.
- The provisional constants live in `FmConfidenceModel`. T9.4i replaces them, in an ADR that supersedes this one's model.
- Follow-up tasks are T9.4e (allocator), T9.4f (table and `--fm-confidence`), T9.4g (survey tooling in the harness),
  T9.4h (perceptual features) and T9.4i (rounds, hold-out and default).
