# ADR 0132: The Lead FM patch sounds at its own pitch

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

After T5.3l ([ADR 0131](0131-tail-loop-decay.md)), David still heard a stray note at the end of FunkyChristmas, just
before it loops (2026-11-20). Looking for it turned up a separate problem with the library's Lead patch (plan T5.3m).

The Lead patch (algorithm 4) modulated OP2 from OP1 at TL 30 with feedback 5. That moved almost all of the carrier's
power into its sidebands. The fundamental came out about 25 dB under the octave above it, so every Lead note was heard
an octave high:

| FunkyChristmas, solo render | Note (dB) | Octave (dB) |
|---|---|---|
| Melody (sample 23), original | D4 −45 | D5 −42 |
| Melody, Lead before | — (under −58) | D5 −41 |
| Melody, Lead after | D4 −41 | D5 −44 |
| Ending chord (sample 1), Lead before | C5 −71 | C6 −38 |
| Ending chord, Lead after | C5 −43 | C6 −40 |

The 12-note chroma checks of [ADR 0125](0125-chord-samples.md) ignore the octave, so they didn't catch it.

## Decision

- **Lighter modulation on OP1:** TL 30 → 42 (9 dB less) and feedback 5 → 3. Everything else is unchanged. The note is
  then about as loud as its octave, as in the original's melody.
- A test renders a Lead note on the chip and checks that its fundamental is at least as loud as its octave.
- David chose it in blind round 36 (2026-11-21), in both pairs. Of the ending he said, "Version X uses multiple FM
  instruments to represent the chord in the original. Version Y uses a single note." In the middle of the song he said
  it could go either way, but it sounded closer to him. Hearing both through the loop in BlastEm, he said he liked v1's
  sound better.

## Options considered

- **Chosen: OP1 TL 42, feedback 3.** Of the three variants tried, it came closest to the original's balance between
  the note and its octave.
- **OP1 TL 42, feedback 3, OP4 TL 14.** The fundamental was a little stronger again (D4 −40 / D5 −46), which moved it
  further from the original's balance.
- **OP1 TL 48, feedback 2, OP4 TL 12.** Close to a plain sine pair: the darkest variant, and the farthest from the
  original's octave balance.

## Consequences

- Every sample the simple mapper gives the Lead patch (brightness 0.25–0.45) sounds an octave lower than before, at its
  own pitch.
- The optimizer and FM fitter start from the library, so a Lead start point now sits at the right octave.
- It doesn't fix the stray note. Playing the file through its loop in BlastEm showed nothing carried over the jump:
  `Xgm2Writer` already lifts every key at the loop frame ([xgm.md](../formats/xgm.md)). The stray note is the ending chord.
  In the last bars the melody rests, and sample 1's C–E–G chord plays on one FM voice as a lone C (David, 2026-11-21).
  Playing that chord as a chord is a separate decision.
