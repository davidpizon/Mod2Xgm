# ADR 0027: Sample usage scan (T2.4)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T2.4: which notes count as using a sample, for the report and the capacity budget.

## Decision

- A note counts when it really starts (period present, the channel's current sample); slides (`3xx`/`5xy`) and notes
  delayed past their row (`EDx`, x ≥ speed) don't; retrigger counts once. (David)
- The scan follows the song's flow now (`Bxx`, `Dxx`, `E6x`, `F00`) rather than counting every stored row (David's
  choice over the "count everything" option).
- Loops: once through; a jump back to played rows is walked one more time (David). A song that just ends is walked
  once (David).
- Details in [sample-capacity.md](../sample-capacity.md#usage-scan-srcmodsampleusagecs-plan-t24).

## Consequences

Details in [sample-capacity.md](../sample-capacity.md).
