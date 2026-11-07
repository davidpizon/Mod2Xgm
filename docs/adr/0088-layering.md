# ADR 0088: Layering: a quiet PSG tone under an FM note, chosen by ear

- **Status:** Accepted
- **Date:** 2026-11-07

## Context

Until now each MOD note got one voice, so a 4-channel MOD sounded at most 4 of the Genesis's 10 channels. David asked whether
overlaying channels could make an instrument closer, and ruled that the output may use more channels than the MOD, except those
the CLI reserves ([ADR 0087](0087-greedy-allocator.md)). Plan T9.3b builds it behind `--mapping optimizer`, and his grades decide
what stays.

## Decision

**An FM note also sounds a PSG tone at its pitch, 12 dB below the tone's level alone** (`OptimizerMapper.ToneLayerLevel`). Nothing
else is layered: a recording plus PSG noise was tried and rejected.

**How a layer is placed:** after every note has its own voice and column (`OptimizerMapper.AddLayers`), so a layer takes only what
nothing needs and can't cost a note anything, and `NotesMoved` and `--no-consolidate` don't count layers. Tracks go by importance,
section by section. A track takes tones only when every note it has in the section can, and only when all of them find a column,
so its sound doesn't change note by note.

**Which notes can't take one:** a note that slides, as a PSG tone holds its starting pitch; a note below the tone channels' range
(about 109 Hz); a sample with no steady pitch; a sample in a `--require-*` list, which keeps to its one kind. Tones come from
`TargetLimits.PsgTones`, so anything the limits take away stays free.

**In the plan:** `PlanCell.Level` is the factor on a tone's level. The layer's cell has the same `Source` as the note's own, so it
follows the MOD channel's volume, and both the renderer and the DMF writer play it.

**What three blind rounds settled (David, grades out of 5, each pair next to the original MOD):**

| Round | Song | Compared | Grades | What David said |
|---|---|---|---|---|
| 1 | cancan | Tone 6 dB down / none | 5 / 4 | "absolutely perfect"; without it, it "lacks the echo effect" |
| 1 | kc | Tone 6 dB down / none | 4 / 4 | The tone gives the melody "the correct fuzz sound" |
| 2 | hawks_claw | Tone 6 dB down / none | 4 / 5 | The tone "stops and starts the instruments", a stutter |
| 2 | kc | Tone 6 dB down / tone at its full level | 5 / 4 | 6 dB down is better |
| 2 | xlr8 | Noise under drum recordings / none | 3 / 4 | Noise makes the snare and hi-hats "crunchy" |
| 3 | hawks_claw | Tone 12 dB down / none | 5 / 5 | "nearly identical"; the stutter is gone |
| 3 | cancan | Tone 12 dB down / tone 6 dB down | 5 / 5 | The banjo is "slightly more accurate" at 12 dB |
| 3 | kc | Tone 12 dB down / tone 6 dB down | 4 / 4 | 6 dB down is "slightly too crisp"; 12 dB down "more rounded" and more accurate |

The 12 dB tone was never played directly against no tone on cancan or kc. That it helps there follows from the rounds above: it beat
the 6 dB tone, which beat or matched no tone. Its gain is small, and the grades are from three songs.

## Options considered

- **The tone 12 dB down, with no measure choosing where.** Chosen: David preferred it or couldn't separate it in all three rounds' comparisons.
- **Let the ruler choose where to layer.** On kc's horn FM alone measures 6.33 dB; with a tone it measures 8.2 to 9.0 at every level
  tried. Requiring 1 dB of gain layered no note on kc, cancan or xlr8, including where David's ears then preferred the layer. So
  this is another place where the ruler and his ears disagree (ADR 0049, ADR 0087).
- **A louder tone.** The 6 dB tone stuttered on hawks_claw and was "too crisp" on kc, and the full-level tone lost to it on kc.
- **PSG noise under a recording with no steady pitch.** At its level alone it sat about 35 dB under xlr8's mix, too faint to grade,
  so it was played 12 dB louder, and lost 3 to 4 as "crunchy". Rejected and removed. It wasn't tried quieter, as quieter is the level
  already too faint to matter, and neon's noise channel never had room for it in the clip.
- **A tone that comes and goes within a track's section.** Removed, as a matter of consistency rather than because it was heard: it
  was first suspected of causing hawks_claw's stutter, but removing it left the layer in that clip measuring the same, so that
  version was never played. The stutter went when the tone went to 12 dB down, which is why the level is taken as its cause.

## Consequences

- A song with free tone channels now uses more voices than it has MOD channels. A DMF has more notes on its PSG columns, and
  conversion notes say which samples took tones.
- Tones are in the plan for XGM and XGM2 too. Their writers (M10, M11) have to play `PlanCell.Level`.
- PSG tones can't pan, so a tone under a panned FM note plays in the centre. No grade pointed at this.
- **Still open, for later tasks, all heard in these rounds and none caused by layering:**
  - David suggests a second FM channel for kc's chords, its weakest part: an FM plus FM layer, not tried here.
  - xlr8 lacks the original's long held chords.
  - hawks_claw restarts a note that should sustain from 0:17 to 0:20.
  - kc's bass doesn't have the original's shape.
