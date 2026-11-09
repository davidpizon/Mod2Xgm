# ADR 0094: Voice steals through pre-mixes

- **Status:** Accepted
- **Date:** 2026-11-08

## Context

Plan task T9.4b. When more recordings sound at once than the target has PCM voices, the greedy allocator
([ADR 0087](0087-greedy-allocator.md)) moves a note to FM or PSG, or cuts it. On XGM2 (3 voices) neon_techno moves 21
notes of its samples 8 and 9 to FM for that reason alone, with no PCM budget ([ADR 0093](0093-optional-pcm-budget.md)).

[ADR 0052](0052-pre-mixed-pcm.md) and [ADR 0072](0072-pre-mix-candidates.md) planned the other way out: mix the
overlapping notes into one recording that plays on one voice. `PreMixer` builds such candidates for the harness, but the
allocator never used them.

David's answers (2026-11-08):
- every target: DMF, XGM and XGM2;
- a looped note that outlasts the mix: build both ways (a tail, or no mix at all), and his ears decide;
- memory: make room by shrinking first, or use only what is left over; his ears decide (only matters with a budget);
- loudness: no mix when it must be turned down by more than a limit, which the survey sets;
- glue only where needed, so a song that fits doesn't change;
- `--quality fast` mixes too;
- a mix of notes from both sides plays centred, as two-sided DAC does today ([ADR 0091](0091-shrinking-recordings.md)).

## Decision

- **A rescue after the allocation** (`OptimizerMapper.Rescue`). The allocator gives every section its sounds exactly as
  without pre-mixes. Then each note whose closest sound is a recording but that didn't get one is given it back, track by
  track in the song's order ([ADR 0092](0092-stable-voices.md)): it takes a PCM voice left free, or is mixed with a
  recording already playing (a note or an earlier mix) that covers every tick with no voice free (`OptimizerMapper.Mix`,
  `src/Optimizer/StealMixer.cs`). Fewest notes first, then the longest overlap; at most 4 notes per mix
  (`StealMixer.MaxNotes`), 4 tries per note. A rescued note gives up its FM or PSG voice, and those voices then go to
  notes that were cut, most important first.
- **No other note changes,** so a song that fits is untouched: a rescue only shares a voice or takes a free one.
- **A track keeps one sound.** A track that plays recordings somewhere in the song gets back every note it can. One that
  never does is rescued only when all its notes can be; otherwise it stays whole on FM or PSG.
- **Head and tail.** The mix is rendered through the MOD renderer from the first note's start to the end of the overlap,
  trimmed at −48 dB, and resampled to the target's rate (DMF: the DAC rate of the highest note). It plays at full volume,
  with no effects but note delay. A looped note that outlasts the overlap goes on as a **tail**: the rest of its sample from
  the next row start, unrolled, at its volume, written as a new DAC cell on the same column. With tails off, such an
  overlap isn't mixed.
- **Loudness.** The mix is measured after resampling and turned down to 127/128 (the top of 8-bit PCM) when it is louder.
  When that takes more than `PreMixRules.MaxClipDb`, it isn't mixed.
- **Bytes and slots.** Mixes with the same bytes are stored once; a mix replaced by a bigger one and played nowhere else
  is let go. Mixes count against the slots and the budget like any recording.
- **Pan.** A mix of notes from both sides plays centred.
- **Notes.** One conversion note per set of samples: `Samples 2 "…" + 6 "…" sound together with no PCM voice to spare, so
  they were mixed into one recording 35 times (0 with a tail; turned down up to 5.9 dB so the mix doesn't clip)`.
