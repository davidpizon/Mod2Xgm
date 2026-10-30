# ADR 0048: MIDI input

- **Status:** Accepted
- **Date:** 2026-10-30

## Context

David asked how hard it would be to accept Standard MIDI Files (`.mid`) as input next to MOD. Reading the file is
small; the rest of the pipeline assumes a tracker module with sampled instruments:

- **The song model** ([ADR 0029](0029-song-model-types.md)) is a tracker grid: orders → patterns → rows, one cell per
  channel per row, one note at a time per channel, speed and BPM in ticks per row. MIDI is a stream of timed events on
  16 channels, each of which can play chords, with its own time base (ticks per quarter note and a tempo map).
- **Every `Instrument` points at a `Sample`**, and `Song` checks it. MIDI instruments are General MIDI program numbers
  (and drum keys on channel 10) with no audio.
- **The simple mapper and the optimizer work from samples** ([ADR 0040](0040-simple-mapper.md), M8–M9): routing by
  sample analysis, FM patches fitted to samples, DAC playback of samples.
- **The CLI contract and the report are sample-shaped** ([ADR 0008](0008-report-on-every-conversion.md),
  [ADR 0028](0028-console-report-sections-1-2.md)): every run prints the MOD metadata and the full sample table;
  `--ignore-samples` and `--require-fm/dac/psg` take MOD sample numbers 1–31; capacity and consolidation count samples.
- **Accuracy has no single reference** ([ADR 0005](0005-goal-accurate-sound-report-capacity.md),
  [ADR 0047](0047-accuracy-harness.md)): the harness scores against libopenmpt playing the MOD, which *is* the song. A
  MIDI file sounds different on every synthesizer. Note timing and pitch, on the other hand, have an exact reference:
  the MIDI events themselves.

Claude proposed a design on 2026-10-29; David answered its six questions on 2026-10-30.

## Decision

**By David, 2026-10-30:**

1. **Add MIDI input**, with DMF output first (XGM/XGM2 follow as for MOD).
2. **After M9**, as milestone **M13 — MIDI input**, once the optimizer works on MOD. It is numbered 13 so existing task
   ids don't change.
