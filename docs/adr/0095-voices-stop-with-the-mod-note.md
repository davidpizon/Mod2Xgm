# ADR 0095: A voice stops when the MOD's note falls silent

- **Status:** Proposed
- **Date:** 2026-11-09

## Context

The plan holds a Genesis voice from its note until the song channel's next note or a note-off (ADR 0059). Two kinds of voice
can't fall silent on their own when the MOD's note does:

- **FM.** The key stays on, so a patch whose carriers hold a sustain level keeps sounding after a one-shot sample has played
  out. The FM fitter renders a held note (`FmNoteRenderer`) and only looks at the sample's length, so a patch that never decays
  scores as well as one that does.
- **The DAC.** A recording plays at the volume its note starts with ([ADR 0040](0040-simple-mapper.md)). A looped sample the MOD
  fades out with a volume slide (`Axy`) or `C00` keeps playing at full volume on the DAC, up to the 10 s a loop is played out.

David heard both in blind rounds 16–18 (with and without T9.4b's pre-mixes, so neither comes from pre-mixing):

| Song | What David heard | Measured cause |
|---|---|---|
| k0w-rsnd, 0:38–0:58 | "a stray tone on the left speaker only, very distracting" | Sample 1, a 60 ms plucked one-shot, on FM (280 notes on the T9.4b branch): the "Lead" patch's carriers have `DR 0`, so each note rings at full level for the whole row. The 300–450 Hz band on the left is 10 dB over the original. |
| musix-omg-plz-help, 0:20–0:22 and 0:25–0:27 | "holding a loud chord that are barely audible quarter notes throughout the original" | Sample 11, a looped chord on channel 2, faded within two rows by `A0F` to 4/64; on the DAC it plays on at full volume until the next note (15.3–17.8 s on main, 4.6 dB over the original). |

## Decision

**The optimizer cuts a voice where the MOD's note falls silent before the channel's next note**
(`OptimizerMapper.Cuts`):

- An FM voice, and its FM layer ([ADR 0089](0089-fm-layer.md)), on the tick after its one-shot sample has played out
  (`SongNote`'s end, when the channel starts no note there).
- A recording on the first tick from which the MOD's volume stays 18 dB or more below the volume the note started at, to the
  note's end (`FadedOutDb`). At 18 dB down, silence is closer to the MOD than the full volume the DAC would keep. omg-plz-help
  runs at 3 ticks a row, so its two `A0F` rows end at 4/64 (−24 dB), not at 0. A volume that only dips (a tremolo) cuts nothing.
- PSG voices follow the sample's loudness and the volume already, so they get no cut.

On a row's first tick the cut is a note-off cell; later in the row it is a `NoteCut` effect (DMF `ECxx`) in that row's cell,
marked `Ends`. A cell holds one cut: a MOD's own `ECx` in it merges with the plan's cut, on the earlier tick. The cut is left
out when:

- another note has taken the column by then;
- the channel starts another sample later in the same row (a retrigger, `E9x`, or a delayed note, `EDx`): that start takes
  the voice over, so an earlier stop would only cut the start short;
- a read of the row would cut where the note still sounds. The renderer reads a row again on each pass of a pattern loop
  (`E6x`) and on each repeat of a row held by `EEx`, and cuts on the same tick of every read, so a cut is kept only when every
  read wants it on that tick;
- the note ended because the channel swapped to another sample without a new note (an instrument-only cell): that sound goes on.

**The Genesis renderer plays the plan's cuts** (`GenesisPlanPlayer`): on its tick a `NoteCut` marked `Ends` stops the voice,
letting go of an FM key or stopping a recording, as a note-off does. A MOD's own `ECx` only sets the volume to 0, as
ProTracker does, so a later volume command can bring the note back: FM and PSG voices already follow that volume, and a
recording that stays silent to its note's end gets the plan's cut above.

## Options considered

- **Cut in the plan (chosen).** One place decides it, and the DMF, the render and the XGM writers (M10–M11) all play the same cut.
- **Stop the voice in the renderer only**, from the playback's sample position and volume. Simpler, but then the render no longer
  plays what the file does (ADR 0059), and the DMF would keep the fault.
- **Penalise sustaining patches in the FM fit** (render past the sample's end). That treats FM only, and a fit can still
  hold: a cut is needed anyway for the key to go off where the MOD goes silent.
- **Restart a faded recording with quieter copies.** Follows a slide more closely, but each step costs a stored copy against
  slots and bytes. Left open.

## Consequences

- FM notes on one-shot samples release at the patch's release rate once the sample has played out, as the MOD does. A
  recording faded by a slide still plays at its starting volume until the volume reaches 0. Partial fades are not followed.
- The simple mapper does not add cuts yet: it puts one-shot samples on the DAC unless `--require-fm` moves them.
- A note whose cut is left out above (a loop pass or an `EEx` repeat that disagrees, a stop before a retrigger) holds as it
  did before this ADR.
- **Verify:** DefleMask's `ECxx` on an FM channel keys off (the DMF's existing `ECx` translation assumes so).
- To be checked by ear in a blind round on k0w-rsnd and musix-omg-plz-help (status Proposed until then).
