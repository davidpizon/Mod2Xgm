# ADR 0089: A second FM voice under a chord sample, chosen by ear

- **Status:** Accepted
- **Date:** 2026-11-07

## Context

Some MOD samples hold a whole chord: kc's `SynthFMaj` is C-F-A and `SynthMaj` is C-E-G. The analysis measures their common root,
which doesn't sound (103 Hz and 77 Hz, with the notes at harmonics 3, 4, 5 and 4, 5, 6), and those partials take up most of one
patch's four operators. David suggested a second FM channel for kc's chords, its weakest part ([ADR 0088](0088-layering.md)). Plan
T9.3c builds it behind `--mapping optimizer`, and his grades decide what stays.

## Decision

**An FM note of a chord sample also sounds a second FM voice: a copy of its patch, the two voices 22 cents apart and together 3 dB
under the sample** (`SoundMenu.SecondVoice`). kc's partials come in pairs about that far apart, which is what makes the chords beat.

**What counts as a chord** (`SoundMenu.IsChord`): the loudest harmonic is the third or above, and the two lowest are at least 20 dB
under it. The three chords in kc and cancan measure 34 dB or more; cancan's Banjo, the nearest sample that isn't one, 10 dB.

**How it is placed:** as in ADR 0088, after every note has its own voice, so it can't cost a note anything, and a track takes it only
when all its notes in the section can. It takes FM1 to FM5 only: FM6 stays free as the first DAC voice's only column. A sliding note
takes none. A chord takes the second voice instead of a PSG tone, not as well as one. `--require-fm` samples can take it, as it is FM.

**In the plan:** `PlanCell.Cents` puts a voice off the note's pitch, half the spread either way, and both the renderer and the DMF
writer play it. The pair's level goes through `PlanCell.Level` and the second patch's `HeardRms`.

**What four blind rounds settled (David, grades out of 5, each pair next to the original MOD).** He gives a 4 next to a 5 to say which
he prefers, not that a song got worse, so a pair's grades are read as an either/or:

| Round | Song | Compared | Grades | What David said |
|---|---|---|---|---|
| 1 | kc | Fitted second voice / none | 4 / 4 | Both sound "like a single 4 note sequence"; the original is "more layered, ambient, and mellow" |
| 1 | cancan | Fitted second voice / none | 5 / 4 | The guitar chords are softer with it, so closer |
| 2 | kc | Detuned voice / fitted voice | 5 / 5 | Can't tell them apart |
| 2 | cancan | Detuned voice / fitted voice | 5 / 4 | The fitted version seems to cut the banjo out more |
| 3 | kc | Detuned voice / none | 5 / 5 | Both "EXTREMELY close"; the chords "overpower the melody" |
| 3 | kc | Kept on their side / centred | 4 / 5 | Kept on their side is "a bit too much to the right" |
| 3 | cancan | Kept on their side / centred | 4 / 5 | Centred is "more accurately balanced to the original" |
| 4 | kc | 3 dB down / full level | 5 / 4 | Softer is more accurate |
| 4 | cancan | 3 dB down / full level | 5 / 5 | Can't tell much difference; the guitar chords are too loud in both |
| 4 | cancan | No second voice / detuned voice | 5 / 5 | The chords' shape is very good; too loud |

**This rests on two songs and is thin.** The detuned voice was never preferred to no second voice in a direct pairing, on either song;
it stays because it was preferred to the fitted voice, which was preferred to none on cancan, and because David wants to keep testing
it. The 3 dB is one preference on one song. None of it has been played on a song it wasn't tuned on.

## Options considered

- **A detuned copy of the first voice, 3 dB down.** Chosen: see the table.
- **A patch fitted to sound right together with the first**, every candidate scored as a mix with it. Lost to the detuned copy on cancan and tied on kc; it also spends a whole FM fit per chord. Removed.
- **Keeping chords on their channel's side.** T9.3's rule centres a panned FM note below 250 Hz as a bass ([ADR 0087](0087-greedy-allocator.md)), and it takes a chord's root for a bass, though the chord sounds at 260 to 440 Hz. Moving them to their side lost 4 to 5 on both songs, so the rule stays for chords as well, whatever its reason.
- **A second voice with slow carriers, to swell as the sample does.** The original's C chord rises about 6 to 7 dB over its first 0.2 s and FM's stays flat. Three tries failed before anyone could grade them: with the detune, beating hid the swell; without it, one chord stayed flat and the other came out 7 to 9 dB too loud. Never played. Still open.

## Consequences

- A chord now takes up to two more FM channels over a song, on channels no note needs.
- Tones under chords go: a chord with a second FM voice has no PSG tone.
- The detuned pair beats deeply: on the chords alone, up to 12 dB. David hasn't flagged it in the full mix.
- XGM and XGM2 writers (M10, M11) have to play `PlanCell.Cents` as well as `PlanCell.Level`.
- The chord rule reaches 8 of about 110 pitched samples, in 6 of the corpus's 19 songs (2026-11-07). Only kc's two and cancan's one are known chords; the other 5 have names that don't say, 2 of them with no steady pitch, so FM doesn't play those unless required.
- **Still open, for later tasks:**
  - Chords' level: David still hears cancan's guitar chords as too loud at 3 dB down.
  - The swell above, which is David's "shape" note on kc.
  - cancan's tambourine hits at 0:04, which David hears as missing, and its banjo cutting out where the bass comes in. The MOD does share channels 3 and 4 between them, so some of that is in the original; how much isn't known.
  - kc's rhythmic clicking, open since ADR 0087.
  - Whether the rule, its level and its spread hold on songs they weren't tuned on.
