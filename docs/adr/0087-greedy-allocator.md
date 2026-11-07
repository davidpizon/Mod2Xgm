# ADR 0087: A greedy allocator, its sounds and levels chosen by ear

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Plan T9.3 asks for importance scoring, greedy allocation per section, and `--require-fm` / `--require-dac` / `--require-psg`
as hard constraints. Until now only the simple mapper made a `GenesisPlan`; the candidates from plan M8 had nothing to choose
between them. The project's goal is the most accurate *sounding* result, and the scores agree only weakly with listening
([ADR 0049](0049-simple-mapper-baseline.md)), so David judged the numbers in blind listening rounds.

## Decision

`OptimizerMapper` implements `IChannelMapper`. `SoundMenu` makes what it chooses from.

**What David decided up front:**

- `--mapping simple|optimizer`, default `simple` until T9.6 compares them. `optimizer` wins over `--quality fast`. Any other
  value is a usage error (exit 1).
- The sounds are fitted FM, PSG tones and hiss, and recordings at every pitch played. CH3 and pre-mixes come later.
- All three formats. A plan can hold several recordings at once (`GenesisChannel.Pcm2`–`Pcm4`, `VoiceKind.PsgTone`).
- A section is one order. A track keeps its sound unless another is clearly closer (`SwitchCostDb`).
- Too many notes: a note takes its next best sound, and with none left the least important is cut, with a conversion note.
- A sliding note prefers FM. A `--require-dac` sliding note stays a recording at its starting pitch, with a conversion note.
- `--no-consolidate` exits 6 when any note lost its first choice to a limit.

**How it allocates:** sections go in order; within one, tracks (a sample on a channel) go by their notes' average importance.
A track first tries one sound for all its notes in the section, then goes note by note. Columns are then given in time
order. FM takes FM6 last, because it is the first DAC voice's only column: FM settled there silently cost a melody its
recordings. Notes are counted after columns are given, since that step can still cut one.

**What six blind rounds settled (David, grades out of 5):**

| Rule | Value | Evidence |
|---|---|---|
| A recording is ranked first | Its distance counts as 0: the ruler read other sounds as closer, and David disagreed | xlr8 3 vs 2 |
| A note's importance | Length counts up to 0.25 s; a fully percussive sample scores 9× one with no hit | xlr8 3 vs 2 |
| No tuned voice without a steady pitch | Drums, noise and samples whose pitch strays over ±25 cents use recordings, then PSG noise, then are cut. FM and PSG tones sounded like wrong notes. A `--require-*` list overrides this | xlr8 3 vs 2, then 5 vs 2 against simple |
| PSG noise level | The sample's loudest highs (above about 2 kHz), 12 dB down. The PSG's noise reaches far above what these samples hold | neon 5 vs 4 (6 dB); it had been "far too loud" |
| Bass pan | FM notes heard below 250 Hz on a panned channel play centred, 3 dB down. The Genesis has no 75/25 pan | xlr8 5 vs 4, kc 5 vs 4 |
| FM level of a fitted patch | Rendered and measured (`FmPatch.HeardRms`): the whole second for a sample that loops, the loudest 30 ms otherwise. The register formula takes a slow decay as already over, which left kc's horn and a chord 6.4 dB too loud | kc 5 vs 4 |

The quieter noise, centred bass and measured FM level apply only to a plan with `GenesisPlan.TunedByEar`, which the optimizer
sets. The simple mapper stays as graded in ADR 0049.

Against the simple mapper on songs the rules were not tuned on, the optimizer won cancan 4 to 2 and hawks_claw 4 to 1.

**Effort:** `SoundMenu.FmRendersPerStep = 300` caps each FM fit's step (David: about 5 minutes for a typical song). Measured with
an empty cache: kc 42 s, cancan 76 s, xlr8 96 s, neon 98 s, hawks_claw 143 s, musix-rubbish 158 s, and the corpus's largest,
sharpojects_cantpleaseeveryone, 188 s. `--time-budget` (T9.5) replaces it.

## Options considered

- **The chosen rules above.**
- **Trust the ruler's distances for recordings.** It preferred FM on drums and "random pitches" followed. David's ears win.
- **Let pitched "drums" and loops use FM, and match FM level over a note's first quarter second.** This measured far better on
  hawks_claw: level −17.9 dB against the original's −17.6 (before, −21.2), bass within 0.2 dB, and 16 notes cut rather than
  about 360. David graded it **3 against 5**: "crackly", with wrong instruments at 16 s. Rejected, and the reason every rule
  here is tested blind against the version he already liked.
- **A darker noise rate instead of a lower level.** Measured the same in neon's opening, so never played to David.
- **Centre everything, or centre the bass without lowering it.** Centring everything loses the stereo picture; without
  the 3 dB drop the mix is up to 3 dB too loud.
- **The new rules for the simple mapper as well.** It would change a baseline other ADRs are measured against, for no gain:
  the optimizer replaces it at T9.6 if it stays better.

## Consequences

- **A song can lose notes though it fits.** With the DAC and noise busy, a sample with no steady pitch is cut while tuned voices sit
  free: neon loses about 13 % of its notes, all drum hits, and hawks_claw about 360. David graded these 5 and 5, so
  this stands, and "a song that fits keeps every note" in T9.4 means the notes the rules can place. `--no-consolidate`
  counts these cuts, so it exits 6.
- **Required recordings can still lose notes.** Exit 6 covers slots, bytes and having a PCM voice. Overlaps on a DMF's single DAC
  cut notes with a conversion note: `--require-dac` on cancan's banjo, which overlaps itself across channels, keeps 44 of 462.
  The simple mapper doesn't run the exit 6 check.
- `DacSample` now has one entry per pitch, and `GenesisPlan.PcmSamples` lists the samples whose first choice is a recording, even
  if clashes moved all their notes ([ADR 0086](0086-capacity-check.md)).
- PSG tones hold the pitch they start at. No LFO on fitted patches yet: the plan has none.
- **Still open, for later tasks:** cancan's melody jumps octaves and cuts out under its chords, and kc's chords have a slight click;
  David graded both songs well in spite of these, and neither cause is known. Pre-mixes, CH3, baked slides and PSG buzz are
  deferred, including David's idea of a pitched fuzz as neon's melody.
- **Layering is allowed** (David): the output may use more channels than the MOD, except those the CLI reserves. Today each note
  gets one voice, so a 4-channel MOD sounds at most 4 of the 10 channels at once. Layering is future work, not in the plan yet,
  and is to be graded by ear like everything here.
