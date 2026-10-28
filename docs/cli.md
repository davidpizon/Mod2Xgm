# Command-line interface

> Status: **proposed** — this is the target contract; update it when the code changes. Parsed with
> **System.CommandLine**.

```
mod2xgm <input.mod> [options]
```

| Option | Default | Meaning |
|---|---|---|
| `<input.mod>` | required | Path to the MOD file. |
| `-f, --format <dmf\|xgm\|xgm2>` | `dmf` | Output format: `dmf` → `.dmf`, `xgm` → `.xgm`, `xgm2` → `.xgm2`. |
| `-o, --output <path>` | next to input | Output file path. |
| `--input-region <pal\|ntsc>` | `pal` | Region of the Amiga the MOD was made on: sets the clock that turns periods into pitch. See [Regions](#regions). |
| `--output-region <pal\|ntsc>` | `pal` | Region of the Genesis the output is made for: its clocks and frame rate. See [Regions](#regions). |
| `--player <protracker\|ust>` | by file | Whose rules to read and play the MOD by: `protracker` (ProTracker 2) or `ust` (Ultimate Soundtracker). Default: `ust` for 15-sample files, `protracker` otherwise. See [Players](#players). |
| `--mapping <name>` | TBD | Channel-mapping strategy (see [channel-mapping.md](channel-mapping.md)). |
| `--ignore-samples <list>` | none | MOD sample numbers to leave out, 1-based, comma-separated, ranges allowed: `3,7,12-14`. Hex accepted with `0x` (trackers show hex: `0x1F` = 31). Repeatable. Unknown/empty numbers → exit 1. |
| `--require-fm <list>` | none | MOD samples that **must** be played by FM (never digitized PCM). Same list syntax as `--ignore-samples`. See [sample-capacity.md](sample-capacity.md#required-fm-and-pcm-samples). |
| `--require-pcm <list>` | none | MOD samples that **must** stay digitized PCM (never replaced by FM or PSG). Same list syntax. |
| `--no-consolidate` | off | Don't consolidate samples; if the song doesn't fit the target, print the capacity report and exit 6. |
| `--max-pcm-bytes <n>` | 128K | PCM byte budget per song for XGM/XGM2 (suffixes `K`, `M`). Over budget → capacity warning + consolidation. |
| `--quality <fast\|normal\|best>` | `normal` | `fast`: simple mapper, no fitting. `normal`: multi-pass with a 5-minute budget. `best`: run until no improvement. See [multi-pass.md](multi-pass.md). |
| `--time-budget <duration>` | per quality | Overrides the optimizer time limit (`90s`, `5m`). |
| `--seed <n>` | 0 | Optimizer seed; same input + options + seed → same output. |
| `--no-cache` | off | Don't read or write the fitted-patch cache. |
| `--reserve-sfx-channels <n>` | 0 | Leave n PCM channels free for game sound effects (XGM ≤ 4, XGM2 ≤ 3). |
| `--info` | off | Dry run: read, report and convert in memory (all report sections and conversion notes), but write no output file. |
| `--force` | off | Overwrite an existing output file. |
| `-v, --verbose` | off | Also print `Debug`-level detail (per-channel mapping, sample processing). |
| `-h, --help` | | Usage. |
| `--version` | | Version. |

## Value syntax and validation

Parsed by System.CommandLine; values are converted and checked by `src/Cli/OptionsBinder.cs`, which reports
**every** problem at once and exits **1**.

- `--format`, `--input-region`, `--output-region`, `--player`, `--quality`: exactly the lowercase values listed (help shows them).
- `--ignore-samples`: comma-separated numbers and ranges, decimal or `0x` hex, each **1–31**; repeat the option
  to add more. Numbers above the module's actual sample count (e.g. 16+ in a 15-sample MOD) are rejected after
  the MOD is read and report sections 1–2 are printed (exit 1, every option at fault named:
  `--ignore-samples 16,20: this module has samples 1-15 only.`).
- `--require-fm`, `--require-pcm`: same syntax and range as `--ignore-samples`, repeatable, and checked against the
  module's sample count the same way.
- **Conflicts (exit 1, every conflict reported):** a sample may not appear in both `--require-fm` and
  `--require-pcm` ("a sample can be FM or PCM, not both"), nor in `--ignore-samples` and either `--require-*`
  list ("an ignored sample isn't converted at all").
- `--max-pcm-bytes`: positive integer, optional `K` (×1024) or `M` (×1024²) suffix, case-insensitive.
- `--time-budget`: positive integer with optional `s`, `m` or `h` (plain number = seconds).
- `--reserve-sfx-channels`: `0`–`4` for `xgm`, `0`–`3` for `xgm2`; must be `0` for `dmf` (one DAC channel, nothing to reserve).
- `--mapping`: accepted as text for now; valid names are defined with the optimizer (plan M9).

## Output naming

Same directory and base name as the input; extension by format: `.dmf`, `.xgm` (XGM v1) or `.xgm2` (XGM2).
`C:\music\song.mod` → `C:\music\song.dmf`. `--output` is used exactly as given (any extension).

Checks before converting (`src/ConversionCommand.cs`):

| Check | Exit |
|---|---|
| Input missing, or a directory | 2 |
| Output is the input file (never overwritten, even with `--force`) | 4 |
| Output is a directory, or its directory doesn't exist (not created) | 4 |
| Output exists and `--force` not given | 4 |

With `--info` no file is written, so the output checks are skipped.

## Regions

Two different machines, each PAL or NTSC, set independently (both default `pal`; the old `--region` was removed,
see [ADR 0030](adr/0030-amiga-pitch-regions-finetune.md)):

- **`--input-region`** — the **Amiga** the MOD was made on. It sets how the song **really sounded**: its pitch
  (Paula clock PAL 3 546 895 Hz / NTSC 3 579 545 Hz, see [formats/mod.md](formats/mod.md#pitch); `ntsc` is about
  **15.9 cents higher**) and its speed (the tick rate follows the Amiga's screen: **50 ticks/s on PAL, 60 on NTSC**
  at BPM 125). Most MODs come from PAL Amigas, and openmpt123 (the accuracy reference) assumes PAL.
- **`--output-region`** — the **Genesis** that plays the result: its YM2612/PSG clocks and 50 or 60 Hz frame rate
  ([genesis-audio.md](genesis-audio.md)).

The output always sounds like the song did on its original Amiga, at that Amiga's pitch and speed (David,
[ADR 0030](adr/0030-amiga-pitch-regions-finetune.md)). The tick player (T3.4) times every tick at the source rate; the writers fit the ticks
onto the target's frames. When the regions match, one MOD tick is one Genesis frame; when they don't, the
converter spreads the ticks onto the frames so the speed stays the same:

| `--input-region` | `--output-region` | Ticks/s at BPM 125 | Frames/s | Ticks per frame |
|---|---|---|---|---|
| `pal` | `pal` | 50 | 50 | 1 |
| `ntsc` | `ntsc` | 60 | 60 | 1 |
| `pal` | `ntsc` | 50 | 60 | 5 ticks over 6 frames |
| `ntsc` | `pal` | 60 | 50 | 6 ticks over 5 frames |

Pitch never depends on `--output-region`: the converter picks the Genesis notes that reproduce the source pitch with
the target console's clocks.

## Players

Trackers read the same MOD bytes differently, so `--player` picks whose rules the converter follows, from reading the
file to playing it ([formats/mod.md](formats/mod.md#players)): `protracker` (ProTracker 2) or `ust` (Ultimate
Soundtracker). Without it, 15-sample files use `ust` and 31-sample files `protracker` (David). Every run prints the
choice right after report section 1, for example:

```
Emulating Ultimate Soundtracker (the default for 15-sample files; --player protracker changes it)
Emulating ProTracker 2 (chosen with --player)
```

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (conversion notes may have been printed) |
| 1 | Usage error (bad or missing arguments) |
| 2 | Input not found / unreadable |
| 3 | Input is not a valid or supported MOD |
| 4 | Output exists and `--force` not given, or output not writable |
| 5 | Conversion failed (internal error) |
| 6 | Song doesn't fit the target: `--no-consolidate` was given, or the `--require-pcm` samples alone exceed the PCM slots/bytes |

## Console report

**Whenever a MOD file is converted**, the converter reads and prints the MOD metadata and the full sample
table. This is mandatory: no option suppresses it, and it is printed for every output format (`dmf`,
`xgm`, `xgm2`) and with `--info`. Order:

1. Sections 1–2 are printed **immediately after the MOD is read**, before mapping or consolidation, so they
   appear even if conversion later fails.
2. Section 3 is printed after mapping/consolidation, before the output file is written.

Logged at `Information` level, one log event per line (`src/Mod/ModReport.cs`, plan T2.5). Numbers are printed
without digit grouping. The examples below are real output; the full files are the golden tests in
`tests/golden/report/`.

**1. MOD metadata** — everything the file embeds:

```
INF File        : song.mod (21802 bytes)
INF Title       : "space {debris}"
INF Format      : ProTracker M.K. — 4 channels, 31 sample slots
INF Song length : 2 orders, restart position 127
INF Patterns    : 2
INF Tempo       : speed 4, 140 BPM (initial)
```

- **Format** names the tracker from the tag: `M.K.`/`M!K!` ProTracker, `FLT4` StarTrekker, `4CHN` FastTracker;
  a file without a tag is `Soundtracker (no tag) — 4 channels, 15 sample slots`.
- **Restart position** is the byte as stored (ProTracker writes 127). In a 15-sample file the same byte may be
  Ultimate Soundtracker's tempo, so the line says so: `restart position 120 (Ultimate Soundtracker reads this byte
  as the tempo)`. Which reading applies is decided by the song model (plan M3).
- **Tempo** comes from the `Fxx` commands on the first row the song plays (row 0 of the first order's pattern),
  read left to right so the rightmost speed and BPM win; ProTracker's defaults (speed 6, 125 BPM) fill in what
  that row doesn't set. Later changes are the song model's job (plan M3).

**2. Samples / instruments** — in a MOD the sample *is* the instrument. List **all** slots, including empty
ones: composers often hide credits and messages in the names of empty samples, so those names are part of
the metadata.

```
INF  #   Name                     Length  Loop start+len  Vol  Fine  Notes  Status
INF  01  "bassdrum"                 4532  —                64     0      3  used
INF  02  "snare 2"                  3870  —                48    -2      2  used
INF  03  "  by Jester"                 0  —                 0     0      0  empty
INF  04  "strings"                  8000  2000+6000        64     0      1  used
INF  05  "never played"              100  —                64     0      0  unused
INF  06  ""                            0  —                 0     0      0  empty
INF  07  "vocal"                    2104  —                64     7      1  ignored
INF  08  "bad·name"                   64  —                64     0      0  unused
```

Lengths and loop points in bytes; Vol as stored; Fine = finetune −8…+7; Notes = how many notes start the sample
([usage scan](sample-capacity.md#usage-scan-srcmodsampleusagecs-plan-t24)); Status = used / unused / empty, or
**ignored** for a sample named in `--ignore-samples` (its Notes still show how often the song plays it).
Section 1 is printed as soon as the MOD is read; section 2 right after the usage scan, which supplies the Notes.

**3. Output instruments** — after mapping/consolidation, what the target will contain:

```
INF Output (XGM2): 5 FM instruments, 41 PCM slots / 122, 118 KB / 128 KB PCM
INF  PCM 01  ← 01 "bassdrum"     13300 Hz  4532 B
INF  PCM 02  ← 05 "bass" C-2     13300 Hz  2210 B
INF  FM  01  ← 06 "strings" (approximation)
```

DMF output lists the DMF instrument names that will be written (named after the MOD samples).

Non-printable characters in names (and the title) are shown as `·`; names are printed in quotes so spaces are
visible. A name is printed exactly as stored, braces included: each report line is logged as a property value, never
as part of the message template.

## Console output

Logged with Serilog (console sink, invariant culture):

- stdout: `Information` (and `Debug` with `--verbose`: the effective options, ignored samples, and later
  per-stage detail). The run ends with a summary such as `song.mod -> C:\music\song.dmf (12 conversion notes)`.
- stderr: every **conversion note** at `Warning` level (always shown), including the **capacity warning**
  when the samples exceed what XGM/XGM2 (or DMF) can hold — see [sample-capacity.md](sample-capacity.md) —
  plus `Error` / `Fatal`.

**XGM2 extension note.** Every run with `--format xgm2` (including `--info`) logs this line at
`Information`, verbatim, after the output path is reported:

```
INF NOTE: SGDK expects XGM2 files to use the .XGM file extension.
```

It is an informational message, not a conversion note, and isn't counted in the summary.

Format: `{Level:u3} {Message:lj}` — e.g. `WRN Effect 7xy (tremolo) on channel 2, row 14 dropped`.
