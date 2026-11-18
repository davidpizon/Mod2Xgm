# ADR 0126: An FM voice for a sample with a tail loop is as loud as the recording

- **Status:** Proposed
- **Date:** 2026-11-18

## Context

David heard that at 4:30 in FunkyChristmas the accompaniment drowned out the melody of "Jingle Bells" (2026-11-18,
plan T5.3k).

The melody is sample 23. It is 6,760 frames of recording followed by a 1,566-frame loop. The recording starts at −8 dB
RMS and fades. The loop is a tail at −23.5 dB. ADR 0117 already counts such a loop as a tail ("the recording is what is
heard") and places the FM voice at the recording's pitch. Its level, though, still came from the loop, because
`PlanVoicing.FmLevel` matched every looped sample by its loop's RMS. The FM melody therefore played about 15 dB too
quiet.

In per-sample renders around 4:30 the original has three parts at about the same level: sample 23 at −23 dB, sample 6
(bass) at −21 and sample 1 (chords) at −20. On the Genesis, sample 23 was at −40 while the others sat only 3–6 dB below
the original.

## Decision

- **Match a tail-loop sample by its recording.** On FM, a sample whose loop is a tail (`LoopKind.Tail`) is matched by its
  loudest 256-frame window against the patch's unsustained level, as a sample that plays once is.
- **Keep loops that are the held tone.** A sample whose loop is its held tone (`LoopKind.Tone`) is still matched by its
  loop.
- A patch measured from a render (`FmPatch.HeardRms`, the optimizer) keeps that measured level.

## Options considered

- **Chosen: the loudest window, as for a one-shot.** This is how hard the note hits, which is what a short melody note
  plays. It is consistent with how DAC and noise levels are set for one-shots.
- **The RMS of the whole recording before the loop.** It averages in the long fade, so it comes out about 6 dB under
  the hit. Short notes, which never reach the fade, would play too quiet.
- **The loop (before).** Up to 15 dB too quiet for a sample whose tail has died away.

## Consequences

- FunkyChristmas's melody plays about 15 dB louder. In solo renders at 4:30–4:35 it matches the original within 1 dB
  (2–3 dB louder just before). The bass is 2 dB under the original and the chords 5–6 dB under, so the melody is on top
  again.
- Any other tail-loop sample on FM also gets louder by the gap between its hit and its tail.
- The harness's `genesis` column measures this change (it changes the DMF too).
