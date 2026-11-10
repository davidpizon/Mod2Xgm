# ADR 0097: A listening score learned from David's blind answers

- **Status:** Accepted (David, 2026-11-10)
- **Date:** 2026-11-09

## Context

Every automatic choice the converter and the harness make is ranked by one ruler, the multi-resolution log-mel distance
([ADR 0042](0042-scoring-metrics.md), [ADR 0049](0049-simple-mapper-baseline.md)). It ranks FM fits
([ADR 0069](0069-fm-patch-fitting.md)), the allocator's sounds ([ADR 0087](0087-greedy-allocator.md)) and the harness's
corpus table. T9.5's refine loop is also meant to be judged by it.

That ruler agrees only weakly with David's ears. Spearman ρ against his 19 clip ratings
(`tests/golden/harness-baseline/listening.tsv`):

| Measure | ρ | Where |
|---|---:|---|
| Log-mel distance (today's ruler) | +0.35 (p ≈ 0.14) | ADR 0049 |
| MFCC distance | +0.29 | ADR 0049 |
| 1 − onset F1 | +0.07 | ADR 0049 |
| Balance distance | +0.02 | [ADR 0051](0051-instrument-balance-metric.md) |
| Mean \|R128 stem offset\| | +0.42 (p = 0.07), picked after looking | [ADR 0053](0053-r128-loudness-in-the-harness.md) |

No single measure tried so far follows the ears. So since ADR 0087 every rule that matters has been settled by blind A/B
rounds, and the ADRs record many of them (ADR 0094 reached round 19). Twice a version that measured better lost by ear:
ADR 0087's hawks_claw (graded 3 and then 2, against 5), and the recordings that log-mel ranked behind FM
(ADR 0087, [ADR 0096](0096-fm-confidence.md)).

Listening works, but it costs a round per decision, and it can't reach the thousands of choices the optimizer makes
inside each song. Better search can't make up for that: a search that gets closer by log-mel can still sound worse.

ADR 0096 already learns one narrow question from David's answers: is FM good enough for this one sample? This ADR
proposes the general version, a ruler learned from the same kind of answers that can stand in wherever log-mel stands
today.

## Decision

Proposed by Claude and accepted by David on 2026-11-10. The items still marked **open** are David's to settle; his answers to
the others are recorded where they were asked.

- **What it predicts.** Given two Genesis renders A and B of the same clip of the same song, it gives the chance that
  David prefers A, with the original MOD heard first. That is the question every round asks already. Pairs are easier to
  answer and to model than ratings out of 10 that are compared across songs.
- **The model.** A pairwise (Bradley–Terry) logistic model on the features' differences:
  `P(A ≻ B) = σ(w · (f(B) − f(A)))`, where each `f` is a distance from the render to the original. It has no intercept,
  so swapping A and B flips the chance. "Can't tell" counts as half a win for each side.
- **What it gives the code.** The score `s(x) = w · f(x)` is a single number per render, lower is closer, so it drops in
  wherever the log-mel distance is used today. It is linear and small, so the weights are constants in `src/Scoring`
  (pure C#, deterministic, AOT-safe, no new library).
- **Features.** At most about one weight per 10 answers, chosen on cross-validation, from:
  - what `src/Scoring` already measures: log-mel, MFCC, onset F1, balance, and the R128 stem offset where stems exist;
  - ADR 0096's attack, envelope and brightness distances, at the scale of a whole clip;
  - perceptual features (plan T9.4k, which took them over from the dropped T9.4h): PEAQ-style noise-to-mask ratio, NSIM on an ERB spectrogram, envelope modulation and
    harmonicity.
- **Pretrained audio models (harness only).** Distances between embeddings from pretrained models (for example CDPAM,
  PANNs or CLAP) are tried as extra features in `tools/Mod2Xgm.Harness` only. A model of this kind moves into the
  converter only if it beats the pure-C# features on the hold-out, and only after a separate ADR shows its runtime meets
  the library rules (Native AOT on win-x64, linux-x64 and osx-arm64, permissive licence, in-process, offline). **David
  (2026-11-10):** try them, in the harness first; which ones is chosen when T9.4k starts.
- **One record of answers.** Every blind round, for any decision, adds rows to one tab-separated file,
  `tests/golden/listening/answers.tsv`. Each row holds:
  - the date, the round and the question;
  - the song, the clip's start and length, and the output format;
  - a recipe for A and for B: the commit and the options or rules that make each render;
  - David's pick (A, B or can't tell), the grades if he gave any, and his words.

  Renders are deterministic, so the recipe remakes the clip. The WAVs are not committed (the corpus is free to share,
  but the clips are large). ADR 0096's rounds (T9.4i) are pairs too, so they go in the same file and train both models.
- **The rounds already done.** Rounds from ADRs 0087–0094 are added to the file when both versions can still be made
  from today's code with options. The others stay in their ADRs as history. **David (2026-11-10):** only answers whose
  clips today's code remakes byte for byte go in, and rounds graded 1–5 stay out (they aren't turned into pairs). How
  many that leaves is found by trying (T9.4j).
