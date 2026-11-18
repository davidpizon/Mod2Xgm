# ADR 0120: A recording whose note fades has the fade baked in (XGM and XGM2)

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Neither SGDK driver can change a PCM sample's volume while it plays. Every DAC note was therefore stored at the volume it
starts at, one copy per 3 dB ([ADR 0044](0044-dmf-effects-and-song-grid.md), [ADR 0091](0091-shrinking-recordings.md)),
and kept that volume until it stopped. [ADR 0095](0095-voices-stop-with-the-mod-note.md) stops a recording once its
volume reaches 0. A partial fade, though, still played at full start volume, and a fade to 0 played at full volume
right up to the end.

In `brun_jul`, order 2 strikes the sleigh bells once with a looping tail, then slides the volume down 1 a tick (`601`)
for the whole pattern. In the original, the bell rings and dies away over about 1.4 s. In the XGM2 file, its tail rang
at full volume for 10 s: a 132 KB copy. David heard the difference ("the original was intended to have a fade out with an
echo") and asked for a recording of the bells fading (2026-11-17, plan T5.3d).

## Decision

- **Bake the fade.** On XGM and XGM2, a DAC note whose MOD volume moves while it sounds plays a copy with that movement
  baked in. This covers a volume slide (`Axy`, `5xy`, `6xy`), tremolo, or a `Cxx` partway through.
  - The fade is each tick's volume against the note's first tick (`NoteFade`), applied from each tick's start, with
    2 ms ramps between ticks so it doesn't click.
  - The copy keeps its starting loudness step as before, so only the movement is new.
- **A fade to silence ends the copy.** When the volume reaches 0 and stays there until the note ends, the copy ramps down
  and stops there. `brun_jul`'s bell copy drops from 132 KB to 18 KB, and its recordings from 307 KB to 200 KB.
- **Notes that fade alike share a copy.** Two fades are the same copy when every tick's gain and length match.
- **Capacity counts what is stored.** The fade is part of the copy's key in `CapacityCheck`, `RecordingShrinker` and the
  optimizer's byte counts (`StoredCopy.Fade`). A song with many different fades needs more copies, and the shrinker's
  usual moves apply to them.
- **The DMF is unchanged.** DefleMask's writer makes its own copies, 3 dB apart, from the notes' starting volumes, so the
  render of a DMF plays every note at the volume it starts with, as before. The harness's `genesis` column renders the DMF,
  so it doesn't measure this change.
- No conversion note: the copy follows the MOD more closely than before, and the capacity report counts its copies.

## Options considered

- **Chosen: one baked copy per distinct fade.** It follows the MOD tick by tick, and costs bytes only for notes that fade
  without falling silent. A fade to silence usually saves bytes.
- **Restart the recording with quieter copies** (left open in ADR 0095). Each step would cost a copy and an audible restart.
- **Leave it.** Every fading recording would keep ringing at its start volume.

## Consequences

- XGM and XGM2 files, and their `--render-wav` renders, follow each recording's volume. On `brun_jul` the order-2 bell now
  dies away as in the original, instead of ringing for 10 s.
- A volume that rises during a note is baked in too. A copy that would go past full scale clips, which the existing
  clipping note reports.
- The DMF could do the same only with copies per fade made by the DMF writer. That's a follow-up if DefleMask output needs
  it.
