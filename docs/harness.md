# Accuracy harness

`tools/Mod2Xgm.Harness` measures how close a conversion sounds to the original MOD (plan T6.3,
[ADR 0047](adr/0047-accuracy-harness.md)). It is how "most accurate sounding" is checked rather than guessed
([testing.md](testing.md#accuracy-harness-drives-design-decisions)). It runs on a developer PC only. It may load
libopenmpt and start Furnace, which the converter itself never does (AGENTS.md, hard constraint 2).

## What it does

For each MOD (the corpus in `tests/data/corpus/` by default):

1. **Reference.** It renders the MOD once through with **libopenmpt** (OpenMPT's engine, default settings, the same
   sound as `openmpt123 --render`), straight into memory, and asks libopenmpt how long the song is.
2. **Song timeline.** It plays the MOD with the converter's tick player (`ModTickPlayer`, PAL) and checks it against
   libopenmpt, row by row ([below](#song-timeline)).
3. **Conversion.** It converts the MOD in-process with the default options, exactly as `mod2xgm song.mod` does, to
   `dmf/song.dmf`. The whole console output goes to `logs/song.log`.
4. **Renderings of the conversion:**
   - **furnace**: Furnace's command line renders the fresh DMF to `wav/song.furnace.wav`. This is automatic, so every
     run gets one.
   - **deflemask**: DefleMask has no command line, so its WAV exports are made by hand (File → Save WAV, loop count 0)
     and picked up as `song.wav` from a folder. When that folder's sibling `dmf/` holds the DMF the export was made
     from (the [corpus-check](../tests/golden/corpus-check/README.md) layout), the harness compares it with the fresh
     conversion. The report then says *stale* if the converter has changed since the export.
   - **mod-renderer**: the converter's own MOD renderer (plan T7.1) plays the *original* MOD, not the conversion, as a
     check that the optimizer's reference sounds like libopenmpt ([below](#in-app-mod-renderer)).
   - **genesis**: the converter's own Genesis renderer (plan T7.5, [ADR 0059](adr/0059-genesis-renderer.md)) plays the
     conversion's plan on the chip models with the DMF's DAC, in OpenMPT's tick lengths, so it is scored without
     alignment like `mod-renderer`. The log gives each song's render time (`genesis rendered 70.4 s in 2.05 s (34x real
     time)`); the report leaves it out, so reports don't depend on the machine.
   - Emulator captures of XGM/XGM2 ROMs join here when those writers exist (plans M10/M11).
5. **Scores.** Each rendering is resampled to the scoring rate if needed, mixed to mono and compared with the reference
   by `SongScorer` ([ADR 0042](adr/0042-scoring-metrics.md)): log-mel distance (dB), MFCC distance, note-start
   precision/recall/F1 and timing, alignment lag, length difference and the combined number. **Lower is closer**,
   except F1.

6. **Loudness:** each whole rendering, and each clip, on an EBU R128 meter (R128Net): integrated loudness, loudness
   range and true peak, next to the original's ([below](#loudness)).
7. **Instrument balance** (with `--balance`): each MOD sample alone in the clip, from both sides, and how far its
   loudness is from the original's ([below](#instrument-balance)).

A song that fails (an unreadable MOD, a Furnace error) is reported and the run goes on.

## Song timeline

Plan T3.4 asks for the tick player's song length to be within ±1 tick of OpenMPT's. The tick player copies ProTracker 2
on a real Amiga (pt2-clone, [ADR 0033](adr/0033-tick-player.md)), and OpenMPT times ticks differently in two ways:

| | Real Amiga (tick player) | OpenMPT |
|---|---|---|
| Tick length | The CIA timer: 709 379 / (⌊1 773 447 / BPM⌋ + 1) Hz | A whole number of samples at 48 kHz: ⌊48 000 × 2.5 / BPM⌋. At BPM 125 both are 20 ms. At most other tempos OpenMPT runs a little fast (0.06 % at BPM 128) |
| Tempo change (`Fxx`, 32 and up) | Takes effect one tick later: the CIA timer only takes the new value at its next interrupt | Applies to the tick that reads it |
| `F00` | Stops the song | Ignored; the song plays on |

So the harness times the tick player's own ticks OpenMPT's way (`OpenMptTiming`) and asks libopenmpt when it reaches
each row (`openmpt_module_set_position_order_row`). A song passes when **every row** starts within one tick of
libopenmpt's time for it **and** the song ends within one tick of libopenmpt's length. A song that stops at `F00` is
compared up to the stop, since OpenMPT plays past it. One tick is the song's longest. Comparing every row catches a
wrong jump or loop even when the total length happens to come out right.

The report also shows **Amiga − OpenMPT**: how much the real Amiga's timing changes the length. It is not an error. The
converter follows the Amiga. Every row's three times are in `logs/song.rows.csv`.

On the corpus (2026-10-29) every song's timeline matches libopenmpt's to the microsecond. `musix-omg-plz-help` stops at
`F00`. A real Amiga's timing differs from OpenMPT's by up to 0.21 s (`drozerix_-_stardust_jam`, 6 minutes at 101–128
BPM).

## In-app MOD renderer

The optimizer will score its candidates against the converter's own rendering of the MOD (`src/Render/ModRenderer.cs`,
[ADR 0054](adr/0054-mod-renderer.md)), so that rendering has to sound like the reference. The `mod-renderer` column
checks it: the tick player plays the MOD (PAL), the renderer mixes it at the scoring rate, and the result is scored like
any rendering. **The bar is 1.25 dB of whole-song log-mel distance on every song;** a song over it fails the run.

Two things make the check measure the sound and not the clock:

- **OpenMPT's tick lengths.** The renderer gets ⌊rate × 2.5 / BPM⌋ samples per tick at the render rate, and a tempo
  change starts **one tick late**, as on an Amiga. That is how libopenmpt's audio is timed, and it is not how its song
  time is timed ([Song timeline](#song-timeline) uses the tick that read the change). A song that sets its tempo on its
  first row therefore starts with one tick at 125 BPM in libopenmpt's audio: 266 samples at 44.1 kHz for
  `musix-omg-plz-help` (96 BPM), −193 for `musix-cute-october` (160 BPM). With the song-time rule those songs scored up
  to 1.59 dB; with the audio rule 0.02–0.14 dB.
- **No alignment.** The rendering already keeps libopenmpt's time, so it is scored with alignment off. On a song that
  repeats, the alignment can line a bar up with the wrong repeat (`musix-cute-october`: 743 ms off at 22.05 kHz,
  4.33 dB), and it moves in steps of 256 samples, which hid the first-tick offset above.

On the corpus (2026-10-31, after the tick player's pitch and loops moved to OpenMPT's,
[ADR 0060](adr/0060-openmpt-pitch-and-loops.md), and 1-word samples are read as OpenMPT reads them,
[ADR 0062](adr/0062-one-word-samples.md)), worst song per rate:

| Rate | Worst song | Log-mel (dB) | Before ADR 0060 |
|---|---|---|---|
| 44.1 kHz | `soundtracker15` | 0.41 | 0.85 (`drozerix_-_ai_renaissance`) |
| 22.05 kHz | `soundtracker15` | 0.62 | 0.76 (`soundtracker15`) |
| 8 kHz | `10kdub` | 0.85 | 0.94 (`fx-poly1`) |

The mean at 44.1 kHz went from 0.30 to 0.08 dB. The 1–3 ms offset that wandered through
`sharpojects_cantpleaseeveryone` was finetune (a long sample 2 cents fast); it now matches libopenmpt sample for sample.
The constant 0.11–0.25 ms lead in `jam_-_heatwave_cruise` and `jam_-_latenightcoffee` was OpenMPT's loader skipping
the bytes of their 1-word samples (ADR 0062); both now match sample for sample too (0.10 and 0.06 dB).

## Instrument balance

A whole-mix score hardly sees one instrument too loud and another too quiet. With `--balance` (and `--clip`) the harness
makes **stems**, each MOD sample alone, and compares their loudness ([ADR 0051](adr/0051-instrument-balance-metric.md)):

- **Original:** a copy of the MOD with every other sample's data zeroed, rendered by libopenmpt. Patterns and effects
  are untouched.
- **Conversion:** the converter's own plan with every other sample's notes turned into note-offs, cut after the orders
  the clip reaches, written to `dmf/stems/song.sNN.dmf` and rendered by Furnace. The harness first checks that its copy
  of the conversion steps gives exactly the converter's DMF.
- **Measure** (`BalanceMetrics`): each stem's K-weighted loudness every 100 ms (400 ms windows), the candidate's moved
  by the clip's lag and corrected by the overall gain between the mixes. The **balance distance** is the mean absolute
  level difference over every audible instrument and moment, in dB. Each instrument's mean offset (+ = too loud in the
  conversion) goes to `balance-instruments.csv`.
- **Check:** the stems' loudness, added up, should match the mix's (`*_sum_error_db`, near 0). Furnace's stems match in
  loudness but not sample by sample, so the check compares levels.

A balance run renders each sample once more with each player: on the corpus it takes a few minutes with `-j 4`.

Each stem's offset is also measured a second way, by gated EBU R128 loudness (`r128_offset_lu`, below): the stem's
loudness next to its mix in the conversion, minus the same in the original. Gating ignores the moments an instrument is
silent, so it reads how loud the instrument is *when it plays* ([ADR 0053](adr/0053-r128-loudness-in-the-harness.md)).

## Loudness

Every rendering, and the original, goes through an ITU-R BS.1770 / EBU R128 loudness meter (R128Net, a C# port of
libebur128; [ADR 0053](adr/0053-r128-loudness-in-the-harness.md)), in stereo:

- **Integrated loudness** (LUFS): what a loudness meter shows for the whole song, gated so silences and fades don't count.
  *Δ LU* is the conversion minus the original (+ = louder).
- **Loudness range** (LRA, LU): how far the song's loud parts are from its quiet ones. A smaller range than the original's
  means the conversion flattened the dynamics.
- **True peak** (dBTP, 4× oversampled): above 0 the render clips.

Whole songs are in the report and `loudness.csv`; with `--clip`, the clips get a line of their own in `loudness.csv`.

`BalanceMetrics` (the balance distance) keeps its own K-weighting in the converter's `src/Scoring`. A test checks it against
R128Net: it reads up to 0.3 dB low, for the reasons in ADR 0053.

## Setup

- **libopenmpt** 0.8 or later, from [lib.openmpt.org](https://lib.openmpt.org/libopenmpt/download/):
  - **Windows:** the "libopenmpt" package (the zip with `bin\amd64\libopenmpt.dll`). Unpacked to
    `C:\Program Files\libopenmpt` it is found without any setup. Otherwise, put the folder for your CPU (`bin\amd64`
    on 64-bit Intel/AMD) on PATH, or pass `--libopenmpt <folder>`. The `x86` folder is for 32-bit programs and
    doesn't load.
  - **Linux:** `apt install libopenmpt0`. **macOS:** `brew install libopenmpt`.
- **Furnace** (optional): on PATH, or in its Windows install folder (`C:\Program Files\Furnace`), or `--furnace <path>`.
- **DefleMask exports** (optional): `tests/golden/corpus-check/out/wav/` is used when it exists; how to make them is in
  [its README](../tests/golden/corpus-check/README.md#remaking-the-files).

## Running

From the repository root:

```
dotnet run --project tools/Mod2Xgm.Harness -c Release
```

Use `-c Release`: the scoring is several times slower in a Debug build. Exit code: **0** every song was measured and
its timeline is within one tick and the in-app renderer within its bar, **1** a song failed, is more than a tick off or
is over the renderer's bar, **2** the harness couldn't run
(libopenmpt missing, bad options).

## Options

| Option | Default | What it does |
|---|---|---|
| `inputs…` | `tests/data/corpus` | MOD files or folders of them |
| `-o`, `--output` | `artifacts/harness` | Folder for the report, DMFs, logs and renders (git-ignored) |
| `--rate` | 44100 | Rate to render and score at, in Hz (8000–192000) |
| `--libopenmpt` | search path, then `Program Files\libopenmpt` | Folder holding libopenmpt |
| `--furnace` | PATH, then `Program Files\Furnace` | Path to Furnace |
| `--no-furnace` | | Don't render with Furnace |
| `--deflemask-wavs` | `tests/golden/corpus-check/out/wav` when it exists | Folder of DefleMask exports named `song.wav` |
| `--no-deflemask` | | Don't score DefleMask exports, even from the default folder |
| `--save-reference-wavs` | | Also write the libopenmpt renders to `wav/song.reference.wav`, for listening |
| `--clip <seconds>` | | Also score each song's first *seconds* (1–600) on their own, write them to `clip-scores.csv` and a report section, and save them for listening as `wav/song.reference.clip.wav` and `wav/song.<renderer>.clip.wav` |
| `--balance` | | With `--clip`: also measure each clip's [instrument balance](#instrument-balance) from stems, for Furnace, into `balance.csv`, `balance-instruments.csv` and a report section |
| `--fm-fit` | | Also fit FM patches to every sample the conversion analyzed, every pitch region, with both structure searches ([ADR 0069](adr/0069-fm-patch-fitting.md)), into `fm-fit.csv`; a song's samples are fitted at once. Slow: minutes per region without a limit |
| `--fm-max-renders <n>` | no limit | With `--fm-fit`: most notes each step of a region's fit renders (the structure search, and each level fit after it) |
| `--psg-fit` | | Also make the PSG candidates of every sample the conversion analyzed, every pitch region ([ADR 0070](adr/0070-psg-candidates.md)), into `psg-fit.csv`; about a second per region |
| `--pcm-fit` | | Also make the PCM candidates of every sample the conversion analyzed, for DMF, XGM and XGM2 ([ADR 0071](adr/0071-pcm-candidates.md)), into `pcm-fit.csv`; about 3 minutes for the corpus |
| `--pre-mix` | | Also make the pre-mixed PCM candidates of every song for DMF, XGM and XGM2, and count its voice steals with and without them ([ADR 0072](adr/0072-pre-mix-candidates.md)), into `pre-mix.csv` and `voice-steals.csv` |
| `-j`, `--jobs` | half the processors, at most 4 | Songs measured at once (1–64). The reports list songs in the same order either way |
| `-v`, `--verbose` | | Log every scoring step |

## Output

In the output folder:

| File | What it holds |
|---|---|
| `report.md` | Scores per song and renderer, the average per renderer, the in-app renderer against its bar, and the timeline table |
| `scores.csv` | `song,renderer,combined,mel_db,mfcc,onset_precision,onset_recall,onset_f1,onset_timing_ms,lag_ms,length_difference_s,remark` |
| `clip-scores.csv` | With `--clip`: the same columns as `scores.csv`, for the first seconds only |
| `balance.csv` | With `--balance`: `song,renderer,balance_db,static_db,mix_relative_db,gain_db,stems,reference_sum_error_db,candidate_sum_error_db` |
| `balance-instruments.csv` | With `--balance`: `song,renderer,sample,name,voices,offset_db,distance_db,frames,reference_level_db,r128_offset_lu`, one line per stem |
| `loudness.csv` | `song,renderer,span,reference_lufs,candidate_lufs,difference_lu,reference_lra_lu,candidate_lra_lu,reference_true_peak_dbtp,candidate_true_peak_dbtp`; `span` is `song`, or `clip` with `--clip` |
| `sample-analysis.csv` | `song,sample,name,kind,percussive,tonal,noisy,period_frames,voiced_share,deviation_cents,drift_cents,peak_db,attack_ms,decay_ms,sustain_db,loop_frames,loop_cycles,regions,region_rates_hz,fundamentals_hz,notes`, one line per sample with sound ([ADR 0068](adr/0068-sample-analysis.md)); the last three list each pitch region, separated by spaces |
| `fm-fit.csv` | With `--fm-fit`: `song,sample,name,region,library,library_db,exhaustive_db,genetic_db,best,best_db,best_mfcc,best_harmonic_db,no_lfo_db,lfo_rate,patch,fitted_beats_library,renders,seconds`, one line per pitch region ([ADR 0069](adr/0069-fm-patch-fitting.md)): the closest built-in patch, each search's fit, the best and its twin without the LFO (log-mel distances in dB), the best's settings, the notes rendered and the time taken |
| `psg-fit.csv` | With `--psg-fit`: `song,sample,name,region,most_at_once,sound,noise_rate,period,pitch_hz,cents_off,channels,room,mel_db,mfcc,harmonic_db,best,best_with_room`, one line per PSG try ([ADR 0070](adr/0070-psg-candidates.md)): the most channels the song sounds the sample on at once, the try's sound (`tone`, `white`, `periodic`), noise rate and period, the pitch it plays and how far off the note, the channels it takes and whether the song has room for it, its distances, and whether it is the region's closest and its closest with room |
| `pcm-fit.csv` | With `--pcm-fit`: `song,sample,name,target,most_at_once,kind,savings,room,copies,bytes,notes,mel_db,harmonic_db,smallest,start_frame,seconds,plain_mel_db,bent_notes` ([ADR 0071](adr/0071-pcm-candidates.md)). Kind `copies`: one line per sample, target and combination of savings (`none`, or e.g. `half speed+short loops`), with whether the sample alone fits the target's caps, its copies, bytes and notes, its note-weighted distances, whether it is the sample's smallest, and the sample's bent notes. Kind `baked`: one line per baked note, with the notes that share it, its bytes, distance, start frame and length, and the plain copy's distance |
| `pre-mix.csv` | With `--pre-mix`: `song,target,samples,offsets_ticks,occurrences,saves_steal,window_s,switch_s,end,head_bytes,tail_bytes,slots,tail_sample,tail_start_frame,tail_id,clip_db,mel_db,seam_mel_db`, one line per pre-mix candidate ([ADR 0072](adr/0072-pre-mix-candidates.md)): its samples and their start ticks in the window (joined by `+`), how often the song plays it and whether it saves a voice steal, the window and where the tail starts, how the outlasting note goes on (`none`, `tail`, `baked`), its bytes and slots, the tail's sample, start frame and id (tails with the same id share bytes), how far the mix was turned down so it doesn't clip, and the log-mel distances of the whole and around the switch |
| `voice-steals.csv` | With `--pre-mix`: `song,target,voices,pcm_notes,overlaps,candidates,steals,steals_with_pre_mix,steals_removed,seconds`, one line per song and target: notes that start while every PCM voice is busy, without and with the pre-mixes |
| `durations.csv` | `song,tick_player_s,openmpt_rules_s,reference_s,difference_s,amiga_difference_s,one_tick_s,within_one_tick,first_row_off,stops_at_f00,conversion_notes,error` |
| `dmf/`, `wav/` | The conversions and the renders; `dmf/stems/` holds the stem DMFs (their renders are deleted) |
| `logs/` | Each conversion's console output (`song.log`) and its rows' times (`song.rows.csv`: `order,row,tick_player_s,openmpt_rules_s,reference_s,difference_s`) |

Nothing in the reports depends on the time or the machine, so two runs of the same converter give the same files
(plan T6.3: *stable scores on reruns*). The one exception is `fm-fit.csv`'s `seconds` column, asked for to judge the
fit's cost (David, [ADR 0069](adr/0069-fm-patch-fitting.md)); every other column of it repeats exactly. Reports stay in `artifacts/`. The simple mapper's baseline, which later changes
are measured against, is committed in [tests/golden/harness-baseline/](../tests/golden/harness-baseline/README.md)
([ADR 0049](adr/0049-simple-mapper-baseline.md)).

## Tests

`tests/Mod2Xgm.Harness.Tests` covers the WAV reader and writer, the report, the length check, the command line, the
stems, the loudness meter (with BS.1770's calibration tone) and a whole run with stand-in renderers. These run in CI. The tests that need libopenmpt or Furnace skip themselves where
those aren't installed, as on the CI runners. Coverage is measured on the converter (`Mod2Xgm.Tests`) only.