3. **General MIDI instruments are samples from Versilian Studios' VS Chamber Orchestra: Community Edition
   (VSCO-2 CE)**, CC0, referenced from its repository [sgossner/VSCO-2-CE](https://github.com/sgossner/VSCO-2-CE), not
   a ready-made FM bank. A MIDI song then has sampled instruments like a MOD, so the M8 fitter, the M9 optimizer and the
   DAC path serve it unchanged. **Only sources whose license plainly allows redistribution are used** (David,
   2026-10-30): freewavesamples.com and the Philharmonia sound samples were considered and dropped (Options considered).
4. **The DAC plays MIDI drums** (channel 10), from VSCO-2 CE's percussion.
5. **Accuracy for MIDI:** timing and pitch against the MIDI events themselves (exact); timbre against one fixed General
   MIDI SoundFont rendered by a harness-only synthesizer. Timbre scores guide the work; they are not ground truth.
6. **The tool keeps its name**, `mod2xgm`.

**Design (Claude's proposal, accepted with the answers above):**

- **Formats read:** SMF format 0 and 1, and RIFF RMID files (an SMF inside a RIFF wrapper). Format 2 (independent
  sequences) plays its tracks one after another, with a conversion note. SMPTE time division is converted to a tempo
  map, with a note.
- **Detection by content, not extension:** `MThd` (or `RIFF…RMID`) at the start means MIDI; anything else goes to the
  MOD reader as today.
- **Own reader, no library:** `src/Midi/MidiReader` → `MidiFile` (header, tracks, events with absolute ticks), the
  same way `ModReader` → `ModModule`. DryWetMIDI and NAudio.Midi (both MIT) aren't needed and their Native AOT support
  is unchecked.
- **Instrument set:** a map, kept in the repository as our own data, from each General MIDI program (1–128) and each
  drum key to one or more VSCO-2 CE samples, each with its root note and velocity range. `MidiToSong` picks, per note,
  the sample nearest in pitch and velocity, and builds one `Instrument` per sample the song uses, so `Instrument` and
  `Sample` stay as they are, and `NotePitch.PlaybackRateHz` is the sample's rate shifted by the note's distance from
  its root. A program played by a related instrument (e.g. string ensemble by the violin section) gets a conversion
  note.
  - **VSCO-2 CE** (recorded by Sam Gossner and Simon Dalzell, sample cutting by Elan Hickler; checked 2026-10-30),
    pinned by commit:
    - **Samples:** branch `master`, commit `440300901dfe9275fd84e0b7763af1f8443ae62e` (2020-08-05). 3,168 WAVs,
      about 2.2 GB. License file `LICENSE`, CC0-1.0 as GitHub reads it.
    - **Maps:** branch `SFZ`, commit `6dd651d55dde97fd4028699be9d4481f26917891`. One `.sfz` file per instrument and
      articulation (e.g. `SViolinVib.sfz`, `OrganLoud.sfz`), each region giving its WAV, key range, root note
      (`pitch_keycenter`) and velocity range, and `GM-StylePerc.sfz`, a General-MIDI-style drum map. The instrument
      map is built from these.
    - **Covers:** upright piano (two), organ, violin (solo and section), viola and cello sections, double bass, harp;
      flute, piccolo, oboe, clarinet, bassoon; horn, trumpet, trombones, tuba; timpani, glockenspiel, xylophone,
      marimba, tubular bells; bass drums, snares, congas, bongos, crash and suspended cymbals, gongs, tambourine,
      triangle, cowbell, claves, guiro and more (`Percussion/`, `VSCO 1 Percussion/`).
    - The WAVs are PCM stereo 44.1 kHz at 16 and 24 bits (checked on the 256-file "256-Sample Pack of Orchestral
      Sounds", a hand-picked subset from the same sessions that David downloaded first; its README allows "total and
      eternal public usage, redistribution, and modification"). The loader reads both depths and mixes to mono, with a
      note when the channels differ much.
    - **No loop points**, neither in the WAVs (`smpl` chunks) nor in the SFZ maps: sustained instruments need a loop
      found when the samples are prepared, or are left to FM, which needs no loop; decided in T13.2.
  - **Programs VSCO-2 CE doesn't cover** (guitars, bass guitars, saxophones, synth leads and pads, voices, sound
    effects) are played by **FM alone**, with patches fitted or picked by the optimizer, and **hi-hats and the ride
    cymbal by PSG noise**. One conversion note per song lists the programs and drum keys played without a sample.
  - Conversion output (DMF / XGM) holds resampled, trimmed copies of the samples inside a song, which CC0 allows.
- **`MidiToSong` turns events into the existing tracker grid**, so mappers and writers keep reading one `Song`:
  - **Grid:** rows per beat chosen per file, the coarsest of 4, 6, 8, 12, 16 or 24 that places at least 95 % of
    note-ons within half a tick; ticks per row chosen so the DMF tick rate gives sub-row timing through `NoteDelay`.
    Onsets that still move get one conversion note with a count and the largest shift. Patterns are cut at bar lines
    (from time-signature events, else every 4 beats).
  - **Tempo map** → `SetTempo` / `SetSpeed` effects, which `DmfTimingPlanner` already handles. No tempo event → 120 BPM,
    with a note. Very dense tempo maps are thinned to one change per row, with a note.
  - **Polyphony:** each MIDI channel becomes as many song channels as the most notes it holds at once, each song
    channel playing one note at a time, so `Song`'s meaning of a channel is unchanged. Fitting them onto the Genesis
    voices is the mapper's job; the reader never knows about the Genesis.
  - **Note-off** → `NoteCut`. **Sustain pedal (CC64)** delays note-offs in the reader.
  - **Velocity × CC7 volume × CC11 expression** → cell volume and volume changes. **CC10 pan** → `SetPan`. **Pitch
    bend** → pitch slides in cents, using the RPN 0 bend range (default ±2 semitones). **CC1 modulation** → `Vibrato`.
    **Program change** → instrument change. SysEx, GS/XG parts and other controllers are ignored, one note each.
  - **Text, copyright and track-name meta events** are the MIDI counterpart of MOD sample names (often the credits)
    and are reported in full.
- **Mapper:** MIDI often holds more notes at once than the Genesis has voices; the optimizer's voice stealing (T9.4)
  covers it, with one conversion note per song giving how many notes lost their voice.
- **CLI and report:**
  - Report section 1 for MIDI: format, tracks, time division, starting tempo and time signature, length, and every
    text / copyright / track-name event. Section 2: every program and drum key the song uses, per channel, with note
    counts and the sample each one plays. AGENTS.md's "MOD metadata and full sample table" becomes "the input's
    metadata and full instrument table".
  - `--ignore-samples`, `--require-fm`, `--require-dac`, `--require-psg` and `--player` on a MIDI input are a usage
    error (exit 1) in the first version; per-program equivalents can follow in their own ADR.
- **Harness for MIDI:** MeltySynth (MIT, pure C#) renders the reference, in the harness only, like libopenmpt
  ([ADR 0047](0047-accuracy-harness.md)). The SoundFont is chosen for its license (candidates: FluidR3_GM, GeneralUser
  GS, licenses still to be checked) and isn't committed if its size or license forbids it. A small corpus of freely
  licensed MIDI files goes in `tests/data/LICENSES.md`, plus files generated in code for tests.

**VSCO-2 CE can be committed and shipped**: CC0 allows redistribution. What limits it is size: 2.2 GB doesn't belong
in git or in the AOT binary. A preparation tool (under `tools/`) fetches the pinned commits above, verifies them, and
prepares only the samples the map uses (mono, trimmed, looped, resampled). T13.2 decides where the prepared set lives:
committed under the repository, or prepared at build time. The converter itself never downloads anything.

## Options considered

- **Samples from VSCO-2 CE only (chosen, David).** One pipeline for MOD and MIDI; real recorded timbres, fitted to FM
  or played on the DAC by the same optimizer. CC0, with SFZ maps giving root notes and velocity layers. Costs: no
  guitars, synths or hi-hats (FM and PSG noise play those), and unlooped samples to prepare.
- **[freewavesamples.com](https://freewavesamples.com/)** (David's first choice, dropped 2026-10-30): would have filled
  the gaps (electric guitars and basses, synths, hi-hats, ride). Custom freeware terms by Jason Champion that forbid
  placing copies on another website for download and say nothing about repositories or bundling in software, so
  shipping them is in question.
- **[Philharmonia sound samples](https://philharmonia.co.uk/resources/sound-samples/)** (dropped 2026-10-30): would
  have filled acoustic guitar, banjo, mandolin and saxophone. Their terms forbid making the samples available "as is",
  as samples or as a sampler instrument, which a converter shipping them comes close to, and the site-wide terms
  (clause 4) allow only personal, non-commercial use without written permission.
- **An existing OPN2 General MIDI FM bank.** Fast, but the bank's sound is fixed and its license unknown; it bypasses
  the optimizer.
- **Fitting FM patches to a SoundFont's notes.** Reuses M8, but adds a second set of sound sources next to the samples.
- **MIDI into the existing tracker grid (chosen).** One model for mappers, writers, the timing planner and the
  optimizer. Costs: grid rounding and voice splitting happen in the reader and are noted.
- **A second, event-based model next to `Song`.** Every mapper and writer would handle two models, and DMF and XGM are
  row/tick based anyway, so the rounding only moves.
- **Voice allocation in the reader (fixed to 6 FM + 3 PSG).** Puts Genesis knowledge in the reader, which AGENTS.md
  forbids.
- **A MIDI library (DryWetMIDI, NAudio.Midi).** Saves a few hundred lines but adds a dependency with unchecked Native
  AOT support.
- **Before M7 (Claude's first proposal).** Generalizes the model once, before the renderer and optimizer exist; David
  chose after M9 so MOD stays the focus until the optimizer works. Choosing samples (answer 3) makes the late start
  cheaper: the model barely changes.

## Consequences

- `docs/implementation-plan.md` gets milestone M13 after M9. Later, in the M13 PRs: `docs/architecture.md` (MIDI reader
  next to `ModReader`), a new `docs/formats/midi.md` (the SMF subset read, with **Verify** markers), `docs/cli.md`
  (input detection, usage errors, report sections for MIDI), AGENTS.md (required behavior item 1, repository layout)
  and `docs/sample-capacity.md`.
- New library ADRs in T13.5: MeltySynth and the SoundFont for the harness, each in `THIRD-PARTY-NOTICES.md`; the
  SoundFont is used only if its license plainly allows it, like the samples. VSCO-2 CE gets an entry crediting Sam
  Gossner, Simon Dalzell and Elan Hickler / Versilian Studios, with the pinned commits; prepared samples in the
  repository are listed in `tests/data/LICENSES.md` or next to them.
- Every reader rounding (grid shifts, thinned tempo changes, dropped controllers, substituted programs, programs
  without a sample) becomes a conversion note, as for MOD ([ADR 0004](0004-conversion-notes-and-logging.md)).
