# ADR 0105: Hold-out songs and the bar for the learned listening score

- **Status:** Proposed; its "can't tell" reading superseded by [ADR 0106](0106-cant-tell-smaller-wins.md)
- **Date:** 2026-11-11

## Context

[ADR 0097](0097-learned-listening-score.md) lets the learned listening score replace log-mel as the main number only
after it beats log-mel at predicting David's picks on a hold-out of songs never used for fitting (plan T9.4l), and left
the bar open. Today's fit ([ADR 0101](0101-listening-score-fit.md)) and the pretrained models
([ADR 0104](0104-pretrained-embeddings.md#results-2026-11-11)) have used 15 of the corpus's 19 songs, one answer each.
The other four include the 15-sample song made in code and a short effects test, so the corpus can't supply a hold-out.

David answered on 2026-11-11.

## Decision

- **New songs (David).** All the MODs The Mod Archive lists as Public Domain or CC0 that aren't in the corpus were
  checked on their own pages: 39 four-channel MODs. The corpus's rules apply ([ADR 0046](0046-mod-corpus.md)): covers of
  other people's songs, songs whose notes say the samples came from another song or from various sources, and "all rights
  reserved" or "(c)" texts are left out (8 songs). David kept files over 1.5 MB unless they raise copyright doubts too;
  the three that size all do ("Track is copyrighted"; two songs that call themselves a tribute to another work). That
  leaves **28 songs**, listed with their licences in [LICENSES.md](../../tests/data/LICENSES.md#hold-out-holdout-plan-t94l).
- **Their own folder (David):** `tests/data/holdout/`, apart from `tests/data/corpus/`, so `--fit`, `--pick` and corpus
  runs never take them by accident, and corpus runs don't grow two or three times longer. `CorpusTests` checks both
  folders against their own tables and that no song is in both.
- **The bar (David): at least 30 hold-out pairs; the learned score agrees with David's a/b picks at least 70 % of the
  time; and at least 10 points above log-mel's agreement on the same pairs.** The 30 are answered pairs whose clips
  remake as heard; "can't tell" answers count among them but for neither score's agreement (Claude's reading, for David
  to confirm). The learned score is scored with its weights as fitted, never refitted on the hold-out.
- **What the pairs test (David):** a mix of the kinds of change round 21 asked about (half the memory, half speed on dull
  samples, `--no-dac`, `--mapping simple`), pairs the pair picker chooses (`--pick`, [ADR 0102](0102-pair-picker.md)),
  and kinds of change the score has never seen (for example layering or pre-mixes on and off).
- **The new kinds (2026-11-12):** layers off and pre-mixes off, as recipe rules (`rule:layers=off`, `rule:pre-mix=off`)
  the harness passes to the optimizer, since no converter option turns either off; the picker offers both as kinds
  (`no-layers`, `no-pre-mix`), and `--kinds` narrows a pick to them. Round 23 is five of each: David raised the cap of 3
  pairs per kind to 5 for that round (`--max-per-kind 5`); the cap stays 3 otherwise.
- The hold-out answers are logged in `tests/golden/listening/answers.tsv` like any round. A row is a hold-out row
  because its song is in `tests/data/holdout/`, so no new column is needed. `--fit` and `--embeddings` leave those rows
  out, and `--holdout` scores them ([harness.md](../harness.md#the-hold-out)). `--pick` fits nothing, so it keeps them:
  they stop a song from being asked twice.

## Options considered

- **New freely licensed songs in their own folder (chosen).** Songs the score has never heard, kept apart by folder
  and checked by tests.
- **The four unused corpus songs only.** Too few for 30 pairs, and two of them are test files, not music.
- **Leaving whole corpus songs out of the fit.** The fit has only 15 answers; taking some away for testing leaves too
  few to fit anything.
- **The new songs in `tests/data/corpus/`.** One folder is simpler, but every corpus run would take them in and the
  hold-out could end up in a fit unnoticed.
- **All 39 songs.** More songs, but covers and borrowed samples would sit in a public repository, against ADR 0046's rule.

## Consequences

- `tests/data/holdout/` (28 MODs, about 11 MB) and a hold-out section in `tests/data/LICENSES.md`; `CorpusTests` reads each
  section separately.
- Plan T9.4l: the rounds of about 10 pairs each on these songs, then the adoption decision, which this ADR's bar decides
  and a new ADR records.
- These songs are no longer training songs for T9.4p ([ADR 0098](0098-learned-allocator-costs.md)): T9.4p's 100 or more
  need other sources.
