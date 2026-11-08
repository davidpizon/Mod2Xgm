# ADR 0091: Shrinking recordings to fit, chosen by ear

- **Status:** Accepted
- **Date:** 2026-11-08

## Context

Plan task T9.4a. XGM2 holds 123 recordings in 128 KB and XGM 63; on XGM2, 17 of the corpus's 19 songs ask for more than the
budget (ai_renaissance asks for 355 copies and 1.8 MB). Until now the greedy allocator ([ADR 0087](0087-greedy-allocator.md))
had one answer to a full budget: move the note to FM or PSG, or cut it ("swap first"). Trimming a one-shot's quiet tail and
storing a short loop once were already free inside `CapacityCheck.CopyBytes` (David: "Always, it's free").

David split T9.4 into two tasks: T9.4a shrinks the recordings themselves, and T9.4b glues overlapping drums into pre-mixes
([ADR 0052](0052-voice-stealing.md)). He chose to let his ears decide three things: whether shrinking or swapping comes first,
which shrink comes first, and how far pitch sharing may go. He listened in blind A/B rounds on XGM2 renders, played as WAV.

While running round 10, David heard sounds on the wrong speaker, which uncovered a bug: every XGM/XGM2 PCM voice mixes onto
the one DAC, which has one pan (FM6's `$B4`), and the player let the first voice's pan win. With the old code, neon_techno's left
channel was 15 dB down and xlr8's right was 13 to 28 dB down. David asked for this to be fixed in this PR, first, by ear.

## Decision

- **`RecordingShrinker`** (`src/Optimizer/RecordingShrinker.cs`) runs before the allocator places notes. It starts with the
  least important sample (the fewest weighted notes) and stops as soon as the copies fit. It keeps a move only when the move
  saves slots or bytes, and it writes a conversion note for each move it keeps. The default moves, in order (David, round 12):
  1. **Merge** look-alike samples: correlation ≥ 0.98, both looped or both one-shot, lengths within 10 %. The kept copy plays
     at the merged sample's level. On DMF a merge happens only within 1.5 dB. A sample on `--require-dac` is never merged into
     another sample's sound.
  2. **Share pitch:** notes within 10 cents of a sample's most-played rate use that rate's copy.
  3. **Share loudness** (XGM and XGM2 only): one copy covers two 3 dB steps (6 dB), so some notes play up to 3 dB off.
     DMF changes a sample's volume at play time and never needs this.
- **Half speed is not a default move.** On XGM2 it stores a copy at 6.65 kHz and halves its size. David heard it as "very
  crunchy and compressed", and swap first beat it in all three songs of round 11. The move stays in `ShrinkMove` and the
  player supports it, but only explicit `ShrinkRules` use it.
- **The gentle moves stay on.** In round 12, gentle shrinking and swap first tied on hit ("Both versions are incredibly
  close to the original"). On the corpus, the gentle moves keep a few notes:
  - hit: 2667 → 2475 notes moved to FM;
  - stardust_jam: 713 → 701 notes cut;
  - omg-plz-help: 23 → 20 notes cut;
  - hawks_claw: 1625 → 1621 notes moved to FM.
  Since sound accuracy is the goal, a tie goes to the rendition that keeps more of the song as recordings.
- **DAC pan clash: `DacPanClash.Centre`** (David, round 12). When recordings on both sides sound at once, the DAC plays in
  both speakers. When every sounding recording is on one side, the DAC plays on that side. The DMF driver has one DAC voice
  and never clashes. The plan carries the rule (`GenesisPlan.DacPanClash`), and `GenesisPlanPlayer.SetDacPan` applies it on
  each tick.
- **Pitch sharing is limited to 10 cents and wasn't tuned by ear.** It fired once in the whole corpus, too rarely for a
  round to show anything.

## Listening rounds

All rounds were blind (David heard the original, X and Y, in shuffled order) and used the first 30 s of an XGM2 render.

| Round | Song | Compared | David's pick |
|---|---|---|---|
| 10 | xlr8 | shrink (with half speed) vs swap first, old pan bug | shrink |
| 10 | neon_techno | same | swap ("offensive garbage" snare in shrink) |
| 10 | latenightcoffee | same | swap |
| 11 | neon_techno, xlr8, latenightcoffee | same, pan fixed (Centre) | swap first in all three: shrink "very crunchy and compressed" |
| 12 | neon_techno | pan clash: Centre vs Louder side | Centre |
| 12 | hit | gentle shrink (no half speed) vs swap first | can't tell |

On xlr8, the Centre and Louder renders differed by less than 0.5 dB, so that question was left out of round 12.

## Options considered

- **Gentle shrink first, then swap (chosen).** It ties with swap first by ear and keeps a few more notes.
- **Swap first, never shrink** (`ShrinkRules.None`). It ties with the gentle moves but keeps fewer notes.
- **Shrink with half speed.** It fits the most, but lost every comparison in round 11.
- **Pan clash on the first voice** (the old behaviour). This was the bug: one side of the mix dropped by 13 to 28 dB.
- **Pan clash on the louder side.** Measured on neon, it dropped the left side by another 3 to 21 dB, and David picked Centre.

## Consequences

- `docs/sample-capacity.md` describes the shrinker and the loudness-sharing row.
- Every shrink writes a conversion note, for example "one copy per 6 dB of loudness to fit, so some notes play up to 3 dB off".
- Things David heard that this task doesn't fix:
  - On neon_techno, the left percussion hit is missing after 0:08 in every version, so the bass line sits alone on the right.
  - Drum notes are cut on several songs, for example 417 on neon_techno.

  These are voice-limit cuts, not byte cuts. They are T9.4b's job: pre-mixing overlapping drums
  ([ADR 0052](0052-voice-stealing.md)).
- latenightcoffee's missing opening melody and drum strum, and the highs above 12 kHz that are too hot in every render, are
  separate problems and aren't caused by shrinking.
