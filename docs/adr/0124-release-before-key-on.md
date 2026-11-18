# ADR 0124: An FM note is let go a tick before the next key-on on its channel (XGM and XGM2)

- **Status:** Proposed
- **Date:** 2026-11-18

## Context

David converted FunkyChristmas to XGM2 and heard that "most of the FM notes click on the attack" (2026-11-18, plan T5.3h).

On the YM2612, a key-on resets the operators' phase, and the attack starts from the envelope's current level. When a note
keys on over a note that still sounds, the wave jumps in one sample, and that jump is a click. The 1 ms attack of T5.3f
([ADR 0121](0121-soft-fm-attack.md)) only helps when the envelope starts from silence.

The player already lets go of the old note on the tick of the new one, but in the same frame as the new key-on. In XGM2
the two writes are microseconds apart, so the old note has not faded at all. This happens to every new note on a held
pad, to legato melody rows and to every `E9x` restart.

In per-sample renders of FunkyChristmas, 1,424 clicks were counted in the middle of notes. A click was counted where the
wave steps while sound was already there 2.5–10 ms before. With this change and [ADR 0125](0125-chord-samples.md) the
count fell to 118. Samples 1, 2, 19 and 23 had none left.

## Decision

- **Let go a tick early.** On XGM and XGM2, a sounding FM note is let go on the tick before its channel keys on again.
  Before the key-off, the player writes release rate 14 into every operator. That fades the note by 96 dB in about
  12 ms, inside the 20 ms tick, so the next key-on starts from silence and gets the soft attack.
- **What counts as a next key-on:**
  - a note of the next row that starts on its first tick;
  - a note this row delays to the next tick (`EDx`);
  - this row's note restarted on the next tick (`E9x`).
- **What doesn't:**
  - a tone portamento, which slides the sounding note;
  - a note delayed past its row's end, which never plays;
  - a note in its own first tick. A one-tick note is left alone.
- **Restore the patch's release.** A restart puts the patch's own sustain level and release rate back before it keys on.
  A new note loads its whole patch anyway.
- **The DMF is unchanged.** DefleMask plays the DMF's rows, and it has no register write on the tick before a row. The
  DMF and its render key on over the sounding note as before.
- **One conversion note** counts the notes let go early.

## Options considered

- **Chosen: let go one tick early at release rate 14.** The new note starts from silence. The cost is the last tick
  (20 ms) of a note followed by another on the same channel.
- **Key-off and key-on in the same frame (before).** Clicks: the old note has no time to fade.
- **Release rate 15.** The chip's fastest release, about 7 ms. Rate 14 still ends well inside the tick and fades more
  gently. Blind rounds may compare the two.
- **Ramp the TL down over the last tick.** TL is written once a frame, so the ramp is itself a step
  ([ADR 0123](0123-smooth-fm-volume.md)), and the TL must be restored for the new note.
- **Let go two ticks early with a slower release.** A 40 ms gap is heard on fast legato lines.

## Consequences

- On XGM and XGM2, and in the WAV render of them, a note followed by another on the same FM channel ends a tick early.
  Fast legato lines get a small gap.
- The harness's `genesis` column renders the DMF, so it doesn't measure this change. A/B clips and the per-sample click
  count do.
- The release rate (14) and the one-tick lead are constants in `GenesisPlanPlayer`. Blind rounds may tune them.
