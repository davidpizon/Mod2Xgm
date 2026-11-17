# ADR 0117: Loops that aren't tones: a tail after a recording, or noise

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

In `brun_jul` the sleigh bells (sample 5, 8188 bytes) were heard as a low brass hum, about 48 Hz, in every one of their
134 notes. The sample is a 7434-frame bell hit that ends in a 654-frame loop of hiss. Two rules went wrong together:

- The simple mapper ([ADR 0040](0040-simple-mapper.md)) took any sample that loops as a sustained tone and played it by
  FM (Brass, by brightness).
- `SamplePitch` measured the pitch from the loop repeated over and over. A loop of hiss matches itself only at its own
  length, so the 654-frame loop read as a 654-frame cycle: at B-3 (31 389 Hz) that is 48 Hz.

David asked for both to be fixed (2026-11-17, plan T5.3b), and for the mapper to log every decision and assumption like
these.

## Decision

- **`LoopShape`** (`src/Mapping/LoopShape.cs`) says what a loop is, for the mapper and the pitch measurement alike:
  - **noise**: `SampleTraits` finds the loop to be noise (bright, with no repeating shape);
  - **a tail**: the recording before the loop is at least 2048 frames and at least 4 times the loop's length. What is
    heard is the recording; the loop only holds its end;
  - otherwise **a tone**.
- **The simple mapper** plays a tone loop by FM, as before. A noise loop, or a tail after a recording with no clear
  pitch (a bell, a voice, a crash), is played like a sample that plays once: by the DAC, or with `--no-dac` by the PSG
  noise channel (noise) or FM. A tail after a recording with a clear pitch (a plucked bass that settles into a loop)
  stays a sustained tone on FM, now at the recording's pitch: moving a busy pitched instrument to the one DAC channel
  made `musix-omg-plz-help` worse (9.14 → 9.93 dB) for the clashes it caused.
- **`SamplePitch`** measures a tone loop from the loop, as before. A tail loop is measured from the recording before it,
  as a sample that plays once is, and a noise loop has no pitch. So the loop's length is never taken for a cycle unless
  the loop is a tone: a one-cycle loop of a waveform keeps its pitch.
- **Every decision is logged** as a conversion note, the first time the instrument plays:
  - what the mapper took a loop to be (`its 654-frame loop is a short tail after 7434 frames of recording with no clear
    pitch, so it is played like a sample that plays once`);
  - where a sample plays, now including the DAC (`it plays once, so it is played as a recording on the DAC`);
  - for a sample played by FM, the pitch its notes are placed at (`its tone repeats every 31.98 frames, so its FM notes
    play at that pitch (C-2 sounds at 259.1 Hz)`), or that none was found.

## Options considered

- **Chosen: one loop test shared by the mapper and the pitch measurement.** They can't disagree about a sample.
- **No pitch whenever the only repeat found is the loop's length.** Rejected: a one-cycle loop of a waveform (most
  chip samples) repeats only at its length too, and that is its real pitch.
- **Only the noise test.** Rejected: the sleigh bells' hiss correlates enough with itself not to count as noise, so the
  bells would still have been a hum. The tail test is what catches them.
- **Every tail sample on the DAC.** Tried first: `8bit_castle` got better (15.52 → 15.32 dB), but `musix-omg-plz-help`
  (9.14 → 9.93) and `musix-rubbish` (12.01 → 12.70) got worse, where busy pitched instruments took the DAC from the
  drums. Only the unpitched tails move.
- **Every tail sample by FM at the recording's pitch.** Rejected: a bell, a voice or a crash isn't a tone FM can hold,
  and the DAC plays the recording as it is.

## Consequences

- On the corpus, the harness's `genesis` column (log-mel dB, lower is better) changes on five songs, 1.40 dB better in
  all (2026-11-17): `drozerix_-_ai_renaissance` 16.48 → 15.14, `drozerix_-_neon_techno` 12.80 → 12.37,
  `drozerix_-_stardust_jam` 9.79 → 9.83, `drozerix_-_the_hawks_claw` 11.48 → 11.55, `musix-rubbish` 12.01 → 12.26
  (two long unpitched recordings move to the DAC and clash with its drums). On `brun_jul` the hum is gone at every bell
  note.
- Samples moved to the DAC take PCM space and the one DAC channel; a clash there is settled as before (ADR 0040).
- The optimizer mapper uses the new pitch measurement too (`PlanVoicing`), so its FM candidates for these samples no
  longer start from a hum.
- Docs: [channel-mapping.md](../channel-mapping.md), [implementation-plan.md](../implementation-plan.md) (T5.3b).
