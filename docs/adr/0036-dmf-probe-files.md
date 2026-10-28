# ADR 0036: DMF probe files (T4.3)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T4.3: the DMF rules learned from Furnace and DefleMask's effect list had to be confirmed in DefleMask itself
and through SGDK.

## Decision

- **Checked both ways** (David): David opens our probes in DefleMask and listens, and his "Save As" copies are read
  back by `DmfReader` and must hold the same song (same description, ignoring file version, pattern names, sample
  cut points, trailing bytes and unused custom Hz bytes).
- **Built by a test** (David, over a one-time script): `DmfProbes` makes them, `DmfProbeTests` keeps the checked-in
  files current (compared decompressed, since zlib output can differ by platform; `UPDATE_GOLDEN=1` rewrites them).
- Each probe is built so a wrong guess is clearly audible: the FM probe's only sounding operator is file operator 2
  under algorithm 4 (silent if file order isn't register order), the DAC probe repeats a sample two octaves up
  (must sound the same) and uses two rate codes, the custom Hz probe beeps once a second.
- The rescomp step uses a checked-in `probes.res` (one `XGM2` per exported VGM).
- **Results (2026-10-28):** every probe opens, reads back unchanged after DefleMask's "Save As", plays as designed
  (measured from DefleMask's WAV and VGM exports, which `DmfProbeVgmTests` now checks) and passes rescomp `XGM2`.
  David ran the checklist; Claude re-ran it with computer use and got byte-identical files.
- **New rule found: DefleMask's PSG notes sound two octaves above their name** (SN1 `A-4` = 1748 Hz). The DMF writer
  lowers PSG octaves by 2 (docs/formats/dmf.md#psg-notes-sound-two-octaves-up).
- **Furnace plays the PSG octave the same way** (David asked to check): Furnace 0.6.8.3's VGM and WAV exports of all
  five probes match DefleMask's, SN1 `A-4` = 1748 Hz included. So the rule is how DMF is pitched, and a DMF we write
  sounds the same in both.

## Consequences

Probe checklist and results in [tests/golden/dmf-probes/README.md](../../tests/golden/dmf-probes/README.md).
