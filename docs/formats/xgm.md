# XGM / XGM2 output format (SGDK)

Written by a **native writer**. The app never calls out to `xgmtool` / `xgm2tool`. Selected with
`--format xgm` (writes `.xgm`) or `--format xgm2` (writes **`.xgm2`**). Both are the uncompiled source form
(with the `XGM ` / `XGM2` magic); SGDK compiles to `.xgc`.

The `.xgm2` extension is deliberate even though SGDK expects `.xgm`; the user renames the file if needed.
Every XGM2 conversion prints, verbatim:

```
NOTE: SGDK expects XGM2 files to use the .XGM file extension.
```

> **Open (SGDK ingestion of XGM2):** SGDK's `rescomp.txt` documents only **VGM** input for the `XGM2`
> resource type (the `XGM` type accepts VGM or XGM). Whether rescomp accepts our XGM2 data must be confirmed
> in T10.2 (check rescomp's source; fallbacks: emit the compiled `.xgc` form for a `BIN` resource, or also
> emit VGM). The file extension is **not** part of this question.

## Sources

| Source | Use it for |
|---|---|
| SGDK `bin/xgm2.txt` — https://github.com/Stephane-D/SGDK/blob/master/bin/xgm2.txt | **XGM2 spec (authoritative)** — summarized below |
| [`xgm.txt`](xgm.txt) — SGDK `bin/xgm.txt`, **XGM v1.01**, copied 2026-10-27 | **XGM v1 spec (authoritative)** — summarized below |
| SGDK `bin/rescomp.txt` | How SGDK imports XGM/XGM2, channel counts |
| kuma4649/mml2vgm — https://deepwiki.com/kuma4649/mml2vgm/8.2-xgm-and-xgm2-formats | Overview; the C# writer `Core/clsVgm.cs` (`Xgm_getByteData()`) is a useful reference implementation |
| zerasul/mdbook_en, sound chapter | Driver capabilities overview |

## Driver capabilities

