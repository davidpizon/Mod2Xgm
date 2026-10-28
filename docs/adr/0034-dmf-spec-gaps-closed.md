# ADR 0034: DMF spec gaps closed (T4.1)

- **Status:** Accepted; confirmed in DefleMask by [ADR 0036](0036-dmf-probe-files.md)
- **Date:** 2026-10-28

## Context

Plan task T4.1: DefleMask's own DMF spec leaves out effect codes, DAC sample selection, custom Hz encoding, sample
rate codes and more.

## Decision

- Every gap in `DMF_SPECS.txt` is resolved in [formats/dmf.md](../formats/dmf.md) from Furnace's DMF loader and saver
  (commit `bcba71d`, read only, GPL — no code copied) and DefleMask's effect list. The probe files (T4.3) confirm
  them in DefleMask.
- **Samples are written the way Furnace writes them:** 16-bit signed, pitch code 5 (×1), amp 50 (×1), so DefleMask
  doesn't resample or scale them on load and the 8-bit layout (unsigned values in 2-byte slots) is never needed.
  Over writing 8-bit data, whose layout only Furnace's loader documents.
- **DAC samples have no per-note pitch:** the note's semitone and `EBxx` bank pick one of 144 samples, the octave is
  ignored, and each sample plays at its rate code. So every pitch a MOD sample plays at is a separate resampled
  copy (sample capacity and the mapper must count them).
- Custom Hz is a whole number (3 ASCII digits): a non-whole tick rate is rounded, with a conversion note. DMF has
  no tick-rate effect or stop effect; mid-song BPM changes and `F00` stops are approximated with conversion notes
  (T5.4, T5.5).
- Ext. CH3 (`0x42`) channel order: FM1, FM2, FM3 OP1–OP4, FM4, FM5, FM6, PSG1–4.

## Consequences

Format details in [formats/dmf.md](../formats/dmf.md).
