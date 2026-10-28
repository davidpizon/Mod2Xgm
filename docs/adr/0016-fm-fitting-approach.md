# ADR 0016: FM fitting approach from the literature

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

FM patch fitting (T8.2) is the hardest part of the optimizer; three papers were read to choose a method.

## Decision

- FM patch fitting (T8.2) uses a **hybrid search**: per-algorithm structure search (GA or pruned exhaustive over
  MULT/DT), then local search over levels and envelopes against the time-varying harmonic spectrum, selected
  by rendered audio. Based on Horner et al. 1993, Sound2Synth 2022 and DDX7 2022
  ([references/fm-matching.md](../references/fm-matching.md)).
- MFCC distance will be tracked next to the log-mel distance in `src/Scoring`.
- No neural parameter estimator inside the converter for now.
- Papers are **not** copied into this public repository (copyright); citations and summaries only. Local copies
  go in the git-ignored `docs/references/papers/`.

## Consequences

Summaries in [references/](../references/README.md).
