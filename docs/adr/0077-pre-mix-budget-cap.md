# ADR 0077: Pre-mix candidates fit the song's PCM budget; the window search grows only windows

- **Status:** Accepted; supersedes the decision "No length cap" of [ADR 0072](0072-pre-mix-candidates.md)
- **Date:** 2026-11-02

## Context

The T8.6 cold corpus run ([ADR 0074](0074-candidate-cache.md#corpus)) sat on `kc-dancinonamiga: making pre-mix
candidates` for about 10 hours on one core and was stopped. Reproduced alone (`--pre-mix --no-cache`), the song spent
74.6 s per target finding windows, then rendered DMF candidates slower and slower: 3,112 of 18,161 after 14 minutes,
each 0.6–0.8 s and growing.

The cause is the song, not a loop that never ends. `kc-dancinonamiga` holds two looped notes for 2,102 ticks (about 40 s);
every other note in it lasts 54 ticks at most. A window starts where its first note does ([ADR 0072](0072-pre-mix-candidates.md)),
so every short note under one of those long notes makes a window that starts at the long note's start: a lead-in of
0–45 s of the long note alone, the overlap, and a looped tail of up to 10 s. No two are the same, so dedupe renders each
once, and on DMF and XGM2 each saves a steal, so each is offered. Since the per-tick hand-over rule of ADR 0072
(commit `e6335d3`), back-to-back notes under a long note are one window rather than a gap, which took the song from
4,920 DMF candidates to 18,161 (`k0w-rsnd`: 2,141 to 8,385).

A render costs about 0.047 s per second of head and 0.041 s per second of tail (NWaves' windowed sinc, 64 taps with a
cosine and a sine each); mixing, the driver model and scoring take milliseconds. So a song's pre-mix time follows the
**candidate audio**, the seconds of head and tail over every candidate offered:

| Song | Candidate audio, all targets |
|---|---|
| `kc-dancinonamiga` | 1,017,934 s |
| `drozerix_-_ai_renaissance` | 632,968 s |
| `k0w-rsnd` | 360,458 s |
| `drozerix_-_stardust_jam` | 229,516 s |
| `drozerix_-_neon_techno` | 210,934 s |
| the other 14 songs | 1,162–131,304 s |

`drozerix_-_ai_renaissance` took about 8 hours in the same cold run too: `kc-dancinonamiga` is the worst of several, not
the only slow song. With output kept exact, no speedup brings a million seconds of sound down to minutes, so the rule
has to change.

A second cost: the window search grew every group of up to four linked notes, gaps included, so a note with *n*
neighbours took about *n*³ steps (74.6 s per target on `kc-dancinonamiga`, 84.2 s on `k0w-rsnd`).

## Decision

(David, 2026-11-02, unless marked)

- **A pre-mix candidate bigger than the song's whole PCM budget is never offered.** Head and tail bytes together
  (`PreMixCandidate.Bytes`) over `--max-pcm-bytes` (default 128 KB, [sample-capacity.md](../sample-capacity.md)): the
  allocator could never fit it, so making it is wasted. The cap applies to **every target, DMF included**, with
  DMF's bytes counted as the pre-mixer already counts them (16-bit, 2 bytes a point). DMF has no byte budget of its
  own yet; this uses the same number.
- **Not the drivers' sample limit.** That was the first proposal: the largest sample SGDK takes as the largest window.
  XGM v1's sample id table holds each size as size / 256 in 16 bits ([xgm.txt](../formats/xgm.txt)), and XGM2's SID
  entries are addresses / 256 in 16 bits, a sample's length being the next entry's address less its own, inside a
  16-bit SLEN ([formats/xgm.md](../formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10)). Either way one sample is at most
  65,535 × 256 = **16,776,960 bytes**, about 1,190 s at XGM's 14,093 Hz and 1,260 s at XGM2's 13,317 Hz; a whole
  4 MB cartridge is about 298 s. The corpus's longest window is 46 s, so this limit drops no window and fixes nothing.
- **Sizes before rendering (implementation).** The size is worked out from the window's ticks and the tail's frames
  first. A candidate estimated more than 1 KB over the budget is not rendered; the rest are rendered and checked
  against their real bytes. So the cap is exact and over-budget windows cost nothing.
- **The window search grows only windows (implementation).** A window with three or four notes, less the note that
  starts last, is still a window. Every note of it starts by then, so after that start no note joins and the count of
  notes sounding only falls, and the two-or-more stretch stays in one piece. Every note left still overlaps another,
  because two notes from separate parts would both be sounding when the last note starts. So every window grows from
  its first two notes, adding a later note that overlaps one of them and starts before the stretch ends. On the corpus
  the windows are the same, in the same order, on all 19 songs: 74.6 s → 0.08 s per target on `kc-dancinonamiga`,
  84.2 s → 0.29 s on `k0w-rsnd`, 7.1 s → 0.17 s on `drozerix_-_ai_renaissance`.
- **Renders run side by side (implementation).** Each candidate's render depends only on its own windows, so they
  run on every core. Tail ids are then given in the same order as before, so the output is the same as one at a time.

## Corpus result

Candidate audio (heads and tails, DMF + XGM + XGM2) before and after the cap, with candidates over the 128 KB budget:

| Song | Over budget | Before | After |
|---|---|---|---|
| `10kdub` | 0 | 4,415 s | 4,415 s |
| `8bit_castle` | 317 | 4,006 s | 2,712 s |
| `cancan` | 3 | 12,566 s | 12,558 s |
| `drozerix_-_ai_renaissance` | 42,504 | 632,968 s | 369,353 s |
| `drozerix_-_neon_techno` | 12,988 | 210,934 s | 86,301 s |
| `drozerix_-_stardust_jam` | 4,364 | 229,516 s | 202,099 s |
| `drozerix_-_the_hawks_claw` | 4,087 | 131,304 s | 109,028 s |
| `fx-poly1` | 0 | 1,417 s | 1,417 s |
| `hit` | 0 | 19,668 s | 19,668 s |
| `jam_-_heatwave_cruise` | 0 | 9,736 s | 9,736 s |
| `jam_-_latenightcoffee` | 0 | 13,040 s | 13,040 s |
| `jam_-_xlr8` | 0 | 8,293 s | 8,293 s |
| `k0w-rsnd` | 15,229 | 360,458 s | 598 s |
| `kc-dancinonamiga` | 31,513 | 1,017,934 s | 6,123 s |
| `musix-cute-october` | 187 | 15,412 s | 14,828 s |
| `musix-omg-plz-help` | 199 | 24,869 s | 24,328 s |
| `musix-rubbish` | 1,973 | 29,767 s | 9,330 s |
| `sharpojects_cantpleaseeveryone` | 3,093 | 51,006 s | 17,150 s |
| `soundtracker15` | 0 | 1,162 s | 1,162 s |

12 of 19 songs change: their `pre-mix.csv` loses the over-budget rows, and the rest of each changed song's tail ids are
renumbered without them. `voice-steals.csv` changes for those songs where a dropped candidate had removed a steal. The other
7 songs' outputs are unchanged.

Checked against `main` on 13 songs (`--pre-mix --no-cache --no-furnace --no-deflemask -j 8`): the 7 unchanged songs' `pre-mix.csv`
and `voice-steals.csv` match row for row. Each of the 6 changed songs' `pre-mix.csv` is `main`'s with exactly its rows over
128 KB taken out, the same rows in the same order, apart from tail ids. Their steals with pre-mixes move on 3 songs, all
on DMF: `8bit_castle` 500 → 468, `musix-rubbish` 95 → 118, `sharpojects_cantpleaseeveryone` 834 → 916 (the count is greedy, so fewer
candidates can count fewer steals).

Times on 8 cores, same options, without the cache:

| Run | Before | After |
|---|---|---|
| `kc-dancinonamiga` alone, pre-mix | stopped after 14 min with 3,112 of 18,161 DMF renders done (about 10 h in the cold run) | 86.5 s (DMF 34.7 s, XGM 25.4 s, XGM2 26.4 s); 1 min 50 s for the whole run |
| `drozerix_-_ai_renaissance` alone, pre-mix | about 8 h in the cold run (4 songs at once) | 45 min (DMF 993 s, XGM 314 s, XGM2 1,375 s) |
| 13 songs above, `-j 8`, whole run | 47.7 min | 20.3 min |

`drozerix_-_ai_renaissance` is still the slowest song: 369 k s of candidates within budget, many of them a few seconds
long and none repeated. No other rule is added for it here.

## Options considered

- **The song's PCM budget** (chosen). It is the most PCM a song can use, and the allocator (M9) never goes past it.
- **The drivers' sample limit** (David's first proposal). It is the hard limit of the formats, but about 20 minutes long,
  so it drops nothing.
- **A lead-in cap** (the second note within 10 s of the first, as for tails): `kc-dancinonamiga` to 116 k s, and it
  changes 5 songs. It leaves `drozerix_-_ai_renaissance`, whose cost isn't lead-ins, at 632 k s, and its 10 s is
  picked by hand.
- **No rule, exact speedups only** (a bit-exact copy of NWaves' resampler with cached weights): the output stays the same,
  but about 10¹² multiply-adds remain for `kc-dancinonamiga`, an hour a target or so.

## Consequences

- `PreMixer.Fit` and `FitSong` take `maxPcmBytes` (default `ConversionDefaults.MaxPcmBytes`), and the candidate cache's
  pre-mix key includes it. The harness uses the default, as its conversions do.
- When M9 makes the budget per target or adds one for DMF, the cap follows it.
- `PreMixSongFit.Renders` counts the windows rendered: the over-budget ones that were never rendered aren't in it.
