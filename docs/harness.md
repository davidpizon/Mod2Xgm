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
   - Emulator captures of XGM/XGM2 ROMs join here when those writers exist (plans M10/M11).
5. **Scores.** Each rendering is resampled to the scoring rate if needed, mixed to mono and compared with the reference
   by `SongScorer` ([ADR 0042](adr/0042-scoring-metrics.md)): log-mel distance (dB), MFCC distance, note-start
   precision/recall/F1 and timing, alignment lag, length difference and the combined number. **Lower is closer**,
   except F1.

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
its timeline is within one tick, **1** a song failed or is more than a tick off, **2** the harness couldn't run
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
| `-j`, `--jobs` | half the processors, at most 4 | Songs measured at once (1–64). The reports list songs in the same order either way |
| `-v`, `--verbose` | | Log every scoring step |

## Output

In the output folder:

| File | What it holds |
|---|---|
| `report.md` | Scores per song and renderer, the average per renderer, and the timeline table |
| `scores.csv` | `song,renderer,combined,mel_db,mfcc,onset_precision,onset_recall,onset_f1,onset_timing_ms,lag_ms,length_difference_s,remark` |
| `clip-scores.csv` | With `--clip`: the same columns as `scores.csv`, for the first seconds only |
| `durations.csv` | `song,tick_player_s,openmpt_rules_s,reference_s,difference_s,amiga_difference_s,one_tick_s,within_one_tick,first_row_off,stops_at_f00,conversion_notes,error` |
| `dmf/`, `wav/` | The conversions and the renders |
| `logs/` | Each conversion's console output (`song.log`) and its rows' times (`song.rows.csv`: `order,row,tick_player_s,openmpt_rules_s,reference_s,difference_s`) |

Nothing in the reports depends on the time or the machine, so two runs of the same converter give the same files
(plan T6.3: *stable scores on reruns*). Reports stay in `artifacts/`. The simple mapper's baseline, which later changes
are measured against, is committed in [tests/golden/harness-baseline/](../tests/golden/harness-baseline/README.md)
([ADR 0049](adr/0049-simple-mapper-baseline.md)).

## Tests

`tests/Mod2Xgm.Harness.Tests` covers the WAV reader and writer, the report, the length check, the command line and a
whole run with stand-in renderers. These run in CI. The tests that need libopenmpt or Furnace skip themselves where
those aren't installed, as on the CI runners. Coverage is measured on the converter (`Mod2Xgm.Tests`) only.
