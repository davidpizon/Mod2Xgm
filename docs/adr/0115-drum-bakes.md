# ADR 0115: Drums baked into recordings, and recording bytes given where the fallback hurts most

- **Status:** Proposed (David chose both answers, 2026-11-16; blind rounds decide what becomes the default)
- **Date:** 2026-11-16

## Context

In round 30 (EM2 at a quarter of today's recording space, plan T9.4t9) David heard, in both versions, that "the noise
channel is a poor substitute for the percussion", and asked whether the drums could be baked into the drum voice. He
added that the converter should weigh baking drums into a recording on a general basis, for every song, not for EM2
alone.

What EM2 does today, at 532,736 bytes on XGM2 (3 PCM voices):

- DRU, plays all 13 of its notes on PSG noise, CLICKATIC 64 of 128, and "hello to paul penson" 60 of 79. None is cut.
- The drums lose their recordings for want of **bytes**, not voices. "please read the text" holds about 184 KB as nine
  copies (pitches and lengths), while DRU, (28,904 bytes in the MOD) gets none.
- Pre-mixes exist ([ADR 0072](0072-pre-mix-candidates.md), [ADR 0094](0094-pre-mixed-voice-steals.md)), but only as a
  rescue after the allocation, when a note that should be a recording finds no free PCM voice. A pre-mix saves voices, not
  bytes, and a rescue that needs bytes the budget doesn't have fails, so the drum stays on noise. Nothing weighs a baked
  drum against noise.

## Decision

David's answers (2026-11-16):

- **Bytes go where the fallback hurts most (score decides).** When recording bytes run short, the allocator measures,
  for each sample, how much further from the original its fallback is (PSG noise, FM, a PSG tone, or a cut) than its
  recording, and spends bytes where that loss per byte is largest, in the price ruler of
  [ADR 0110](0110-price-ruler.md). No fixed "drums first" rule: a drum on noise is expected to win often, but the score
  says so, song by song. This is the open idea of [ADR 0114](0114-fm-chords.md) ("recordings first to the samples FM plays
  worst"), widened to every fallback.
- **Both kinds of baking are candidates, the score picks.** Each is offered to the allocator like any other sound, with
  its bytes, its voices and its distance, and used only where it sounds closer and fits:
  - **Hits that land together:** drum notes that start on the same tick (kick and click) mixed into one recording that
    one voice plays. It saves voices and costs a few KB per distinct combination. Today's pre-mixes already build these;
    they become candidates in the allocation, not only a rescue.
  - **A whole repeating beat:** a stretch of the drum tracks (a bar, or the pattern's length) rendered once through the
    MOD renderer and replayed each time it repeats exactly. It replaces many hits with one recording on one voice, and
    costs the stretch's length in bytes, once per distinct stretch.
- **Behind harness rules first** (`rule:bytes=score`, `rule:bake=together|beat|both`), compared with today's version in
  blind rounds at round 28's budgets. A rule becomes the default only after the rounds.

## Options considered

- **Score decides, both kinds of baking as candidates (chosen, David).** General: it follows each song's own drums and
  budget, and needs no list of which samples are drums.
- **Drums always first** (a fixed rule). Simple, but it trusts the drum label ([ADR 0111](0111-neither-and-listening-models.md)'s
  CLAP labels aren't in the converter yet) and spends bytes on a drum even where noise already sounds close (hi-hats,
  T9.4t8).
- **Keep today's byte order and only add baking.** Baking saves voices, but EM2's drums lack bytes, so it wouldn't help
  there.
- **Hits that land together only,** or **a whole beat only.** Each wins in some songs: short, sparse hits favour the
  first; a loop repeated many times favours the second. The score chooses instead.

## Consequences

- Plan task T9.4t10. `OptimizerMapper`'s allocation weighs bytes by fallback loss; `PreMixer`/`StealMixer` candidates
  join the allocation; a new beat candidate (find exact repeats of the drum tracks' stretches, render them, store each
  once).
- A beat recording plays from its start, so a stretch is only replaced where it repeats from the same row with the same
  notes, volumes and effects; any difference starts a new stretch or keeps the hits.
- Every byte-exact test is unchanged with the rules off.
- The harness takes the two rules (docs/harness.md); squeeze rounds at round 28's budgets on the six biggest songs, with
  EM2's drums among the pairs.
