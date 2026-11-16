# ADR 0114: A chord sample's notes as FM voices of their own

- **Status:** Proposed (David chose the approach and the drop rule, 2026-11-15; blind rounds decide whether it becomes the default)
- **Date:** 2026-11-15

## Context

In round 28 (a quarter of today's recording space, [ADR 0113](0113-squeeze-rounds.md)) David heard EM2, Burp and the
Spaceman and Heaven remixes lose "all the background chords". Plan T9.4t9 is the fix. The converter cut none of those notes. They
lost their recordings and moved to FM: in EM2, MELLO MAJ played 30 of 32 notes by FM, MELLO MAJG 71 of 111, MELLOW MAJ7
7 of 9.

These samples are chords recorded as one sound. The analysis hears them as a low root, 41–55 Hz, that doesn't sound
itself; the chord's notes are harmonics of it (kc's SynthFMaj, C-F-A, is harmonics 3, 4 and 5). One FM voice is fitted
to play that root, so the chord's notes go missing. The only chord help so far, [ADR 0089](0089-fm-layer.md), adds a copy
of the same patch 22 cents away, which thickens the sound but adds no notes.

David (2026-11-15) pointed out that the Genesis has six FM channels and the output needn't use as many channels as the
MOD, as layers already show ([ADR 0088](0088-layering.md)). Build the FM chords first, since they cost no recording space,
and when FM channels run out, drop notes rather than take a voice from another track.

## Decision

Behind the harness rule `rule:chords=on` (`ConversionOptions.Chords`) until a blind round decides:

- **The chord's notes.** For a region that is a chord (`SoundMenu.IsChord`), its notes are the harmonics of the root
  within 12 dB of the loudest that aren't harmonics of a lower note (`SoundMenu.ChordTones`). Only harmonics up to the
  16th count, four octaves up, and only the four loudest notes are kept.
- **One patch.** A chord sample is usually one instrument playing several notes, so all its voices play one patch.
  It is fitted with the usual FM fitter to one note's own harmonics (`SoundMenu.ToneTarget`): the note that shares the
  fewest harmonics with the others (`SoundMenu.ReferenceTone`). Each of the fitter's tries (library, exhaustive,
  genetic) is rendered as the whole chord, each note at its pitch and its measured share of the level, and scored
  against the sample. The closest is kept.
- **Only when closer.** The chord voices are offered only when, together, they are closer to the sample (log-mel) than
  the one-voice fit. Otherwise the region keeps today's sound and its second voice.
- **Placing them.** As with every layer, chord voices come after every note has its own voice, so they never cost a note
  anything (`OptimizerMapper.AddLayers`). The note's own voice plays the lowest note. The track's other notes take free
  FM columns, never FM6, and the same number for every note of the track in a section, so the chord doesn't change note
  by note. When there's room for fewer than all of them, **the top notes are left out first** (David). With no room at
  all, the note keeps today's layers.
- **Levels and pitch.** Each voice plays the chord's patch `ChordTone.Cents` above the note's pitch (1200 × log2 of its
  harmonic) at its share of the level (`PlanCell.Level`). The patch's `HeardRms` is the whole chord's, so all of them
  together sound as loud as the sample.
- **Told.** A conversion note per sample gives the chord's size, how many FM notes play it, and how many left out top
  notes.

## Options considered

- **Several FM voices of one patch, one per note, top notes dropped first (chosen).** It costs no recording space, and
  it uses the FM channels a quieter section leaves free.
- **CH3 special mode,** each of channel 3's four operators at its own pitch: one channel for a whole chord, but each note
  is a single operator, close to a sine, and XGM v1 doesn't allow the mode until a probe ROM confirms it. Left for later,
  for when FM channels are short.
- **A patch fitted for each note.** Closer to a mix of different instruments, but it takes three or four fits per chord
  instead of one, and a chord sample is usually one instrument.
- **Borrowing a voice from a quieter track** when FM runs out. David chose dropping chord notes instead: it never takes a
  sound from another track.
- **Giving recordings first to the samples FM plays worst** (importance × FM distance). Still open, and it may matter less
  once chords play as chords. Later, its own ADR.

## Consequences

- `SoundMenu`: `ChordTones`, `ToneTarget`, `ReferenceTone`, `RenderChord`, and `RegionSounds.Chord`, fitted only with
  chords on (one more FM fit per chord region, cached like the others).
- `OptimizerMapper`: a note's layers are now a list (`PlannedNote.LayerColumns`), so one note can hold several voices,
  and every stop, cut and volume follows all of them. Without chords every plan is unchanged.
- The harness takes `rule:chords=on` (docs/harness.md). Round 29 compares it with today's version at the budgets of
  round 28, on the passages where David heard chords go missing.
- If it wins, a later change turns it on by default and records that in a new ADR. The golden files change then.
