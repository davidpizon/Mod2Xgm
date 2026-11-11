# ADR 0102: A pair picker for the listening rounds

- **Status:** Proposed
- **Date:** 2026-11-11

## Context

[ADR 0097](0097-learned-listening-score.md) lets a round add pairs where the learned listening score is least sure, and
plan T9.4k3 builds the picker and runs its first round of about 10 pairs (David). The answers file has 5 answers, all
from round 20 ([tests/golden/listening](../../tests/golden/listening/README.md)), too few for one weight
([ADR 0101](0101-listening-score-fit.md)), so the score has no weights yet and is 50/50 on every pair. The picker has to
say what "least sure" means before there is a fit, and where its pairs come from.

David answered the questions below on 2026-11-11. The details that follow from them are Claude's, recorded for David to review.

## Decision

**What it runs.** `mod2xgm-harness --pick answers.tsv` makes the candidate pairs from the harness's songs, picks
`--pairs` of them (default 10), and writes `listening/picked.tsv`: the new round's rows without hashes or answers, for
`--remake` and the survey as before ([harness.md](../harness.md#listening-answers)). `listening/candidates.tsv` lists
every candidate with how sure each measure is, so the choice can be checked. The answers file is only read: for the
round's number, the songs already asked about, and the songs' order. It can't be given with `--remake` or `--fit`.

**The pairs (David).**

- XGM2 only, as round 20: every change below works there.
- Each pair is the default version, `--mapping optimizer`, against the same with **one change**, so it is clear what
  David heard. The four kinds:
  - **simple:** `--mapping simple`;
  - **budget:** `--mapping optimizer --max-pcm-bytes N`, with N half the PCM bytes the default version stores (its
    XGM2 header's SLEN), rounded down to 256 bytes; not offered for a song that stores none;
  - **no-dac:** `--mapping optimizer --no-dac`;
  - **half-speed:** `--mapping optimizer rule:half-speed=s`, for a **dull** sample only: one with little sound above
    3.325 kHz, the highest XGM2 half speed keeps (Claude: at most 1 % of its energy above it, in the pitch region it
    plays most; the two dullest such samples of a song are offered).
- Clips are 20 seconds, as round 20. Starts are tried every 10 seconds through the song, and the picker chooses one.
- A pair whose two clips are the same bytes is skipped. Any other difference counts: round 20's were 21–34 dB below
  the music and David still heard them.

**Least sure (David).**

- **Without a fit:** each of the seven measures (`ListeningFeatures.Names`) votes for the version it finds closer to the
  original. The pairs where the votes are most split are the least sure: `|votes for default − votes for change| /
  votes cast` is lowest. A measure that finds both versions equally far casts no vote. Ties go to the pair with the
  smaller log-mel difference (Claude).
- **With a fit:** the score's chance that the change wins, `σ(w · (f(default) − f(change)))`, nearest ½. The picker
  switches by itself when `ListeningWeights` holds weights.

**Which pairs (David).** One pair per song, avoiding the songs already in the answers file while enough others are left,
since leaving one song out needs answers from many songs. At most 3 pairs of each kind, so all four kinds appear. The
candidates are taken least sure first under those two limits.

**The rows (Claude).** The round is the next number after the file's highest (`r21` after `r20`), the questions `q1`
on in the order picked. Which version is A is decided by the SHA-256 of the song's name and the start (its first byte's
lowest bit), so the default isn't always A and the same pick always gives the same file.

## Options considered

- **Votes of the seven measures (chosen, David).** With no weights, the measures' disagreement is the best sign of a
  pair the ears must settle. Pairs every measure agrees on teach a linear score little.
- **Log-mel near a tie.** Uses one measure only; the pairs it can't separate may be ones every other measure separates easily.
- **A small fit on the 5 answers to steer the picker.** Breaks ADR 0097's limit of one weight per 10 answers, and all
  5 answers are on one kind of change.
- **Any two versions in a pair.** More variety, but a pair of two changes doesn't say which change was heard.
- **Any songs, several pairs per song.** Fewer songs make the leave-one-song-out checks weaker.

## Consequences

- New `tools/Mod2Xgm.Harness/PairPicker.cs`; `--pick` and `--pairs` in the harness; [harness.md](../harness.md) says how a
  picked round goes.
- Round 21 is the first picked round; its answers go into `answers.tsv` and the fit runs on them (T9.4k3).
- The picker renders four or five versions of each song and scores every start of each, so it takes much longer than
  `--remake`; it is run by hand, not in CI.
