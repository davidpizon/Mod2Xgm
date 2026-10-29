# ADR 0045: The command line writes the DMF (T5.6)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan T5.6 joins the stages built in T5.1–T5.5 into the conversion command: MOD → song model → simple mapper → DMF
timing → DMF song builder → DMF writer. Four things weren't settled by the earlier ADRs: what to do with `--format xgm`
/ `xgm2` before their writers exist (plan M10/M11), what `--quality normal` (the default) and `best` do before the
optimizer exists (plan M9), what an ignored sample's notes sound like, and what report section 3 shows for DMF.

## Decision

All four by David, 2026-10-28:

- **XGM and XGM2 aren't written yet.** The run reads the MOD and prints report sections 1–2 as always, then stops with
  exit **1** and `--format xgm2 isn't available yet: this version writes DMF only (XGM2 comes with plan M10, XGM with
  M11).` `--format xgm2` still prints `NOTE: SGDK expects XGM2 files to use the .XGM file extension.` first. No file is
  written.
- **Every quality uses the simple mapper** until the optimizer exists. `--quality normal` and `best` print one
  `Information` line saying so: `--quality normal: the optimizer isn't built yet (plan M9), so the simple mapper is used`.
  It describes the tool, not the song, so it isn't a conversion note. `--quality fast` prints nothing extra.
- **An ignored sample's note silences its track** until the track's next note, as a note of an empty sample does: the
  FM channel the track was sounding gets a note off, and so do the DAC and the PSG noise channel when that track
  started their current sound (never another track's). One conversion note per ignored sample says how many notes
  were left out. `MappingOptions.Ignore` carries the list to the mapper.
- **Report section 3 lists one line per DMF instrument and DAC sample**, in file order, after a summary
  (`src/Dmf/DmfReport.cs`):

  ```
  Output (DMF): 1 FM instrument, 1 PSG noise instrument, 3 DAC samples, 13 KB PCM
   FM  01  ← 01 "lead", 02 "pad" (Sine patch)
   PSG 02  ← 04 "hat" (noise)
   DAC 01  ← 03 "drum"                    11025 Hz  5321 B
   DAC 02  ← 03 "drum"                    11025 Hz  5321 B  -6 dB
   DAC 03  ← 03 "drum"                    11025 Hz  2596 B  from frame 2048
  ```

  An FM instrument is a library patch shared by every sample that uses it, so its line lists them all. Numbers are
  1-based like the MOD's. Sizes are the bytes the console holds once DefleMask exports the song as 8-bit PCM (one per
  frame), not the DMF file's 16-bit storage. `DmfSongBuilder.Build` now returns a `DmfSong`: the `DmfFile` plus where
  each instrument and sample came from.

Also decided in the code (no choice for David): the output is opened with `FileMode.CreateNew` without `--force`, so
a file that appeared after the checks is never overwritten; any I/O failure while writing is exit 4.

## Options considered

- **XGM/XGM2:** stopping before reading the MOD lost: it breaks "the tables are printed for every format". Writing a DMF
  instead lost: easy to miss, and the file name would be wrong.
- **Quality:** making `fast` the default lost: it changes the CLI contract for a few milestones. Using the simple
  mapper without a word lost: a user asking for `best` should know they didn't get it.
- **Ignored notes:** skipping the note so the track's earlier sound rings on lost: it leaves long held notes where the
  song had a different sound.
- **Section 3:** a summary line alone lost: it doesn't show which sample went where.

## Consequences

- `mod2xgm song.mod` now writes a playable `song.dmf`; `--info` runs every stage and prints section 3 but writes nothing.
- M10 and M11 replace the "isn't available yet" stop with their writers and their own section 3; M9 replaces the
  quality line with the optimizer.
- `docs/cli.md` documents section 3 for DMF, the formats not written yet and the quality line; the plan marks T5.6 done.
