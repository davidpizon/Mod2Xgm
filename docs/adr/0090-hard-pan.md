# ADR 0090: Panned channels stay on their side, chosen by ear

- **Status:** Accepted
- **Date:** 2026-11-08

## Context

The original is OpenMPT's render, which plays a MOD's channels at half their pan: a channel the MOD pans fully left is heard at
75/25 ([ADR 0054](0054-mod-renderer.md)). A YM2612 channel is left, right or both, nothing between, so the converter plays a
panned channel's FM and DAC notes on its side alone (`PlanVoicing.Side`). In a plan tuned by ear, bass notes are the exception and
play centred ([ADR 0087](0087-greedy-allocator.md)).

In round 8 David heard omg-plz-help's drums "far to the left", where the original has them only slightly left. Measured over
three songs, main's mixes lean 5 to 7 dB toward one side, where the originals are within about 1 dB of balanced, and are wider
than the originals. David suggested keeping a sound on one side only when the original plays it more than 75 % to that side,
measured as heard. For a MOD that centres every channel: a fully panned channel is heard at exactly 75/25, and none of the
corpus's 19 songs uses a pan effect (`8xx`, `E8x`).

## Decision

**A panned channel's FM and DAC notes stay on their side, as before, apart from the bass centring of ADR 0087** (David, round 9).
The 75 % rule is not used for MOD input, as it would centre everything.

**Round 9 (David, an either/or: X, Y or can't tell, each pair next to the original).** Main's hard sides against a build that
centred every panned FM and DAC note, FM at equal power, the DAC about 3 dB louder centred, as it has no level of its own. Two of
the songs are new to these rounds.

| Song | Clip | Choice | What David said |
|---|---|---|---|
| musix-omg-plz-help | from 0:47 | Hard sides | The original swings left-right-left-right at one point; hard sides keep that, centred is flat |
| 10kdub | from 0:00 | Hard sides | Some sounds should be in the right speaker only; centred is too mono |
| jam_-_heatwave_cruise | from 0:00 | Centred | With instruments missing, hard sides lean too far right; centred is more balanced |

The one win for centring comes from the lean, not from the width: notes cut on one side leave the hard-sided mix heavy on the
other.

## Options considered

- **Hard sides, bass centred (main).** Chosen: it won two of three, and David's notes on both are about the songs' stereo movement.
- **Every panned FM and DAC note centred.** The same as David's 75 % rule, measured as heard, for every MOD. Lost two of three: "too mono", "flat". Never committed.
- **The 75 % rule measured on the MOD's own pan.** A fully panned channel is at 100 %, so it plays on its side as now. It would centre only partial pans, and the corpus has none to hear. Not built.

## Consequences

- Nothing changes in the code. `PlanVoicing.Side`'s cut-off (a pan beyond ⅓ of the way to a side) stays.
- **Still open:**
  - The lean. When the converter cuts notes for capacity, the cuts can land on one side; T9.4's consolidation moves could weigh the balance between sides when they choose what to cut.
  - Songs with pan effects, and input whose original is heard further than 75/25 to a side (multichannel formats, [ADR 0063](0063-multichannel-mod-xm-it-input.md)). Neither has been heard.
- The lean measurements and David's notes come from three songs.
