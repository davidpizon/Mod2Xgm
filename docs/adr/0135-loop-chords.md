# ADR 0135: The simple mapper reads a triad's notes from a loop with no one cycle

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

[ADR 0133](0133-simple-fm-chords.md) plays a chord sample's notes on FM voices of their own when its loop repeats at
the chord's common cycle, so the notes are harmonics of that cycle. A recorded chord rarely repeats like that. Its notes
are tempered, detuned or chorused, and no cycle the pitch search reaches (1,024 frames) repeats. ADR 0125 plays such a
loop at its bottom note on one FM voice, so the rest of the chord is lost (plan T5.3p).

David asked which next step was better, fitting FM shapes more closely or finding more chords. He chose chords, measured
on the corpus first, and triads first, leaving out two-note fifths (2026-11-21).

The corpus and FunkyChristmas have 22 tone loops with no one cycle. A measurement script grouped each loop's strong
partials into harmonic series:

| Read as | Samples |
|---|---|
| Triad or four-note chord | FunkyChristmas 2 (G A# D), 14 (G B D), 15 (G A# D); ai_renaissance 2 (G# B D#), 3 (G# C D#); omg-plz-help 11 (C D# G); rubbish 23 "CHORD 7TH" (B D F A), 24 "CHORD MAJ" (A# D F) |
| Fifth | stardust_jam 15, 20; FunkyChristmas 19 |
| Unclear | rubbish 2 |
| Noise, chorus or many series | 8bit_castle 3; stardust_jam 2, 10, 21, 22, 25; hawks_claw 2, 3; rubbish 10, 13 |

## Decision

- **Which samples.** A sustained tone the simple mapper plays on FM whose loop has no one cycle (`SamplePitch.Chord`),
  and whose loop spectrum makes a chord (`LoopSpectrum.Chord`, `LoopChord.IsChord`):
  - **Partials:** in the loop's 65,536-point spectrum (ADR 0125), bands 2 cents apart. A partial is the loudest band
    within 80 cents, at least 10 dB above the quietest band within 200 cents, and within 30 dB of the loudest partial.
  - **Series:** the lowest partial not yet grouped that is within 20 dB of the loudest (`SamplePitch.StrongDb`) starts a
    series. Each ungrouped partial within 30 cents of one of its first 12 harmonics joins it.
  - **Notes:** a series with at least two partials, its loudest within 12 dB of the loudest partial, is a note. Its
    share is the power its partials hold.
  - **A chord:** 3 or 4 notes on at least 3 pitch classes, in at most 8 series, holding at least half the loop's power.
    A fifth (2 notes) and a fifth plus its octave (2 pitch classes) are not chords yet. Many series mean a chorused or
    noisy sound.
- **How it plays.** As in ADR 0133, each note gets an FM channel of its own: the same channels, top notes left out
  first, the same source tracking, and level shares whose powers add up to 1 (here from the notes' power shares). Each
  voice plays at its interval in cents above the bottom note, where the sample's FM notes are already placed.
- **Told.** The chord note names the notes at C-2 ("a chord (G3, B3, D4 at C-2, read from its loop)"). The pitch note
  of every loop with no one cycle now says how many notes and series its partials make, and the share they hold.

## Results on the corpus

The in-app detector plays these as chords:

| Sample | Notes at C-2 |
|---|---|
| FunkyChristmas 2 | G4, A#4, D5 |
| FunkyChristmas 14 | G3, B3, D4 |
| FunkyChristmas 15 | G3, A#3, D4 |
| ai_renaissance 2 | G#2, B2, D#3 |
| ai_renaissance 3 | G#2, C3, D#3 |
| stardust_jam 21 | C#3, E3, G#3 |
| omg-plz-help 11 | C4, D#4, G4 |
| rubbish 2 | F3, A#3, G4 |
| rubbish 23 "CHORD 7TH" | A2, B2, D3, F3 |
| rubbish 24 "CHORD MAJ" | A#2, D3, F3 |

All eight measured triads are found. Stardust_jam 21 is a 2,188-frame loop that the measurement's 8,192-point frames
smeared into nine series. The in-app spectrum finds three notes in three series holding 82 % of its power, a C# minor
triad. Rubbish 2 reads as F, A# and G, which isn't a common chord.

Solo renders of each sample, XGM2 in-app against libopenmpt, compared by 12-note chroma similarity (1 is the same pitch
classes in every frame):

| Sample | Before (one voice) | After (chord voices) |
|---|---|---|
| FunkyChristmas 14 | 0.40 | 0.62 |
| FunkyChristmas 15 | 0.60 | 0.72 |
| ai_renaissance 2 | 0.56 | 0.84 |
| ai_renaissance 3 | 0.55 | 0.69 |
| stardust_jam 21 | 0.49 | 0.77 |
| rubbish 2 | 0.51 | 0.52 |

Stardust_jam 21 is confirmed as a chord. Rubbish 2 neither gains nor loses, so it stays in until a listening round says
otherwise.

David chose the chord voices in blind round 37 (2026-11-21), in all four pairs. Each was a 15-second full-mix clip,
in-app XGM2 renders against the original, cut where the two versions differ most: FunkyChristmas 2:38, ai_renaissance
3:22, stardust_jam 3:43 and omg-plz-help 0:33. Version order was mixed, and the versions had no names until he had
chosen.

Ai_renaissance 3 has 8 series, the faint ones from its recording. A first limit of 6 left it out. With the
pitch-class rule, 8 still leaves out every noisy loop (9 series or more, or no 3 notes holding half the power), and
stardust_jam 20's C#2–G#2–C#4 (a fifth and its octave).

## Options considered

- **Chosen: notes as harmonic series of the loop's partials, triads and four-note chords only.** It finds every
  measured triad and none of the noisy loops.
- **Every strong partial as a note.** A note's own harmonics would each become a voice.
- **Fifths as two-voice chords.** Left for later (David: triads first). A fifth on one voice already sounds close, and
  it costs a channel.
- **Recordings on the DAC** (`--require-dac`). Still available per sample. David's rule is that chord samples go to PCM
  only by that option.

## Consequences

- Songs with such loops use more FM channels on the default mapper, so their XGM, XGM2 and DMF outputs change.
- A detector mistake plays wrong notes, not just a poorer sound. A listening round on musix-rubbish decides whether
  its sample 2 stays in.
- Fifths, and chords of more than four notes, still play one voice.
