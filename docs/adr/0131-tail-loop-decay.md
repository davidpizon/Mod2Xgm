# ADR 0131: An FM note for a sample with a tail loop fades as the recording does

- **Status:** Proposed
- **Date:** 2026-11-20

## Context

David heard a stray note at the end of FunkyChristmas, just before the song loops (2026-11-20, plan T5.3l). It comes
from the conversion, not the MOD.

The note is the melody's last one, sample 23 at order 40, row 24, about 4.8 s before the end. Sample 23 is 6,760 frames
of recording that fades from −7 to −23.5 dB, then a 1,566-frame loop at −23.5 dB. ADR 0117 counts that loop as a tail,
so the simple mapper plays the sample on a sustained FM patch at the recording's pitch. ADR 0126 sets the voice's level
from the recording's hit. Nothing made the FM note follow the recording's fade, though, so a held note stayed at the
hit's level until the song ended. In a render of sample 23 alone, the original falls from −20 to −36 dB within 0.7 s;
the XGM2 stayed at −22 dB for all 4.8 s.

## Decision

- **Fade with the recording.** On XGM and XGM2, the Genesis player tracks where in its sample an FM note of a tail-loop
  sample (`LoopKind.Tail`) is, as it already did for noise. Each tick it multiplies the note's level by the recording's
  RMS there, over a 256-frame window, against the sample's loudest window. Past the loop's start that is the loop's
  level, so the note settles at the tail.
- **Don't fade twice.** The factor is scaled by the patch's attack level over its sustained level and kept at 1 or
  below. A patch whose own decay falls further than the recording is left to decay by itself.
- **Keep the hit.** The note starts at ADR 0126's level, so short notes sound as before, and ADR 0123's 6 dB-a-tick
  limit still applies.
- **The DMF is unchanged.** DefleMask changes a channel's volume only on a row, and the DMF keeps a note's level, as it
  does for faded recordings (ADR 0120). So does the DMF render.
- A conversion note names each sample whose FM notes fade, with the tail's level under the hit, when that is 1 dB or more.

## Options considered

- **Chosen: follow the recording's RMS tick by tick.** This is the same approach the noise player already uses. It
  handles a note that starts partway through the sample (`9xx`), restarts (`E9x`) and portamento with no extra rules.
- **Write volume steps into the DMF's volume column too.** That would cover the DMF, but on rows only (several ticks a
  step) and with many more cells. It is left for when a DMF needs it.
- **Bake the fade into the FM patch** (a decay rate and sustain level fitted to the recording). One patch is shared by
  every note at every pitch and length, so it can't follow a fade measured in frames of the sample. It would also change
  the patch DefleMask shows.
- **Play the last note shorter** (cut it at the recording's end). That leaves a step to silence where the MOD has a quiet
  tail.

## Consequences

- FunkyChristmas's last melody note fades as in the original. In the solo render, the original falls from −20 to −36 dB
  and the XGM2 from −23 to −36 dB, then holds at −39.
- The hit itself is about 3 dB under the original's, as it was before. Over the song, sample 23 now plays about 3 dB
  under the original where the held level used to hide this. It is still above the chords (5–6 dB under, ADR 0126),
  about level with the bass. Matching the hit more closely is a separate change to ADR 0126's level.
- On the corpus, seven samples in six songs fade by 3 dB or more: drozerix_-_stardust_jam (sample 1, 9.6 dB),
  musix-omg-plz-help (3, 5.4 dB), musix-rubbish (7, 3.1 dB; 21, 14.1 dB), allsort (20, 3.1 dB),
  jam_-_highway_encounter (13, 13 dB) and FunkyChristmas (23, 17.1 dB). Others have tails within 1 dB of the hit.
- The harness's `genesis` column renders the DMF, so it doesn't measure this change. A/B clips of the XGM2 render do.
- **Listening round 35** (David, 2026-11-20, blind, XGM2 with `--require-dac 19`):
  - Mid-song, 4:25–4:40: David chose this change, because "the original notes seem to have a brief decay", which it
    plays better.
  - The last 10 s: David chose the old version. In those bars the melody rests and channel 2 repeats sample 1, a chord
    sample, which plays its bottom note on FM (ADR 0125). The old held melody note filled out the chord by accident.
    With it gone, one FM note is left where the original has a chord. What is missing at the end is sample 1's chord,
    not the fade.
