# ADR 0137: The simple mapper plays a fitted FM patch per sample where it sounds closer, with one LFO rate for the song

- **Status:** Proposed
- **Date:** 2026-11-21

## Context

The simple mapper ([ADR 0040](0040-simple-mapper.md)) plays every looped sample with a built-in FM patch: one of five
chosen by brightness, or a patch built from four harmonics ([ADR 0118](0118-harmonic-patches.md)). The FM fitter
(plan T8.2, [ADR 0069](0069-fm-patch-fitting.md)) gets much closer on FunkyChristmas, by its log-mel score (dB):

| Sample | Closest built-in patch | Fit |
|---|---|---|
| 14 (organ) | 9.16 | 5.39 |
| 15 (organ) | 8.69 | 5.60 |
| 1 | 11.25 | 7.36 |
| 2 (lead) | 11.10 | 8.32 (with the LFO; 10.81 without) |
| 8 | 9.5 | 7.16 |
| 23 (melody) | 9.86 | 8.52 |

Only the optimizer (`--mapping optimizer`) plays fits today, and it isn't the default until plan T9.6. David wants the
songs he ships improved now (2026-11-21):
- "Yes, simple uses fits".
- `--quality best` may search for hours, `normal` for about 5 minutes, and `fast` keeps today's patches.
- A fit plays "Only if closer" than today's patch, judged by the fitter's own score.
- "Allow LFO fits too".

## Decision

- **At `--quality normal` and `best` without `--mapping optimizer`, the converter analyzes the samples and fits FM
  patches** to every sample the simple mapper plays on FM with one voice (`SimpleMapper.FmSamples`): looped tones,
  not drums, noise or chords. Samples are fitted in parallel, with the LFO, and kept in the candidate cache
  ([ADR 0074](0074-candidate-cache.md)). `best` puts no limit on renders. `normal` stops each step of a fit at
  `FittedFm.NormalRendersPerStep` renders. `fast` fits nothing and plays exactly what it did before.
- **A sample plays one patch for all its notes, and a fit only where it sounds closer.** The fitter makes a fit per
  pitch region, but a patch per region was heard as the instrument switching between notes (round 38, below). So every
  candidate (each region's fit without the LFO, and with it where that is closer) is judged with the fitter's
  evaluator on every pitch region of the sample, and the distances are averaged by the regions' notes. A fit keeps the
  score the fitter gave it on its own region. The built-in patch is judged the same way, at the pitch it plays (lifted
  for a chord's cycle, ADR 0125). The fitter's score lines the levels up before it compares, so it can't hear a patch
  that is much quieter at another pitch. So each region also adds how many dB louder or quieter the candidate sounds
  there than at the sample's busiest region, where its one level is set: a note that plays 20 dB too quiet counts
  20 dB farther. The closest candidate plays only with a smaller average than the built-in patch,
  which wins a tie. A note says which plays, the region the fit was made for, and both averages. A fit plays at the
  sample's own pitch, never lifted.
- **Fits play without SSG-EG** (`FitChoice.WithoutSsgEg`), and are judged that way: a fit that used it is scored again
  on every region, its own included. The fitter judges one note on a chip just reset, but in a song the SSG-EG
  envelope's state carries from note to note. In round 39 it left FunkyChristmas's flute (sample 8, Fit 8.1, SSG-EG
  mode 10 on its carrier) some 30 dB too quiet for five seconds; the same patch without it held within 4 dB of the
  sample.
- **Fits are levelled as the optimizer's are:** the loudest carrier at TL 0, and the heard level measured from a
  render (`SoundMenu.AtFullLevel`, `SoundMenu.Measured`), at the pitch of the sample's busiest region.
- **One LFO rate for the song** (`GenesisPlan.LfoRate`). The LFO is one setting for the whole chip. For each rate, the
  song adds up what its samples gain with a fit at that rate over their other choices (the built-in patch and the fits
  without the LFO), weighted by notes, and runs the LFO at the rate that gains most. Samples whose best fit uses another
  rate play their closest fit without the LFO, with no LFO depth, as it was judged. When no sample plays an LFO fit, the LFO
  stays off. A note names the rate.
- **The LFO reaches every output.**
  - The plan player writes `$22` with the rate, so the in-app render and the XGM2 recording use it (the XGM2 writer
    stores it as `$F9`).
  - A DMF gets `10 1y` once, on the first row. Patches carry their FMS and AMS, as before.
- **A chord sample keeps its built-in patch.** Its notes play on several voices (ADR 0133, ADR 0135), and a fit is one
  voice for the whole sound.
- **The harness converts at `fast`** (`CorpusRun.HarnessQuality`), so corpus runs keep their speed and results. Tests
  that aren't about fitting also ask for `fast`.

## Options considered

- **Chosen: one fit per sample where closer, without SSG-EG, one song LFO rate chosen by gain.** In round 41
  (2026-11-22) the fits won all 4 FunkyChristmas clips against today's patches. The flute played Fit 8.2, steady at
  6 dB under the sample (today's Organ is 12 dB under), and the song needed no LFO.
- **A fit per pitch region.** Each region gets its closest patch by the score, and this was the first version. In
  round 38 (FunkyChristmas, fitted against today's patches, 2026-11-21) the fits won 3 of 4 clips, but in the fourth
  David heard the melody (sample 23) "flip flops between an acceptable instrument and a bad instrument" and dip in
  volume, and found neither version acceptable. A sound that changes between notes is worse than a steady one a little
  further off.
- **Fits with SSG-EG, as the fitter made them.** In round 39 the switching was gone, but sample 8 (the "breathy
  flute" that takes the melody over from sample 23, as David pointed out from solo clips in round 40) played Fit 8.1 for
  all its notes, and for five seconds it was "almost inaudible", about 30 dB down. The cause was SSG-EG state carried
  between notes, which the fitter's one-note test can't see; the fits won 1 of 4 clips. Fitting without SSG-EG is the
  thorough fix and belongs to the fitter (it would also help the optimizer); stripping it in the mapper is the quick
  one.
- **Always play the fit.** It is the closest by the score, except where it isn't: the built-in patch is sometimes
  closer (FunkyChristmas sample 6's harmonic patch). David chose "Only if closer".
- **No LFO fits.** It is simpler, but sample 2's fit is 2.5 dB closer with it. David chose "Allow LFO fits too".
- **An LFO rate per sample.** The chip can't do it: register `$22` is global.
- **Leave fitting to the optimizer.** It isn't the default until plan T9.6, and David wants the simple mapper's songs
  improved now.

## Consequences

- A default conversion (`normal`) now takes minutes where it took seconds, the first time only: the cache keeps the
  fits, across builds once [ADR 0136](0136-fm-fits-across-builds.md) is merged.
- Rounds 38–41 on FunkyChristmas (fitted patches against today's, in-app XGM2 renders against the original) took the
  fitter's score from 3 of 4 wins with a "neither" to 4 of 4, once each sample kept one sound without SSG-EG. CLAP
  judges next (plan T9.4s3).
- The optimizer's fits still keep SSG-EG, and the same state problem may reach its songs.
- The flute is still about 6 dB under the sample: no FM patch reaches its level next to the rest of the song. Keeping
  the balance by turning the rest down is a separate decision.
- `docs/cli.md` (`--quality`) and `docs/channel-mapping.md` describe the fits. The goldens under `tests/golden/output/`
  show a `fast` run.
