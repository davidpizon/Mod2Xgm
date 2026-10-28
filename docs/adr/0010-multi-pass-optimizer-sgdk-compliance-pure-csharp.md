# ADR 0010: Multi-pass optimizer, SGDK compliance, pure C# rendering

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Choosing how each MOD instrument is rendered (FM, PSG or PCM) is the core quality problem, and every output must work
in SGDK.

## Decision

- **Multi-pass optimizer adopted**, built after the first DMF release (simple mapper first). It fits FM/PSG/PCM
  candidates per instrument, allocates within target limits, renders in-app and refines. Decides by measured
  error and cost, not a fixed FM-first rule.
- **In-app rendering and scoring are pure C#:** own MOD renderer (validated vs openmpt123), C# port of ymfm
  (BSD-3) for YM2612, C# SN76489, PCM driver model. No native libraries for these.
- **Conversion time:** minutes are acceptable by default. `--quality fast|normal|best` (default normal, 5-min
  budget), `--time-budget`, `--seed`, fitted-patch cache (`--no-cache`).
- **SGDK compliance is the standard:** every output must play through SGDK; DMF must survive DefleMask VGM
  export → rescomp.
- **CH3 special mode** allowed for **XGM2** (spec'd in xgm2.txt) and **DMF** (system 0x42); not XGM v1 until
  xgm.txt confirms.
- **`--reserve-sfx-channels N`**, default 0 (music may use every PCM channel).

## Consequences

Design in [multi-pass.md](../multi-pass.md); built after the first DMF release.
