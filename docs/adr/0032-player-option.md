# ADR 0032: `--player` (T3.3a)

- **Status:** Accepted; supersedes parts of [ADR 0031](0031-mod-to-song-effect-decoding.md)
- **Date:** 2026-10-27

## Context

MOD players disagree on rules, and 15-sample files were made for Ultimate Soundtracker, which reads several effects
differently.

## Decision

- **One option picks whose rules the converter follows**, from the reader on (David, replacing T3.3's "option later"):
  `--player protracker|ust`. OpenMPT was chosen too, then left for a later task (T3.3b) once its rules turned out to be
  mostly per-file guesses (David). Name `--player` (David, over `--mod-player` and `--tracker`).
- **Default by format**: `ust` for 15-sample files, `protracker` for 31-sample files (David, over `protracker` for all
  and `openmpt`). **Every run prints the player and why** (David): an `Information` line right after report section 1.
- **Ultimate Soundtracker rules** follow OpenMPT's Soundtracker loader (`soundlib/Load_stk.cpp`, BSD-3-Clause, read
  only): loop starts in bytes; looped samples start at their loop start; byte 471 is the tempo (OpenMPT's timer
  formula, 0 and 0x78 → 125 BPM); no finetune; `1xy` arpeggio, `0xy` arpeggio from 03 up, `2xy` pitch bend (y up, else
  x down); `3xx`–`Fxx` ignored with one note, `Cxx` included. Under ProTracker rules a 15-sample file keeps T2.3's
  words-or-bytes loop guess.
- Decided while implementing: `Timing.Bpm` is a `double` (the tempo byte gives fractional BPM such as 121.14); the
  report's tempo line prints up to two decimals. The sample-usage scan drops effects 3–F under Ultimate Soundtracker
  rules. A tempo byte of 240 or more has no meaning in the formula: 125 BPM with a note. `ModToSong` takes the player
  from `ModModule.Player` instead of a parameter.

## Consequences

OpenMPT rules are plan task T3.3b.
