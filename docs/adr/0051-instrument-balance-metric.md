# ADR 0051: Instrument balance metric

- **Status:** Accepted
- **Date:** 2026-10-30

## Context

The combined score is the whole-mix log-mel distance ([ADR 0042](0042-scoring-metrics.md), onset weight 0 since
[ADR 0049](0049-simple-mapper-baseline.md)). On the corpus it agrees only weakly with David's listening ratings of
30-second clips (`tests/golden/harness-baseline/listening.tsv`): Spearman ρ 0.35, and below 0.2 for every other whole-mix
measure tried (level, gain-matched log-mel, band balance, loudness envelope). David hears **FM channel volumes far off
from the MOD's**. The converter (as baselined) sets an FM note's volume from the MOD volume column only, not from how
loud the sample is next to the FM patch, so one instrument can end up 10 dB too loud or too quiet. A whole-mix spectrum
hardly sees that: the same energy is still there, in a different instrument.

The optimizer (plan M9) will choose between candidates by this score, so the score has to follow the ears. This ADR
designs a measure of **per-instrument loudness balance** and checks it against the same ratings.

## Decision

### Stems: each MOD sample alone, from both sides (David chose this approach)

For every sample the clip plays, the harness renders two **stems**: the MOD's sound of that sample alone, and the
conversion's sound of the notes that came from that sample alone. Everything else about the song stays as it is.

- **MOD side:** a copy of the MOD with **every other sample's PCM data zeroed**. The patterns, effects, volumes and
  timing are untouched, so the sample sounds exactly as in the full song and the rest is silence. libopenmpt renders it.
  The reader now records where each sample's data sits in the file (`ModSample.DataOffset`).
- **Conversion side:** the harness runs the converter's steps in-process and checks that they give exactly the DMF
  the conversion wrote. It then takes the converter's **plan** (`GenesisPlan`) and turns every note whose source sample
  (`PlanCell.Sample`) is another sample into a note-off. The mapping decisions (routing, clashes, dropped notes, FM patch,
  DAC copies) and the timing are the full song's, so the stem is exactly what that sample became in the real conversion.
  Furnace renders it.
- **Only the clip is rendered.** The plan is cut after the last order the clip (plus the 1 s alignment search) reaches,
  from the row times, so Furnace renders seconds: a full 6-minute song takes Furnace 40 s, and a song has 3–14 samples
  in its first 30 s.
- **Check:** the stems' loudness, added up, should match the mix's. It compares levels, not waveforms: Furnace's
  renders match the mix in power but not sample by sample (its phases differ from run to run). On the corpus the sums
  are within 0.03–0.35 dB of the mix for libopenmpt and 0.07–1.32 dB for Furnace, so the stems are what they claim.

### The measure: balance distance (`src/Scoring/BalanceMetrics.cs`)

Per stem and per moment, compare the two loudness levels after taking out the overall gain difference:

1. **Level:** K-weighted (ITU-R BS.1770) power in 400 ms windows every 100 ms, in dB, for each stem and each mix. The
   candidate's stems and mix are moved by the clip's alignment lag.
2. **Overall gain** g = the candidate mix's mean power level − the reference mix's, in dB. The ears adjust to the
   overall level; the balance is what is left. The candidate's levels are corrected by g.
3. **Audible floor** T = the reference mix's mean level − 30 dB. A stem frame counts when either side is above T; both
   sides are clamped at T, so a missing or added instrument costs at most about 30 dB.
4. **Per frame:** d_i(t) = max(L_cand,i(t) − g, T) − max(L_ref,i(t), T).
5. **Balance distance** B = the mean of |d_i(t)| over every counted (instrument, frame) pair, in dB. 0 = every
   instrument at the right level at every moment. This was the primary number, fixed before looking at the ratings.
6. **Per instrument:** the mean signed offset o_i = mean d_i(t) (+ = too loud in the conversion), in
   `balance-instruments.csv` with what the conversion plays it with (FM, DAC, noise).

Two variants are reported alongside: the static part only (mean |o_i|, weighted by frames), and levels relative to
each side's own mix at each moment instead of the global gain.

