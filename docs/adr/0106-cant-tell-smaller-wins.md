# ADR 0106: "Can't tell" means either is fine, and the smaller file wins

- **Status:** Proposed; supersedes the "can't tell" rule of [ADR 0097](0097-learned-listening-score.md),
  [ADR 0101](0101-listening-score-fit.md) and [ADR 0105](0105-hold-out-songs.md)
- **Date:** 2026-11-13

## Context

Ten of the 25 answers in `tests/golden/listening/answers.tsv` before round 23 are "can't tell" (`same`): five in round 21
and five in round 22, the first hold-out round. Until now the fit counted each as half a win for each side
([ADR 0097](0097-learned-listening-score.md), [ADR 0101](0101-listening-score-fit.md)), and the hold-out counted them as
pairs but for neither score's agreement ([ADR 0105](0105-hold-out-songs.md), Claude's reading, never confirmed).

Asked to confirm that reading before the rest of the hold-out rounds (plan T9.4l), David answered on 2026-11-13.

## Decision

- **What "can't tell" means (David):** either version is acceptable, unless his words on the row say otherwise, and
  **the smaller file wins**.
- **Smaller (David):** the output file's size in bytes (the `.xgm2` each recipe makes), not only the PCM bytes. The
  harness measures it when it remakes a row (`RemadeRow.BytesA`, `BytesB`); both sizes equal or one unknown (XGM has no
  writer yet) leaves the answer undecided.
- **On the hold-out (David):** a "can't tell" the sizes decide counts as a pick of the smaller version. A score that
  finds the smaller version closer is right, and so is one that finds both equally close (an exact tie), since David
  heard them as equal. On his own a/b picks a tie stays a miss. It counts among the a/b picks for both the learned score
  and log-mel. An undecided one still counts as a pair, for neither score.
- **In the fit (David):** the same: a decided "can't tell" counts as a full win for the smaller version, not half for
  each. An undecided one stays half each.
- **Words that say otherwise:** when David's words on a "can't tell" row prefer one version, the row is logged with that
  pick (`a` or `b`) and the words kept, so the file says what counts. The harness never reads the words.
- The answers file is unchanged: `same` stays what David said, and the reports show it as `can't tell → b (smaller)`.

## Options considered

- **Smaller wins, in the fit and the hold-out (chosen, David).** Matches what the converter should do with two versions
  that sound the same: keep the smaller.
- **Half a win each / for neither score (before).** Treats "can't tell" as no information, though David says either is
  fine and size then decides.
- **Right when the score calls it "about equal".** Needs a tie margin nobody has chosen; an exact tie is already counted
  right.
- **Smaller in the fit only, or the hold-out only.** The two would then measure different things.
- **PCM bytes instead of the file size.** Leaves out the FM and PSG data the file also holds.

## Consequences

- `FitAnswer.Smaller`, `Decided` and `Target`; `--fit`, `--holdout` and `--embeddings` go by the decided pick.
  `docs/harness.md` describes it.
- The weights in `src/Scoring/ListeningWeights.cs` were fitted with the old rule. They are fitted again with this one
  before the hold-out is scored (the fit never sees hold-out rows, so the hold-out stays unseen).
- A score that learns "smaller is closer" would be wrong in general; here it learns which version David accepts, which
  is what the allocator needs.
