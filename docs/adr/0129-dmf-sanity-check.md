# ADR 0129: DMF sanity check against DefleMask's VGM exports

- **Status:** Proposed
- **Date:** 2026-11-20

## Context

David put songs made in DefleMask in `tests/data/dmf/`, each with DefleMask's own VGM export, and expects each DMF,
put through the project's XGM2 writer, to come out equivalent to its VGM (2026-11-18). These songs already fit the
Genesis's channels, so no mapping is involved: what is checked is the writer and, under it, SGDK's XGM2 driver
conventions. The converter has no DMF input, and the songs aren't freely licensed.

## Decision

David's answers (2026-11-18):

- **Three checks, one report.** Each XGM2 file is decoded back into chip writes and compared with the VGM for the same
  writes per frame, the same chip state per frame, and the same sound (both rendered with the project's chip models
  and scored with `SongScorer`). The findings go to `tests/data/dmf/sanity-report.md`. **A difference is a potential
  defect, not a defect**; the test fails only when a song can't be put through at all.
- **The DMF player lives in the test project only** (`tests/Mod2Xgm.Tests/Dmf/Sanity/`). It plays a DMF the way
  DefleMask does, tick by tick, into chip writes. The converter doesn't read DMF.
- **Two paths per song.** "DMF → player → XGM2", and a control, "VGM → XGM2", which feeds DefleMask's own writes to the
  writer and so leaves the player out. A difference only in the first path is the player's; one in both is the
  writer's (or SGDK's driver's). The player is also compared with the VGM on its own.
- **Old files are re-saved.** DMF versions 18–21 aren't read; David re-saves them in DefleMask and re-exports their
  VGMs. Until then the report lists them as not read.
- **The songs stay local.** `tests/data/dmf/` is git-ignored; the test is skipped where the folder is missing, as in CI.
- The DMF reader gains a lenient mode for this check only: DefleMask itself saves a macro loop past the macro's end
  and a note 0 with an octave (it plays that as a C).

## Options considered

- **Chosen:** a test-only DMF player plus a VGM control path. The control isolates the writer even while the player
  is incomplete.
- Compare only the XGM2 bytes with an XGM2 made from the VGM by `xgm2tool`: needs an external tool, and the files differ
  by design (PCM sent ahead, ADR 0119; padded samples).
- A DMF player in the converter: no use for it outside this check, and more code to keep AOT-clean.
- Commit the songs: not freely licensed.

## Consequences

- Plan task T10.1c; [testing.md](../testing.md#dmf-sanity-check) describes how to run the check and read the report.
- DefleMask playback rules the exports showed are written up in [formats/dmf.md](../formats/dmf.md#deflemasks-playback-rules-seen-in-its-vgm-exports).
- The player models DefleMask only as far as the songs needed; what it still doesn't match is listed in each report.
  Its differences are the player's until the control path shows them too.
