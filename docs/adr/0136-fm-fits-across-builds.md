# ADR 0136: FM fits are kept across builds, until the fitter's version changes

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

[ADR 0074](0074-candidate-cache.md) keeps every kind of candidate in one folder per build, and a new build deletes the
other builds' folders (David: every build starts with an empty cache). For FM fits that means hours. A full fit of
FunkyChristmas takes about 10 minutes on four cores, the corpus takes 40 CPU-hours, and every change to any code
starts over.

David wants the simple mapper to play fitted FM patches in the songs he ships (2026-11-21), with `--quality best`
searching for hours, and wants the fits of every song he has converted kept: "Once, then saved", keyed so that a changed
song isn't matched to stale fits. Asked what happens when the converter itself changes, he chose "Keep across builds".

ADR 0074 had turned down a hand-bumped fitter version, because a forgotten bump leaves stale fits.

## Decision

- **FM fits live in a folder per fitter version,** `fm-v<N>` under the cache folder, not in the build's folder.
  `CandidateCache.FmFitterVersion` is `N`, starting at 1. A new version deletes the other versions' folders (only folders
  named `fm-v` and digits). PSG, PCM, pre-mix and CH3 candidates stay per build, as before. They take seconds, and CH3's
  key already holds the FM fits it cuts down.
- **A changed song never meets a stale fit.** The key is unchanged: each pitch region's sound (its note, fundamental and
  spectrum) and the fit options. A changed sample is a new key, and an unchanged sample keeps its fits, even in another
  song.
- **A test guards the version.** `CandidateCacheTests` fingerprints (SHA-256, line endings made LF) the code a fit
  depends on:
  - the searches, the evaluator, its note renders and scores;
  - the analysis of those renders and the YM3438 model;
  - the library seeds and the cache's file format.

  When that code changes, the test fails until the fingerprint is updated. Its message says to bump the version when a
  fit could come out differently, which is what makes the version safe.

## Options considered

- **Chosen: a version per fitter, with a fingerprint test.** Fits survive every build that doesn't touch fitting, and
  a forgotten bump fails the build instead of going stale.
- **Keep fits until David clears the folder.** It's the simplest, but a change to the fitter would keep giving old fits
  without saying so.
- **Keep today's rule (per build).** It's always fresh, but hours of fitting are lost after every change.
- **A fingerprint of the fitting code in the key itself, computed at build time.** It needs a source generator or an
  MSBuild step to hash the files into the assembly. It would also refit after a change that can't alter a fit, such
  as a comment. The test leaves that call to whoever changes the code.

## Consequences

- A rerun after any build that leaves fitting alone reads its FM fits back. FunkyChristmas's second `--quality best`
  conversion skips the 10-minute search.
- Whoever changes the fitting code updates the fingerprint in the test. They bump `FmFitterVersion` when a fit could
  change, which deletes the old fits.
- Switching between branches with different fitter versions refits, as switching builds did before.
