# ADR 0052: Pre-mixed PCM for repeated overlaps (T8.5)

- **Status:** Accepted; the decision "Tails share bytes where the format allows" superseded by [ADR 0072](0072-pre-mix-candidates.md)
- **Date:** 2026-10-30

## Context

PCM voices are the scarcest resource on the Genesis: DMF has one DAC, XGM four voices and XGM2 three, minus
`--reserve-sfx-channels`. When more samples sound at once than there are voices, consolidation step 8
([sample-capacity.md](../sample-capacity.md#consolidation-default)) steals a voice: the least important note
moves to FM/PSG or is cut.

[multi-pass.md](../multi-pass.md) already planned a way around that for plan task T8.5: a **pre-mixed PCM** candidate,
one sample containing both sounds. But it was offered only for *locked pairs*, two instruments that start together at a
fixed pitch interval. That misses the overlaps MODs are full of: a snare a tick after the kick, a kick over the start of
a long bass note, a hi-hat over a held chord. They repeat the same way all through a song, but they neither start nor
end together.

David asked whether repeated overlaps of two or more samples could be found and merged, wholly or in part, so that they
play on one PCM voice.

## Decision

(David, 2026-10-30)

- **T8.5 offers a pre-mixed candidate for any repeated overlap, not only for locked pairs.** An overlap is two or more
  PCM-eligible notes on different MOD channels sounding at the same time. Their starts may be offset and their ends
  needn't match.
- **Overlaps are found from the note events, not from audio.** The tick player ([ADR 0033](0033-tick-player.md)) gives
  every note exactly. An overlap's **window** runs from the first note's start until the overlap ends (a note ends, or
  is cut or retriggered on its channel). Its signature: the samples, pitches, volumes, start offsets and the effects
  active in the window (slides, vibrato, `9xx`, volume changes).
- **Windows are deduplicated by their rendered bytes.** The in-app MOD renderer (T7.1) renders each window's notes at
  the target's PCM rate and format. Windows with identical bytes are one pre-mixed sample. Near-identical ones are left
  to consolidation step 6 (merge near-duplicates), like any other sample.
- **Head and tail.** A pre-mixed sample holds the window: the first note alone for its offset, then the mix. A sound
  that outlasts the overlap continues as a **tail**, the rest of that note's own sample, started on the same voice
  when the pre-mixed sample ends. A looped note always continues as a tail: a loop is never baked in. When the
  outlasting sound is short, the candidate may instead bake it to its end (no tail and no seam, more bytes).
- **Tails share bytes where the format allows.** An XGM v1 slot holds its own address and size
  ([formats/xgm.txt](../formats/xgm.txt), sample id table), so a tail slot can point into the middle of the sample's
  existing data, on a 256-byte boundary (~18 ms at 14 kHz). XGM2 takes a slot's length from the next table entry, so
  an aliased tail probably works only where the tail directly follows its head — **verify** against `xgm2.txt` and
  rescomp; otherwise the tail costs its own bytes. In DMF a tail is its own sample.
- **The seam is checked, not assumed.** Sample sizes are padded to 256 bytes (XGM, XGM2), and the driver starts the tail
  only on a frame (about 233 samples at 14 kHz NTSC). T8.5 picks the switch point so these line up as well as they can,
  and the PCM driver model (T7.4) measures the seam. If it's audible, the candidate bakes to the end instead or isn't
  offered.
- **One quantization step.** The notes are mixed at their MOD volumes in floating point and quantized to 8 bits once,
  which is better than letting the driver add two separately quantized samples. A mix that would clip is scaled down,
  and a conversion note says by how much (the notes then sound quieter than their FM/PSG neighbours,
  [ADR 0050](0050-voice-levels.md)).
- **Candidates only; the allocator decides.** The allocator (M9) weighs each pre-mixed candidate's score against its
  cost (bytes and slots for the window and its tails) and its benefit (voices freed × duration × occurrences). A window
  is offered when it occurs at least twice, or once where it saves a note from voice stealing. Consolidation tries it
  at step 8, before stealing a voice.
- **User constraints.** Samples in `--require-fm`, `--require-psg` or `--ignore-samples` never go into a pre-mix.
  Samples in `--require-dac` may: they still play on the DAC, and their other notes keep their own data.
- **Every pre-mix is a conversion note,** e.g. `Samples 01 "kick" C-3 and 05 "bass" A-1 (+2 ticks) pre-mixed into one
  PCM sample, 14 times`. The output sample table names each pre-mixed sample by its parts. In DMF the DAC column then
  shows one sample where the MOD had two notes; accepted, since sound beats editability
  ([ADR 0005](0005-goal-accurate-sound-report-capacity.md)).
- **CH3 special mode is unchanged:** it stays a locked-pair candidate.

## Options considered

- **Any repeated overlap, head and tail, deduplicated by rendered bytes** (chosen).
- **Locked pairs only** (the earlier plan): misses offset and partial overlaps, which are most of the drum-on-bass and
  drum-on-drum collisions in MODs.
- **Find overlaps in the audio** (cross-correlate renders): an approximation of what the note events give exactly. The
  render is used only to deduplicate.
- **Pre-mix whole channels or the whole song** ([channel-mapping.md](../channel-mapping.md) option B): about 14 KB a
  second at the XGM rate, so a 3-minute song needs about 2.5 MB against a 128 KB budget.
- **Pre-mix whole repeated bars** (a drum bar repeated through the order list): a 2-second bar is about 27 KB, so a few
  could fit. Deferred, not ruled out: a later ADR can add it as one more candidate once T8.5 shows how often windows
  repeat.
- **Leave the mixing to the driver** (what happens without this): costs a voice per overlapping note, and step 8 steals
  when they run out.

## Consequences

- [multi-pass.md](../multi-pass.md) (passes 1 and 3, combined instruments), the T8.5 row of
  [implementation-plan.md](../implementation-plan.md), consolidation step 8 in
  [sample-capacity.md](../sample-capacity.md) and option B of [channel-mapping.md](../channel-mapping.md) point here.
- T8.5 needs the MOD renderer (T7.1) to build windows and the PCM driver model (T7.4) to measure seams.
- To verify: whether an XGM2 slot can alias a tail inside another sample's data.
- The console report's output sample table and [cli.md](../cli.md) get the pre-mix row format when T8.5 is built.
- Corpus result, when T8.5 lands: how many voice steals pre-mixing removes, and its score and listening effect.