The harness's new `--balance` option (with `--clip`) writes `balance.csv`, `balance-instruments.csv` and a report section
([harness.md](../harness.md#instrument-balance)).

### Agreement with listening: none

Harness run on the corpus (2026-10-30; converter as baselined in ADR 0049, i.e. `feat/t6-4-baseline`; Furnace; 30 s
clips; the clip log-mel scores came out identical to the committed baseline). Spearman ρ and Kendall τ between −measure
and David's ratings, 19 songs, positive = agrees with the ears:

| Measure | ρ | p | τ |
|---|---:|---:|---:|
| Log-mel distance (today's combined score) | +0.35 | 0.14 | +0.32 |
| **Balance distance** (primary) | **+0.02** | 0.93 | 0.00 |
| Static balance | −0.04 | 0.86 | −0.01 |
| Mix-relative balance | +0.03 | 0.92 | 0.00 |
| \|Overall gain\| | −0.03 | 0.90 | −0.03 |
| Log-mel + w × balance, w = 0.25 / 0.5 / 1 / 2 / 4 | +0.28 / +0.23 / +0.24 / +0.20 / +0.10 | | |

**The balance distance does not predict which songs David rates higher**, and adding it to log-mel makes the agreement
worse at every weight tried. Examples: `drozerix_-_neon_techno` has the best balance (1.6 dB) and the lowest rating (1).
`soundtracker15` has the worst log-mel score and the best rating (7), with a middling balance (3.5 dB).

**But it does see the problem David described.** Per instrument (134 audible stems), the conversion's levels are far
off, most of all on FM:

| Played with | Stems | Mean offset dB | Mean \|offset\| dB | Range dB |
|---|---:|---:|---:|---:|
| FM | 57 | +1.1 | 4.4 | −12.3 … +13.8 |
| FM + DAC | 39 | +1.7 | 4.1 | −5.6 … +16.2 |
| DAC | 25 | −2.2 | 3.9 | −11.5 … +7.0 |
| DAC + noise, noise | 13 | −2.6 | 3.3 | −7.4 … +2.3 |

The largest are FM instruments 10–14 dB too loud or too quiet (`k0w-rsnd` sample 1 +13.8 dB, `10kdub` sample 5
−12.3 dB, `8bit_castle` sample 1 −10.9 dB). Some are drum samples that loop and so went to FM (`jam_-_xlr8`'s
`BassDrum2` and `HiHat1`, +10 dB).

So the balance errors are real and large, but across songs the ratings are driven by something else as well: wrong
timbre (placeholder FM patches, drums on FM), pitch and missing parts, which differ from song to song more than the
balance does. A ranking across 19 different songs can't separate those. A **paired** test can: the same songs with
only the levels changed. The FM-loudness fix ([ADR 0050](0050-voice-levels.md), now merged) and David's A/B listening of old against
new levels give exactly that.

### What follows for the score

- **The combined score is unchanged** (log-mel alone, ADR 0049). The balance distance isn't folded in: on these ratings
  it would make the score agree less with listening.
- **The balance distance is reported** by `--balance` as a diagnostic, and its per-instrument offsets are the
  measurement for FM-loudness work: a fix should pull the offsets toward 0.
- **Proposal, once the A/B result is in:** run `--balance` on the old and the new FM levels. If the songs where David
  prefers the new levels are the ones whose balance distance drops, the measure follows the ears within a song, and it
  can join the combined score as a second term, `log-mel + w × balance` with w chosen on that A/B data (David's call).
  If it doesn't, the measure stays a diagnostic.

## Options considered

- **Per-sample stems by silencing, from the real conversion's plan** (chosen, David). Exact on the MOD side and
  faithful to the conversion's real decisions on the other. It measures what David hears: one instrument too loud or too
  quiet. It costs one libopenmpt and one Furnace render per sample per song, kept small by rendering the clip only (the
  corpus run with `--balance` and `-j 4` took about 7 minutes).
- **Per-sample stems by removing notes on both sides** (the first idea). The same in spirit, but deleting MOD notes
  changes what later effects and sample-less cells act on. Zeroing the data is simpler and exact.
- **Re-converting a soloed MOD** (e.g. `--ignore-samples` for all the others): the sample would win clashes it lost in
  the real conversion, and dropped notes would come back.
- **Per-channel stems**: libopenmpt mutes MOD channels (its `ext` interactive interface) and Furnace writes one WAV per
  Genesis channel (`-outmode perchan`) in one render. Cheapest, but the channels don't correspond: the DAC takes notes
  from every MOD channel and clash losers move, so a channel's level difference mixes several instruments.
- **Estimated levels without stems**: compare each sample's RMS × volume with the FM patch's level from a model. It is
  fast enough for the optimizer, but it needs the in-app YM2612 renderer (M7) and only models what the stems measure.
  Worth revisiting in M9 if the stems show the measure follows the ears.
- **Sample-by-sample residual as the stem check**: Furnace's stems never cancel the mix (+2.6 dB residual on `10kdub`
  while their power matches to 0.02 dB), so the check compares loudness.

## Consequences

- `src/Scoring/BalanceMetrics.cs` (levels, K-weighting, balance distance, the sum check) with tests;
  `ModSample.DataOffset`. Coverage stays at 95.9 %.
- `tools/Mod2Xgm.Harness`: `--balance`, `StemSource` (`Stems.cs`), `IDmfRenderer` (Furnace), `balance.csv`,
  `balance-instruments.csv`, stem DMFs in `dmf/stems/`. [harness.md](../harness.md), [testing.md](../testing.md) and
  [architecture.md](../architecture.md) describe it.
- No change to the combined score or the baseline.
- Follow-up: the paired check above, on the FM-loudness fix with David's A/B ratings.
- With 19 ratings a ρ difference of 0.1–0.2 is within noise; the gap here (0.02 against 0.35) is not large enough to say
  the balance is worse than log-mel, only that it adds nothing across songs.
