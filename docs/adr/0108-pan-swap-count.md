# ADR 0108: Counting the pan swaps the MOD doesn't make

- **Status:** Accepted (David, 2026-11-13)
- **Date:** 2026-11-13

## Context

In rounds 23 and 24 (plan T9.4l, [ADR 0107](0107-learned-score-not-adopted.md)) David found it "very unsettling" when an
instrument moves between the centre and one speaker mid-phrase where the original doesn't. Lazy Cows' percussion and
Highway Encounter's voices did it. Every recording on XGM and XGM2 shares the DAC's one pan
([ADR 0091](0091-shrinking-recordings.md)). So a pre-mix of notes from both sides plays centred
([ADR 0094](0094-pre-mixed-voice-steals.md)), and a clash between sides is settled moment by moment (`DacPanClash`).

Plan T9.4r gives such a swap a cost the allocator weighs against the others, and either/or rounds find the threshold, if
there is one. Its first step is to count the swaps: which songs, which instruments, how long, how loud, and why. This ADR
records what counts as a swap and how it is counted. The cost and the rounds come in later PRs (David).

## Decision

David's answers, 2026-11-13:

- **A harness run counts them first, in its own PR:** `harness --pan-swaps` ([harness.md](../harness.md#pan-swaps)) writes
  `pan-swaps.csv`, one line per swap, and a summary in `report.md`. Nothing in the converter's output changes.
- **Formats:** XGM and XGM2, the two whose recordings share one DAC pan. DMF is left out.
- **Mappers:** the optimizer, with no byte budget as in the listening rounds, and the simple mapper, to compare.
- **Songs:** the corpus and the hold-out, 47 songs.
- **What a swap is:** an instrument (a song sample) changes the side it is heard on while its side in the MOD stays the
  same. All three kinds count: one side to the centre and back, one side to the other, and centre to one side.
- **Silence doesn't hide a swap:** a drum heard on the left, silent a while, then heard centred has swapped if the MOD
  kept it on the left over that time; otherwise it hasn't (David). The silence is listed as the swap's gap. When the MOD
  moves the instrument to a channel on the other side, the count starts over. A move to another channel on the same side
  is still a swap, and the line says so (`channel_changed`), so either reading can be filtered.
- **Nothing is too small to count:** every swap is listed with how long it stays on the new side and its MOD volume. The
  threshold is for the rounds to find.
- **An instrument steadily on the wrong side is no swap:** a drum centred the whole song never moves. It is listed apart,
  as "steadily off side".
- **Every cause counts, labelled:** the shared DAC pan, a centred pre-mix, an FM bass note played centred below 250 Hz
  (`PlanVoicing.CentredBass`, so a bass line crossing 250 Hz hops), and the PSG, which has no pan.

How it is measured: with `ConversionOptions.PanLog` set, the Genesis player (`GenesisPlanPlayer`) records each sounding
note's heard side and MOD side on every tick of the `--render-wav` render, after the DAC pan is set. A recording that has
played out is silent and isn't recorded. `PanSwaps.Count` takes, per sample and tick, the loudest note's sides. When its
notes on one tick have different MOD sides, the count starts over.

## Results

The full run, 2026-11-13: 47 songs, four versions each.

| Format | Mapper | Swaps | Shared DAC | Pre-mix | Centred bass | PSG | Songs with swaps | Steadily off side |
|---|---|---|---|---|---|---|---|---|
| XGM | optimizer | 14678 | 13505 | 0 | 909 | 264 | 43 of 47 | 293 |
| XGM | simple | 3033 | 0 | 0 | 0 | 3033 | 24 of 47 | 9 |
| XGM2 | optimizer | 13482 | 12118 | 296 | 922 | 146 | 45 of 47 | 281 |
| XGM2 | simple | 3031 | 0 | 0 | 0 | 3031 | 24 of 47 | 9 |

- **The shared DAC pan is the main cause,** about nine in ten of the optimizer's swaps. The centred bass makes about
  900, the PSG and pre-mixes a few hundred. The simple mapper's swaps all come from the PSG.
- **Most swaps are short and loud.** Of the optimizer's swaps (names without commas), 37–43 % last 0.1 s or less and
  46–48 % up to 1 s. 11–14 % last longer. Over 90 % are at half the MOD volume or more.
- **The most swaps:** hit (1657 on XGM), k0w-rsnd (1625), musix-the-messanger (1128) and kc-dancinonamiga (1085 on
  XGM2). Lazy Cows has 449 and Highway Encounter 713 on XGM and 385 on XGM2. These are the two songs where David heard
  the swaps.
- **The optimizer leaves many instruments steadily off side,** about 290 against the simple mapper's 9. These are
  mostly recordings centred all song. They aren't swaps, but the cost step must not trade swaps for more of them
  without the rounds.

## Options considered

- **Count first, then the cost, then the rounds, each in its own PR (chosen, David).** The count shows how common the
  swaps are and which cause matters most before any cost is set.
- **Count and cost together, or all of T9.4r in one PR.** Faster, but the cost would be set before knowing what to weigh.
- **Count only recordings.** Leaves out the centred-bass hops on FM, which move the same way.
- **A silence longer than a set time (1 s, a pattern) starts a new phrase.** David chose the MOD as the judge instead:
  if the original kept the side, the change is a swap.

## Consequences

- `GenesisRenderOptions.Pans`, `ConversionOptions.PanLog`, `PanLog` and `PanSwaps` in `src/Render`; `--pan-swaps`
  in the harness. The converter never sets the log, so its output and renders are unchanged.
- Next in T9.4r: a cost for a swap in the allocator, set by a harness rule, and either/or rounds down the ladder (never →
  only short, quiet swaps → today) on the songs this count finds.
