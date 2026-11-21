# ADR 0134: At the XGM2 loop, FM notes releasing at the song's end go quiet

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

David's most important bug (2026-11-20): a stray note at the end of FunkyChristmas, just before it loops (plan T5.3o).
He described it as "a very short blip of an attack note", possibly an E. It stayed through T5.3l, T5.3m
([ADR 0132](0132-lead-fundamental.md)) and T5.3n ([ADR 0133](0133-simple-fm-chords.md)), each of which fixed something
else found while looking for it.

A BlastEm capture through the loop, read one speaker at a time, showed it. The last bars play sample 1's C–E–G chord on
MOD channel 2, which is on the right. Up to the jump, the chord's E5 sounds only in the right speaker, at −43 dB. At the
loop frame it shows up in the left speaker too, rising from −64 to −45 dB within one frame, then fades over about
200 ms. C5 and G5 do the same.

What happens at the jump:

1. `Xgm2Writer` sets the chips back as they were on the first time through. For a loop to frame 0, nothing had been
   written yet, so it only lets every key up. A released note goes on sounding through its release.
2. The loop's first frame is the song's first frame. The player sets every FM channel's pan to the middle there.
3. The chord's release tail moves from the right speaker to both. In the left speaker it starts from nothing, like a
   new note's attack.

The original cuts the chord at the jump: order 0, row 0 sets channels 2 and 3 to volume 0 (`C00`). The first time
through, nothing sounded there, so the player wrote nothing for it. In-app renders play the song once, so they never
showed the blip.

## Decision

- **At the loop frame, every FM slot whose total level the first time through hadn't set yet is set silent** (level 127,
  about 95 dB down), through the driver's `$9x` command, which keeps the driver's own copy of the level right. Then the
  registers that had been set are restored and the keys are set as before.
- Silent is how those slots were the first time through, so the loop now starts as the song did. That is what
  `StartLoop` was already meant to do, as it does for keys (up) and PSG volumes (off).
- Every FM note loads its whole patch when it starts, total levels included, so no later note stays silent.
- A loop to frame 0 adds 24 two-byte commands, 48 bytes once in the song. FunkyChristmas's loop frame still fits in one
  frame of the driver (149 frame splits before and after).

## Options considered

- **Chosen: silence the slots not set yet.** It cuts the tail at the jump, as the original does, and keeps every
  restored register as it was.
- **Silence every slot, then restore.** It sends a `$9x` twice for slots that had been set.
- **Fastest release (RR 15) at the loop.** The tail dies in a few milliseconds, but SL/RR would then have to be
  restored, and the pan still moves the first milliseconds.
- **Leave the pans alone at the loop.** The player's frame-0 pans are what every note after them relies on, and a tail
  on the wrong side would still sound into the song's opening, where the original is silent.
- **Play the `C00` of row 0 as a cut.** It covers only songs that silence their channels there.

## Results on FunkyChristmas

BlastEm captures through the loop, sample 1's E5 (the other chord notes alike), one speaker at a time:

| Around the jump | Right | Left |
|---|---|---|
| Before, last bars | −43 dB | −64 to −69 dB (background) |
| Before, first frame after the jump | −45 dB, fading over 200 ms | −45 dB, out of nothing, fading over 200 ms |
| After, last bars | −43 dB | −64 to −69 dB (background) |
| After, first frame after the jump | −132 dB | −134 dB |

The chord now stops at the jump, as the original's `C00` stops it. David heard the capture through the loop and confirmed
the blip is gone (2026-11-21).

## Consequences

- Every XGM2 file that loops gets the commands at its loop frame. For a loop past the start, slots the song hadn't
  set by then are silenced too.
- The byte-exact writer tests changed to include them.
- The DMF isn't affected: DefleMask plays its own loop.
