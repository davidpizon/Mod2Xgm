# ADR 0133: The simple mapper plays a chord sample's notes on FM voices of their own

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

Looking for the stray note at the end of FunkyChristmas (plan T5.3n), David pointed to the last bars in BlastEm captures
through the loop. There the melody rests, and sample 1, a C–E–G chord, plays alone. The original plays the chord. The
Genesis played one lone C. Playing the chord didn't remove the stray note. That was the chord's release tail coming back
at the loop ([ADR 0134](0134-loop-silences-release-tails.md)).

The simple mapper plays a chord sample on one FM voice at its bottom note ([ADR 0125](0125-chord-samples.md)). Sample
1's loop repeats at the chord's common cycle, and the chord is harmonics 4, 5 and 6 of it. ADR 0125 left playing the
whole chord, on several FM voices or as a recording, as a channel-mapping decision.

David chose FM voices (2026-11-21) over `--require-dac 1`. As a recording, sample 1 leaves out 62 of its 139 notes,
those that start on a row with a drum, because the DAC plays one sample at a time. The optimizer already plays chords on
FM voices of their own ([ADR 0114](0114-fm-chords.md)), behind a harness rule.

## Decision

- **Which samples.** A sustained tone the simple mapper plays on FM whose loop repeats at a chord's common cycle, with
  a library patch: ADR 0125's "a cycle below every note" case (`HarmonicPatch.Lowest` above 1). Its notes are read from
  the loop's harmonic levels (`HarmonicPatch.LevelsDb`) by the optimizer's own rule (`SoundMenu.ChordTones`, ADR 0114):
  harmonics within 15 dB of the loudest, an octave or fifth of a lower note left out as its overtone when at least 3 dB
  quieter, at least two pitches, at most four notes. A chorused chord with no cycle (ADR 0125's other case) has no
  harmonics to read and keeps one voice.
- **Where.** The note's own FM channel plays the chord's lowest note. Each other note goes to an FM channel nothing else
  sounds on: FM1–FM5 (never FM6, which the DAC shares), with no note on the row, no track's own FM note sounding and no
  other track's chord. The channel's previous chord voices come first, then the channels no MOD channel owns, then the
  idle tracks' channels.
- **Never at another track's cost.** Chord voices are placed after every note of the row has its channel. A track's
  own note always takes its channel back from a chord voice, which then stops until the chord's next note.
- **Too few channels:** the top notes are left out first, as in ADR 0114 (David).
- **Level and pitch.** Each voice plays the sample's patch at `1200 × log2(harmonic)` cents above the measured cycle,
  at its share of the level (`PlanCell.Level`). The shares come from the harmonics' levels, and their powers add up to
  1, so the chord is as loud as the one voice was.
- **Following the track.** Every voice has the chord's MOD channel as its source. On XGM and XGM2 the player follows that
  channel's volume, pitch and cuts tick by tick. For DMF, the channel's volume changes and effects are copied to each
  voice. A note on the channel that isn't a chord, or the track going silent, lets the voices go.
- **Told.** A conversion note per chord sample names its harmonics, how many notes play it, and how many had too few free
  channels. A tally counts the chord voices a track's own note cut.

## Options considered

- **Chosen: FM voices of one patch on free FM channels, top notes left out first.** Every note of the chord sample is
  kept, and it costs no recording space and no other track's voice.
- **`--require-dac 1`.** It plays the real chord, but leaves out 62 of FunkyChristmas's 139 chord notes. David's rule
  is that notes that cut in and out lose to a poorer sound.
- **A voice of a quieter track.** Rejected in ADR 0114: it takes a sound from another track.
- **CH3 special mode,** each operator at its own pitch: one channel for the chord, but each note close to a sine, and
  not allowed for XGM v1 yet. Left for later.

## Results on FunkyChristmas

Solo render of sample 1, XGM2, over the last bars (the four strongest notes per 0.5 s):

- Original: E5, G5, C5 and E6.
- Before: C6 alone (C5 after [ADR 0132](0132-lead-fundamental.md)).
- After: G5, E5 and C5 all sound, with each voice's octave above. The E is about 8 dB under its share in the original.

The chord plays on all 139 notes with its three voices. None had too few channels and no voice was cut.

## Consequences

- Every song with a chord sample of this kind now uses more FM channels on the default mapper. The XGM, XGM2 and DMF
  outputs change for them, and so does any golden file that holds one.
- The optimizer's chord rule (ADR 0114, `rule:chords=on`) is unchanged and still off by default there.
- Blind rounds through the loop (BlastEm captures, since the in-app render plays one pass) decide whether the voices are
  heard as the chord.