| | XGM | XGM2 |
|---|---|---|
| SGDK | 1.x and later | **2.00 and later** |
| PCM channels (software-mixed on the DAC, replace FM6) | **4** | **3** (ch 1 normally music, 2–3 SFX) |
| PCM format in file | 8-bit **signed**, 256-byte aligned address and size | 8-bit **signed**, 256-byte aligned |
| PCM rate | **14 093 Hz** NTSC / 13 964 Hz PAL fixed: one byte every 254 Z80 cycles | **13 317 Hz** NTSC / 13 195 Hz PAL (YM Timer A, one byte every 4 FM samples), or half that, "half speed", **per play command** |
| PCM mixing ([ADR 0058](../adr/0058-pcm-driver-models.md)) | channels added in order, each sum stuck at +127 / −128; DAC on while PCM plays and for 3 × 256 bytes after | the same; half speed writes each byte twice; DAC on for 3 × 64 bytes after |
| Max samples | **63** | **122** single-track (248-byte SID = 124 entries − #0 reserved − end-marker entry; mml2vgm says 123 — **verify**) |
| Max layout | 5 FM + 4 PCM + 4 PSG | 5 FM + 3 PCM + 4 PSG = 12 |
| Stream design | one combined stream | **separate FM and PSG streams** |
| Volume | — | FM / PSG volume adjustable at runtime (not PCM) |

## Common to both

Both are **register-level streams per frame** (1/60 s NTSC, 1/50 s PAL), not pattern data. The converter
therefore needs a **tick player** (`src/Xgm/Player`) that runs the IR song tick by tick and emits YM2612 /
PSG register state changes; the XGM and XGM2 writers then encode the same event list differently.
MOD ticks (BPM×2/5 Hz) rarely equal the frame rate — the player must resample time onto frames
(accumulate fractional ticks; conversion note when BPM ≠ 125 at PAL or ≠ 150 at NTSC).

## XGM2 layout (from `xgm2.txt`, version byte `0x10`)

All multi-byte values little-endian unless noted. Single-track file:

| Offset | Size | Field |
|---|---|---|
| `$0000` | 4 | Magic `XGM2` (absent in compiled `.xgc`) |
| `$0004` | 1 | Version `0x10` |
| `$0005` | 1 | Flags: b0 0=NTSC 1=PAL · b1 multi-track · b2 GD3 present · b3 packed FM/PSG/GD3 · b4–7 reserved |
| `$0006` | 2 | SLEN = sample block size / 256 |
| `$0008` | 2 | FMLEN = FM block size / 256 |
| `$000A` | 2 | PSGLEN = PSG block size / 256 |
| `$000C` | 248 | SID table (504 for multi-track): 2-byte entries = sample address / 256, relative to SDAT |

SID rules: entry #0 reserved (PCM-stop id); sample length = next entry − this entry, so a **dummy
terminating entry** is required; empty = `$FFFF`; samples **256-byte aligned**.

Blocks follow in order: **SDAT** (SLEN×256, 8-bit signed PCM) → **FMDAT** (FMLEN×256) →
**PSGDAT** (PSGLEN×256) → optional GD3. Pad each block to a 256-byte multiple.

v1 of the writer: single-track, unpacked, no GD3 (flags b1–b3 = 0).

### FM stream commands

| Byte | Len | Meaning |
|---|---|---|
| `$0x` | 1 | Wait x+1 frames (x 0–E) |
| `$0F xx` | 2 | Wait xx+16 frames |
| `$1x id` | 2 | PCM play: b1–0 channel, b2 half speed, b3 priority; id 1-based, 0 = stop |
| `$2x d[30]` | 31 | Load instrument (28 slot regs + 2 channel regs); b1–0 channel, b2 port |
| `$3x hi lo` | 3 | Freq set (14-bit, **high byte first**); b14 key-off before, b15 key-on after; b2 port, b3 CH3 special |
| `$4x` | 1 | Key off/on all ops (b3) |
| `$5x` | 1 | Key sequence off→on / on→off (b3) |
| `$6x` / `$7x` | 1 | Panning port 0 / 1: b3–2 0 off, 1 R, 2 L, 3 centre |
| `$8x hi lo` | 3 | As `$3x` + end of frame |
| `$9x yy` | 2 | TL set (b3–2 slot, b0 port, yy b7–1 TL) |
| `$Ax yy` / `$Bx yy` | 2 | Freq delta (b0 sign, b7–1 = delta−1) / + end of frame |
| `$Cx yy` / `$Dx yy` | 2 | TL delta (b1 sign, b7–2 = delta−1) / + end of frame |
| `$Ex …` | 1+2n | Raw register writes: b2–0 = n−1, b3 port; pairs reg,value |
| `$F0` | 1 | Frame splitter |
| `$F8 xx` | 2 | Key register `$28` write |
| `$F9 xx` | 2 | LFO register `$22` write |
| `$FA` / `$FB` | 1 | CH3 special mode on / off |
| `$FC` / `$FD` | 1 | DAC on / off (`$2B`) |
| `$FF dddddd` | 4 | Loop/end: 24-bit address in FM block, −1 = no loop. Always last |

### PSG stream commands

| Byte | Len | Meaning |
|---|---|---|
| `$0x` | 1 | Wait x+1 frames (x 0–D) |
| `$0E xx` | 2 | Wait xx+15 frames |
| `$0F dddddd` | 4 | Loop/end (address in PSG block, −1 = no loop) |
| `$1x yy` | 2 | Tone low update (b0 = end of frame) |
| `$2x yy` / `$3x yy` | 2 | Tone update (b3–2 channel, b1–0 tone bits 9–8) / + end of frame |
| `$4x`–`$7x` | 1 | Tone delta ch0–3 (b1–0 = delta−1, b2 sign, b3 EOF) |
| `$8x`–`$Bx` | 1 | Volume set ch0–3 (x = attenuation) |
| `$Cx`–`$Fx` | 1 | Volume delta ch0–3 |

Simplest correct encoder: use `$Ex` raw writes + waits + `$1x` PCM for FM, and tone/volume set for PSG.
Optimize to delta/combined commands later, with golden tests guarding output equivalence.

## XGM (v1.01) layout — from [`xgm.txt`](xgm.txt)

All multi-byte values **little-endian**.

| Offset | Size | Field |
|---|---|---|
| `$0000` | 4 | Magic `XGM ` (with trailing space) |
| `$0004` | 252 | Sample id table: **63 entries × 4 bytes** — `+0` address / 256 (relative to sample data block), `+2` size / 256. Empty entry: address `$FFFF`, size `$0001` |
| `$0100` | 2 | SLEN = sample data block size / 256 |
| `$0102` | 1 | Version `0x01` |
| `$0103` | 1 | Flags: b0 0=NTSC 1=PAL (frame wait = 1/60 or 1/50 s) · b1 GD3 tags present · b2 multi-track · b3–7 reserved |
| `$0104` | SLEN×256 | Sample data, **8-bit signed**, every sample's address **and size** aligned to 256 bytes |
| `$0104+SLEN×256` | 4 | MLEN = music data block size (bytes) |
| `$0108+SLEN×256` | MLEN | Music data (commands below) |
| after music | var | Optional GD3 tags (b1) |

Sample ids in commands are **1-based** (`id − 1` indexes the table); id 0 stops a channel. PCM plays at a fixed
**14 093 Hz** (NTSC; resample to that, not 14 000 Hz, which is 11 cents flat); up to **4** channels are mixed on the DAC **in place of FM6**, so music has FM1–5 + 4 PCM + 4 PSG.

### Music commands (one combined stream)

| Byte | Length | Meaning |
|---|---|---|
| `$00` | 1 | Wait **one** frame (no multi-frame wait — long rests are repeated `$00`) |
| `$1X d…` | 1+(X+1) | X+1 bytes written to the PSG port |
| `$2X (r,v)…` | 1+2(X+1) | X+1 YM2612 **port 0** register writes |
| `$3X (r,v)…` | 1+2(X+1) | X+1 YM2612 **port 1** register writes |
| `$4X d…` | 1+(X+1) | X+1 writes to the key on/off register `$28` |
| `$5X id` | 2 | PCM play: X & 3 = channel 0–3, X & `$C` = priority 0/4/8/12 (higher wins; lower-priority play on a busy channel is ignored); id 0 = stop |
| `$7E dddddd` | 4 | Loop: 24-bit offset from the start of the music data block |
| `$7F` | 1 | End |
| `$6X`, `$80`–`$FF` | — | Reserved — **never emit** |

### Writer rules (XGM)

- Group writes per frame: all register writes for a frame, then `$00`.
- Up to 16 writes per `$1X`/`$2X`/`$3X`/`$4X` command; split longer runs.
- Never write the DAC registers `$2A`/`$2B` or touch FM6 — the driver owns them (inferred from "PCM in
  replacement of the 6th FM channel"; confirm with the probe ROM).
- Music PCM priority: use a fixed level, proposed `0`, so game SFX at higher priority can take a channel —
  **confirm** what xgmtool emits for music. A channel's priority goes back to 0 when its sound ends or is stopped
  (`drv_xgm.s80`; [ADR 0058](../adr/0058-pcm-driver-models.md)); a play or stop at a lower priority is ignored.
- **CH3 special mode:** the format can express it (register `$27` is a port-0 write), but `$27` also holds the
  YM timer controls and the spec doesn't say whether the driver uses them. Enable for XGM only after a probe
  ROM shows it plays correctly (T11.1).
- GD3 tags and multi-track: not written by default (flags b1, b2 = 0). Proposed later: GD3 with the MOD title.
- `.xgz` (gzip) is an optional variant; not produced.

## Rules

- Capacity and consolidation: see [../sample-capacity.md](../sample-capacity.md). XGM/XGM2 can't pitch
  PCM, so each played *(sample, pitch)* is its own pre-resampled sample slot.

- Resample PCM to the target rate and convert to 8-bit with the target's signedness.
- Respect the PCM channel limit (4 / 3); collisions are resolved by the mapper and reported, never silently dropped.
- Validation: output must play in an SGDK test ROM or an accurate emulator (BlastEm, Genesis Plus GX) via
  `XGM_startPlay` / `XGM2_play`.
