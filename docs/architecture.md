# Architecture

## Pipeline

```
 .mod bytes
    │  ModReader            (src/Mod)       big-endian parse + validation
    ▼
 ModModule                                  faithful image of the file
    │  ModToSong            (src/Model)     effects decoded, timing resolved
    ▼
 Song (IR)                                  format-neutral: orders, rows, notes, instruments, samples
    │  IChannelMapper       (src/Mapping)   SimpleMapper, then multi-pass Optimizer — see multi-pass.md
    │     ↑↓ uses: Render (src/Render: MOD renderer, YM2612/PSG/PCM models) + Scoring (src/Scoring)
    ▼
 GenesisPlan                                per-channel events for FM1–6 / DAC / PSG1–4
    │  SampleBudgeter       (src/Mapping)   capacity check + consolidation — see sample-capacity.md
    ▼
 GenesisPlan (fits target)
    │  SampleProcessor      (src/Audio)     resample, 8/16-bit, loops, length limits
    ▼
 ┌──────────────┬──────────────────┐
 DmfWriter      XgmWriter          Xgm2Writer      (src/Dmf, src/Xgm)
 .dmf (zlib)    .xgm               .xgm2
```

Each arrow is a pure function of its input plus a shared `ConversionNotes` collector (each note is logged
via Serilog at `Warning` level — see AGENTS.md § Logging). No layer
reads files except `ModReader`'s caller, and no layer writes files except the CLI.

## Layers

**ModReader** — Detects variant (31 vs 15 samples, channel tag), parses header, sample headers,
order list, patterns and sample data. Rejects malformed or unsupported files with
`ModFormatException(offset, reason)`. Does no interpretation.

**ModToSong** — Turns raw cells into musical events: period → note + fine pitch, effect decoding,
resolves `Fxx` speed/BPM changes into a tick timeline, handles pattern break / position jump
(`Dxx`/`Bxx`) and detects the loop point.

**Song (IR)** — Holds what any output needs:

- `Orders` (list of pattern indices) and loop position
- `Patterns` → `Rows` → per-channel `Cell { Note?, Instrument?, Volume?, Effects[] }`
- `Instruments` → reference to a `Sample` + default volume + finetune
- `Samples` → normalized PCM (`float[]` or `short[]`), source rate, loop start/length
- `Timing` → initial speed/BPM, region (PAL/NTSC)

**IChannelMapper** — Decides which Genesis channel plays each MOD channel/instrument, and how (DAC sample,
FM patch, PSG). Several implementations can exist; selected by CLI flag. See
[channel-mapping.md](channel-mapping.md).

**ModReport** (`src/Cli`) — called **unconditionally** by the conversion command: prints the metadata and
full sample table from `ModModule` right after reading (before any later stage can fail), and the output instrument table after budgeting. See
[cli.md](cli.md#console-report).

**SampleBudgeter** — computes required sample slots, simultaneous PCM voices and PCM bytes for the target,
logs the capacity warning, and runs the consolidation steps (dedupe, trim, pitch quantize, merge, mid-song
substitution, voice stealing). Applies `--ignore-samples` before anything else.

**SampleProcessor** — Converts sample data to what the target accepts (rates, bit depth, signedness,
max length), applies MOD volume where the target cannot, unrolls or trims loops.

**Writers** — Serialize the plan. The DMF writer emits pattern data that DefleMask plays; XGM writers emit
a per-frame register-write stream (they need a small "player" that runs the song tick by tick to produce
YM2612/PSG writes — shared between XGM and XGM2).

## Why an IR

MOD is only the first input, and DMF / XGM / XGM2 are very different (pattern-based vs register stream).
The IR keeps the MOD quirks in one place and lets the mapping experiment without touching parsers or writers.

## AOT considerations

Everything is plain classes/records and spans; no reflection. Native libraries (e.g. a MOD renderer or a
YM2612 emulator core used for in-app scoring) are called via P/Invoke with source-generated
`[LibraryImport]` and preferably statically linked into the AOT binary. No external processes are started. Compression uses `ZLibStream`. Keep the
binary small: avoid LINQ-heavy hot paths only if profiling says so — correctness and clarity first.