- **More answers.** At first there are no rounds held only for this score: each round David runs for a decision is
  logged and teaches the score as a side effect. When the model is unsure, a round may add a few pairs where it is
  least sure (chance near 0.5), using a pair picker (plan T9.4k, which took it over from the dropped T9.4g). **Open:** whether David wants such extra pairs, and
  how many per round.
- **Fitting.** In the harness, with L2 regularization. Every choice (features, regularization) is made on
  leave-one-song-out checks. The fitted weights, the date and the number of answers are written next to the constants.
- **Adoption gate.** The learned score is reported next to log-mel in the harness from the first fit. It replaces log-mel
  as the main number only after it beats log-mel at predicting David's picks on a hold-out of songs never used for
  fitting. **Open:** the bar. Claude suggests at least 30 hold-out pairs, at least 70 % agreement, and at least 10
  points above log-mel's agreement on the same pairs.
- **Using it.** Each use is its own task, with a blind round against the version David already liked: the harness's
  main number, picking the best FM fit (step 4 in [fm-matching.md](../references/fm-matching.md)), the allocator's
  distances, and T9.5's refine loop. A new ruler changes plans, and a search
  pushed against any ruler finds its blind spots. So the answers keep being logged, and the model is refitted when
  they disagree with it.

## Options considered

- **A pairwise score learned from blind A/B answers (proposed).** It learns from the question David already answers well,
  and the answers pile up as a side effect of rounds held anyway. A linear model stays readable: each weight says what
  the ears care about.
- **Regression on the 1–10 clip ratings.** There are only 19, one per song, and ratings across songs don't compare
  well. They stay as one more check.
- **Keep hand-picking a better measure.** Six or more have been tried (ADRs 0049, 0051, 0053) and none is established.
  The best, the R128 stem offset, was picked after looking. Here those measures become features instead, and the
  answers set their weights.
- **An off-the-shelf perceptual metric as the ruler (PEAQ, ViSQOL, CDPAM).** These are trained or tuned on speech and
  codec artefacts, not chip renditions of tracker music. They are worth trying as features (above), not trusting alone.
- **A neural network on spectrograms.** It would need thousands of answers. A few hundred is realistic.
- **Keep deciding by ear only.** It works, but only for rules. The thousands of choices inside each song still fall to
  log-mel.

## Consequences

- New file `tests/golden/listening/answers.tsv`, with a README describing its columns, and a harness command that remakes
  any row's clips from its recipe and checks they come out byte-identical.
- New `src/Scoring` class for the model's constants and score. Once one is chosen, [testing.md](../testing.md) and
  [harness.md](../harness.md) say which ruler is the main number.
- David (2026-11-10) dropped plan tasks T9.4e–T9.4i (ADR 0096's threshold, table and rounds). Their survey tooling and
  perceptual features move into T9.4k.
- Plan tasks, after T9.4d and before T9.5 (T9.5 is the first heavy user of the ruler):
  - **T9.4j:** answers file, recipe re-render check, the past rounds added where they can be remade, and a small round of
    about 5 pairs logged through it (David: half speed on dull samples);
  - **T9.4k:** harness fit, with leave-one-song-out checks and the score reported next to log-mel; the perceptual
    features and the pair picker;
  - **T9.4l:** hold-out round and the adoption decision, recorded in an ADR;
  - **T9.4m:** use in FM fit selection and the allocator, each checked blind.
- Rules out nothing already accepted. ADR 0016's "no neural parameter estimator inside the converter" is untouched: this
  model scores renders, it doesn't make patches.