- **Rules** (`PreMixRules.Default`, set by the rounds below): on; tails on; clip limit 6 dB; make room by shrinking off
  (it only matters with a budget and wasn't surveyed, so a mix uses only the bytes left over).

### Corpus (XGM2, no budget)

Converted by the optimizer with the rules above, against `PreMixRules.None`. "Mixes" counts mixes played (with a tail in
brackets); the other columns count notes. Songs with no mix can still change: a note gets back a PCM voice left free.

| Song | Mixes (tails) | FM / PSG / cut with | FM / PSG / cut without |
|---|---:|---:|---:|
| 10kdub | 0 (0) | 580 / 0 / 0 | 580 / 0 / 0 |
| 8bit_castle | 58 (6) | 412 / 0 / 0 | 476 / 0 / 0 |
| cancan | 43 (0) | 248 / 17 / 0 | 300 / 17 / 0 |
| drozerix_-_ai_renaissance | 15 (0) | 3429 / 11 / 0 | 3460 / 11 / 0 |
| drozerix_-_neon_techno | 60 (0) | 703 / 0 / 0 | 767 / 0 / 0 |
| drozerix_-_stardust_jam | 0 (0) | 1765 / 345 / 14 | 1772 / 349 / 14 |
| drozerix_-_the_hawks_claw | 0 (0) | 764 / 489 / 106 | 801 / 519 / 106 |
| fx-poly1 | 0 (0) | 341 / 0 / 0 | 341 / 0 / 0 |
| hit | 23 (0) | 1516 / 754 / 0 | 1525 / 782 / 0 |
| jam_-_heatwave_cruise | 6 (0) | 232 / 0 / 0 | 280 / 0 / 0 |
| jam_-_latenightcoffee | 74 (0) | 1 / 130 / 0 | 2 / 224 / 0 |
| jam_-_xlr8 | 32 (0) | 550 / 0 / 0 | 594 / 0 / 0 |
| k0w-rsnd | 74 (1) | 240 / 320 / 0 | 280 / 384 / 0 |
| kc-dancinonamiga | 0 (0) | 192 / 0 / 0 | 192 / 0 / 0 |
| musix-cute-october | 13 (1) | 65 / 0 / 0 | 82 / 0 / 0 |
| musix-omg-plz-help | 4 (1) | 567 / 0 / 0 | 583 / 0 / 0 |
| musix-rubbish | 8 (3) | 7 / 22 / 0 | 7 / 32 / 0 |
| sharpojects_cantpleaseeveryone | 2 (0) | 491 / 0 / 0 | 493 / 0 / 0 |

No song loses a note to pre-mixes: every count goes down or stays.

### Rounds

Blind A/B rounds (original plus two versions, 20 s at the busiest moment where they differ), 2026-11-08 and -09:

| Round | Question | Song | Picked |
|---|---|---|---|
| 16 | mixes vs none | neon_techno | none |
| 16 | mixes vs none | 8bit_castle | mixes |
| 16 | mixes vs none | cancan | mixes |
| 17 | clip limit 6 dB vs 2 dB | 8bit_castle | 6 dB |
| 17 | clip limit 6 dB vs 2 dB | cancan | 6 dB |
| 17 | mixes vs none | k0w-rsnd | mixes |
| 17 | mixes vs none | jam_-_latenightcoffee | can't tell |
| 18 | tails vs none | 8bit_castle | tails |
| 18 | tails vs none | musix-rubbish | tails |
| 18 | tails vs none | musix-cute-october | can't tell |
| 18 | tails vs none | musix-omg-plz-help | can't tell |

- **Mixes:** 3 songs for, 1 against (neon), 1 tie.
- **Clip limit:** 6 dB on both songs. At 2 dB, castle keeps 12 of its 58 mixes and cancan 12 of 43; neon makes none at
  all.
- **Tails:** 2 songs for, 2 ties. Without tails castle makes 20 mixes instead of 58, and David heard its noise drum ring
  on too long.

## Options considered

- **A rescue after the allocation** (chosen).
- **Mix during the allocation, note by note, wherever a recording is closest:** lost on the corpus before any listening.
  A track mixed into a recording held a PCM voice that tracks chosen after it would have had, so they lost notes
  (ai_renaissance: 34 cut and 677 to PSG, against 0 and 11 without; neon: 91 drum notes to PSG noise), and neon's pad
  switched between FM and recordings.
- **Mix during the allocation only when the whole track then stays on recordings:** still worse. neon's looped pad,
  ranked above the drums, took recordings in some sections; the drums then needed tails to mix with it and lost 591 notes
  to PSG noise.
- **Use `PreMixer`'s candidates (ADR 0072):** they are scored for the harness over every overlap, and cost a full
  candidate pass. The allocator only needs the overlaps it can't voice, so it renders those on demand.
- **Steal the least important note without mixing (as before):** kept as the fallback when no mix is allowed.

## Consequences

- `DacSample.Mix` carries a mix's points; the DMF writer, the plan player and the PCM render play them like any sample.
- A note that crosses several crowded moments (neon's long pad, sample 9) isn't mixed: one mix covers one overlap, so it
  still moves. Chaining mixes along a held note would need a tail per seam; left for later if the corpus asks for it.
- neon_techno sounds better without mixes: its drums are mixed with the looped pad and turned down by up to 6 dB. A
  limit of 2 dB would avoid that on neon, but costs castle and cancan most of the mixes that won there. Left as is.
- Seven older allocator tests pin `PreMixRules.None`, since they check who keeps a voice.
