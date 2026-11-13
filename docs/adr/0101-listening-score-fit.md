# ADR 0101: Fitting the learned listening score

- **Status:** Proposed; its "can't tell" reading superseded by [ADR 0106](0106-cant-tell-smaller-wins.md)
- **Date:** 2026-11-10

## Context

[ADR 0097](0097-learned-listening-score.md) settles what the learned listening score is: a pairwise (Bradley–Terry)
logistic model with no intercept, `P(A ≻ B) = σ(w · (f(B) − f(A)))`, L2-regularized, at most about one weight per 10
answers, every choice made on leave-one-song-out checks, and the weights kept as constants in `src/Scoring`. Plan
T9.4k2 builds the fit and reports the score next to log-mel. The features come from `src/Scoring`, the perceptual ones
from [ADR 0100](0100-perceptual-features.md).

The answers file has 5 answers so far, all from round 20, so this task builds and tests the machinery. The first weights
come from T9.4k3's round.

David answered the questions that aren't DSP detail (2026-11-10). The fitting details are Claude's, recorded here for
David to review.

## Decision

**How it runs (David).** `mod2xgm-harness --fit answers.tsv` remakes every row as `--remake` does, measures each clip
against the original's clip, fits, and writes `listening/fit.txt`. `--remake` stays as it was. The two can't be given
together.

**Where the weights go (David).** After a fit, the harness rewrites `src/Scoring/ListeningWeights.cs` itself: the
weights, the date, the number of answers and songs, and both agreements. It finds the file by going up from the current
folder to the folder that holds `Mod2Xgm.slnx`. With too few answers, or when no feature helps, it leaves the file
alone. The change shows in `git diff`, and it is committed in a normal PR.

**Which rows it learns from (David).** Rows that are answered and whose clips remake byte for byte. A row whose clips
now differ stays in the answers file (David's answer is still true of what he heard), but the fit leaves it out.
`fit.txt` lists every row left out, and why.

**What it reports (David).** A summary (answers, songs, the most weights allowed, the weights kept, λ, the
leave-one-song-out log-loss next to a coin's ln 2), how often it guessed David's pick on songs it didn't learn from next
to how often log-mel alone does, and one line per answer: the chance it gave the pick and log-mel's guess.

**Where the score shows (David).** A `learned` column in `scores.csv`, `clip-scores.csv` and `features.tsv`, and a
*Learned* column next to *Log-mel dB* in `report.md`'s score tables. It is empty, or *not fitted*, until there are
weights. Log-mel stays the main number until the adoption decision (T9.4l).

**The fit (Claude).**

- **Features:** `mel_db`, `mfcc`, `onset_miss` (1 − onset F1, so every feature is a distance), `nmr_db`, `nsim_distance`,
  `modulation_db`, `harmonicity_db`, in that order (`ListeningFeatures.Names`). Balance and the R128 stem offset need
  stems, which the clips don't have yet; they join when they do.
- **Targets:** A's win counts 1, B's 0, and "can't tell" ½.
- **Scaling:** each feature's difference is divided by its root-mean-square over the answers before fitting, so one λ
  fits all of them. With no intercept there is nothing to centre. A feature that never differs is left out. The weights
  are scaled back, so the file holds raw weights: `s(x) = w · f(x)` needs no scaling.
- **Solver:** Newton's method on the regularized log-loss, with a Cholesky solve. The L2 term keeps the Hessian positive
  definite, so it converges, even when one feature separates every pick.
- **Choosing features and λ:** forward selection. Starting from none (a coin, log-loss ln 2), each step tries every
  feature not yet in, with each λ in {0.01, 0.1, 1, 10}, and keeps the one with the lowest mean leave-one-song-out
  log-loss. It stops when nothing lowers it or when the cap is reached.
- **Cap:** ⌊answers / 10⌋ weights. With none allowed, or answers from fewer than two songs (no song to leave out), the
  fit says "not enough answers" and stops.
- **Agreement:** over the a/b answers only. A chance over ½ guesses A, exactly ½ counts half. Log-mel alone guesses the
  version with the lower `mel_db`.

## Options considered

- **Forward selection on leave-one-song-out log-loss (chosen).** With a few dozen answers and seven features, it shows
  which features earn a weight, and it keeps the cap simple.
- **All features at once, with L2 only.** Simpler, but with fewer than 70 answers it breaks ADR 0097's limit of about one
  weight per 10 answers.
- **L1 (lasso) for the selection.** It also picks features, but its path needs a different solver, and with seven
  features exhaustive forward steps are cheap.
- **Leave-one-answer-out checks.** Answers from one song share clips and sounds, so leaving one answer out overstates how
  well the score carries to new songs. ADR 0097 asks for songs.
- **The harness prints the weights and a person pastes them (Claude's first suggestion).** David chose to let the fit
  write the file itself.

## Consequences

- New `src/Scoring/ListeningScore.cs` (`ListeningFeatures`, `ListeningScore`) and `src/Scoring/ListeningWeights.cs`,
  which only the fit writes. New `tools/Mod2Xgm.Harness/ListeningFit.cs`.
- [harness.md](../harness.md): `--fit`, the fit section, and the `learned` columns. The listening README no longer marks
  stale rows as **Verify**.
- The test that checks `scores.csv`'s columns skips the `learned` value, since it depends on the weights. A later fit
  breaks no test.
- T9.4k3 runs the first fit, on its round, and commits the first weights.
