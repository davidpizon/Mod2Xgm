# ADR 0076: Loosened locked pairs: half of each track together, at least eight times, lone notes on their own half

- **Status:** Accepted; supersedes the lock rule of [ADR 0073](0073-ch3-locked-pairs.md)
- **Date:** 2026-11-02

## Context

[ADR 0073](0073-ch3-locked-pairs.md) made a locked pair two tracks (a sample on a channel) whose *every* note starts with
one of the other's, at one gap. On the corpus that found two pairs, both in `jam_-_latenightcoffee`, each heard twice:
coincidences, not layered parts. It missed the clear layers. In `hit`, sample 9 is doubled on channels 1 and 2, starting
together 518 times, but each copy also plays some notes alone. In `k0w-rsnd`, every note of sample 1 comes with sample 2,
which also plays 182 notes alone.

CH3 special mode keys each half of channel 3 on its own (`$28` bits 4–7). So a note one track plays alone can still play
on its own half, and one channel covers both tracks either way.

David listened to an A/B of k0w-rsnd's pair, with both pitch fixes in ([PR #78](https://github.com/davidpizon/Mod2Xgm/pull/78),
[ADR 0075](0075-fm-pitch-guard.md)). Strict meant each track on its own four-operator channel; loosened meant two
two-operator halves of CH3. He found the loosened one closer. The scores agree on tone (log-mel 4.66 against 5.61 dB, MFCC
26.6 against 39.8), though its onsets match less well (F1 0.40 against 0.55). For `hit`'s doubling the strict version
scored 3 dB closer.

## Decision

(David, 2026-11-02.)

- **A pair starts together on at least half of each track's notes**, at one gap. The gap is the largest group of the
  together onsets within 10 cents of each other, so the times the tracks meet at another interval don't count.
- **At least eight onsets together** (it was two). Fewer is chance, and a part that short barely needs its own channel.
- **Lone notes play on their own half**, keyed alone; the other half stays silent. A candidate is still fitted and
  judged on the onsets where both play. A lone note sounds as its voice does on its own, which is what the voice's own
  score against its sample measures.
- `LockedPair` carries each track's note count, so `LoneA`, `LoneB` and `Strict` (neither plays alone) can be read off
  it. The `--verbose` log and `locked-pairs.csv` report the lone notes.
- A track may now be in more than one pair. The allocator (M9) picks at most one pair per track and one CH3 per song.

## Corpus result

Pair detection over the full corpus (19 songs), 2026-11-02, finds six pairs in five songs:

| Song | Tracks | Together | Alone | Gap |
|---|---|---|---|---|
| hit | sample 9, ch 1 + ch 2 | 518 | 48 / 76 | unison |
| hit | sample 3, ch 1 + ch 2 | 368 | 260 / 6 | unison |
| k0w-rsnd | sample 1 ch 1 + sample 2 ch 3 | 320 | 0 / 182 | +1 semitone |
| fx-poly1 | sample 1, ch 2 + ch 3 | 59 | 51 / 44 | −3.97 semitones |
| musix-omg-plz-help | sample 15 ch 3 + sample 4 ch 4 | 56 | 46 / 44 | −12.22 semitones |
| cancan | sample 6 ch 1 + sample 3 ch 4 | 32 | 31 / 14 | +2.86 semitones |

The two strict pairs of ADR 0073 (two onsets each) are no longer pairs.

## Options considered

- **Half of each track, at least eight times** (chosen). Every pair it finds is a real layered part.
- **Half of either track:** what David first picked. On the corpus it found 191 pairs, mostly a rare sample (two to a few
  dozen notes) that happens to start with a busy track: hit's sample 4 on channel 2 played both its notes with sample 3
  on channel 1. That made the fitting slow and left the allocator a lot of noise.
- **Half of each track, at least twice:** about 13 pairs; the extra seven are two or three onsets each.
- **One side always paired:** finds k0w-rsnd, not hit (92 % and 87 %).
- **Locked per song section:** strict, but per section of the order list. That suits M9's per-section allocator but is
  more work; it can come with M9 if listening asks for it.
- **The strict lock** (ADR 0073): found no real part on the corpus.

## Consequences

- `LockedPairs.Find`, `LockedPair` (`NotesA`, `NotesB`, `LoneA`, `LoneB`, `Strict`) and `LockedPairReport` change;
  `locked-pairs.csv` gains `lone_a` and `lone_b`. `Ch3Fitter` is unchanged: it fits the together onsets as before.
- ADR 0073's Status names this ADR. Its fitting, scoring and output decisions stand.
- The allocator (M9) weighs a CH3 candidate's lone notes by its voices' own scores.
