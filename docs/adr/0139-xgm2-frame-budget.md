# ADR 0139: The XGM2 writer sends only what changes, in XGM2's smallest commands, within the frame budget

- **Status:** Proposed
- **Date:** 2026-11-21
- **Revisit:** whenever SGDK's XGM2 driver (`drv_xgm2_ym_fct.i80`) changes how it stores TL, SL/RR, pan or frequency.

## Context

The `sg_SeasonsGreetings` ROM stutters while it plays `FunkyChristmas_xgm2.xgm`. The findings are in
[xgm2-frame-budget.md](../xgm2-frame-budget.md). The FM stream of the committed file is 372 KB. It holds 481 frame
splitters (`$F0`), each of which plays the rest of its frame, key-ons included, one frame late. It has only 11 load
instrument commands (`$2x`): every FM note start rewrites the channel's whole patch as raw writes (`$Ex`), even when
the channel already holds that patch. Busy frames also keep the Z80 busy, and the driver then delays music frames to
refill its PCM buffer.

rescomp compiles an XGM2 `.xgm` file into `.xgc` by packing it (`xgm2tool`, `XGM(byte[])`, then `packed = true`). It
doesn't optimize the commands, as it does for a VGM. So the commands the writer chooses are the ones the Z80 runs.

Plan task T10.1d. David chose the scope (2026-11-21): all four changes the findings propose, in one PR, plus every
compact FM command XGM2 has. The sound must not change.

## Decision

All of this is in `Xgm2Writer`. The player, the recording (`GenesisStream`) and the renders stay as they are. (David,
2026-11-21.)

1. **Send only what changes.** The writer keeps what the chip holds from the stream, and what the driver has stored
   (TL, frequency, pan). A register write or a `$9x` whose value the chip already holds is dropped. Rules:
   - A register the stream hasn't set yet is always sent, since the chip's power-on value isn't assumed.
   - **Frequency latch.** The YM2612 holds one high-byte latch per part for `$A4`–`$A6`, and another for `$AC`–`$AE`.
     The low byte (`$A0`…) copies the latch. A sent high byte is followed by its low byte. A low byte sent alone needs
     the latch to hold its own channel's high byte: if another channel's high byte was written since, the high byte
     goes again. The proposal missed this rule; the YM3438 model has the shared latch too.
   - Keys (`$28`), LFO (`$22`), CH3 mode and PCM commands always go.
   - The loop restore stays complete, as today. What it sends becomes what the chip holds, and registers the restore
     doesn't set count as unknown after the loop, since the second time through may leave them otherwise.
   - The driver stores TL from `$9x`, `$Cx`, `$Dx` and `$2x` (`loadInstSetInternalNext`), SL/RR from raw `$8x` writes
     and `$2x`, and pan from raw `$B4`, `$2x`, `$6x` and `$7x`. A dropped write repeats a value the driver already
     stored, so its own state stays right, and unmuting after pause or stop (`FM_unmute`, `updateSLRR`) restores the
     right values.
2. **`$2x` wherever it's smaller.** A channel gets one load instrument when the chip and driver values of all 30
   registers it carries (7 × 4 slot registers, `$B0`, `$B4`) are known and its 31 bytes cost less than the changed
   writes as raw writes and `$9x`. This generalizes the proposal's rule (all 29 `LoadPatch` registers written in the
   frame, and `$B4` known): resending a value the chip holds changes nothing.
3. **XGM2's compact FM commands** ([xgm2.txt](../formats/xgm.md), checked against `drv_xgm2_ym_fct.i80`):
   - `$4x` (1 byte) for a key on or off of all four operators, in place of `$F8` (2 bytes). `$F8` stays for others.
   - `$5x` (1 byte) for a key off then on (or on then off) of one channel with nothing between them.
   - `$3x` (3 bytes) for a channel's frequency: `$A4` then `$A0`, as the latch needs. It carries the key on when the
     channel keys on straight after, and the key off before it when that was the last command sent.
   - `$Ax` (2 bytes) for a frequency within 1–128 of the one the driver stored. The driver adds the delta to its
     stored 16 bits, key flags included, and writes the result, so it's used only when that gives exactly the new
     `$A4`/`$A0`. Raw writes to `$A0`–`$A6` don't update the stored frequency, so after the stream starts using `$3x`
     every frequency goes through `$3x` or `$Ax`.
   - The CH3 slot forms (`$3x`/`$Ax` with bit 3) for `$AC`–`$AE`/`$A8`–`$AA` in CH3 special mode. The driver ignores
     their key bits.
   - `$6x` / `$7x` (1 byte) for a pan change when `$B4`'s AMS and PMS bits stay. The driver keeps the stored low 6 bits.
   - `$8x`, `$Bx` and `$Dx`, the "+ end of frame" forms, in place of a `$3x`, `$Ax` or TL change followed by a 1-frame
     wait (`$00`). The driver ends the frame without setting a wait, which is the same as `$00`. `$Cx` alone is the
     size of `$9x`, so `$9x` stays.
   - The driver writes the key flags of `$3x` into bits 7–6 of `$A4`, which the chip ignores.
4. **Move patch writes earlier, only out of frames that would still split.** A frame still over the budget after 1–3
   gives up the writes to a channel's patch, levels and frequency that come before the channel's key on. All of that
   channel's writes in the frame move together, to the latest earlier frame with room. Frames below 80% of the budget
   come first. A frame qualifies only if, from its start until the key on, the channel is keyed off with no key
   events, the YM3438 model says all four operators are silent (released to full attenuation, or never keyed on), and
   no other write touches those registers. A move never crosses the loop frame. The key on never moves. If no frame
   fits, the split stays. The model runs only when a frame would still split.
5. **Note frames near the budget.** A conversion note counts the frames at 80% of the budget or more, at least 178 of
   the 222 bytes, the frame's ending wait included. It names the busiest frame (frame, time, order and row, bytes,
   register writes, channels), and a note each names the next four. The existing split note stays. `--verbose` logs
   the writer's numbers: FM bytes, writes dropped, `$2x` loads, compact commands, writes moved and writes that
   couldn't move.

**Acceptance (David).** The written file, decoded with a model of the driver's state, must leave the chip as the
recording intends at the end of every frame, and render the same, sample by sample, through the YM3438, SN76489 and
XGM2 PCM models. The old file differs from the recording only where it split frames.
`Xgm2IntendedSongTests` checks this (docs/testing.md#xgm2-intended-song-check). The frequency high byte is compared as
the chip takes it, through the latch. A channel's registers may differ only at frames where the YM3438 model has it
silent, which is where change 4 sends a patch early. Three things are left out, all decided before this ADR:
- registers the file sets and the recording never does (the loop's silent levels, ADR 0134);
- the PCM stop at a looping song's jump (ADR 0099);
- the sample number of a recording stored once for two samples with the same bytes.

A frame whose writes all drop costs no wait byte either: its wait is written with its first command, so the wait
before it covers it.

## Options considered

- **Chosen: all of it in the writer, in one change**, measured against the recording.
- **The proposal alone** (dedupe, `$2x`, moves, warning). It loses about 11 KB of `$F8` keys and every `$3x`/`$Ax`
  saving on FunkyChristmas, and David asked for every compact command.
- **One task per change.** Four reviews and four golden-file updates of the same writer.
- **Comparing with the old file, bit for bit.** It can't hold: removing a split makes the late notes play on time.
- **Moving writes out of frames at 80% as well.** It would even out Z80 spikes, but it moves more of the stream. David
  chose over-budget frames only.
- **Estimating silence from release rates.** The model's envelope is exact. An estimate could move a write into a
  release tail.
- **Running xgm2tool's VGM optimizer.** It's an external program, which the converter may not launch.

## Consequences

- `src/Xgm/Xgm2Writer.cs` encodes with the chip and driver state. The test decoder
  (`tests/Mod2Xgm.Tests/Dmf/Sanity/Xgm2Decoder.cs`) learns every new command, with its own model of the driver's
  state. The byte-exact writer tests change, with the intended-render tests as proof.
- `GenesisStream` carries each frame's order and row, for the note.
- The YM3438 model gains a read-only "channel silent" query for the move step.
- [formats/xgm.md](../formats/xgm.md) lists the commands the writer uses. [xgm2-frame-budget.md](../xgm2-frame-budget.md)
  is marked implemented.
- On hardware, a frequency folded into `$3x` reaches the chip a few microseconds later than the channel's other
  writes in that frame. All of a frame's writes are applied at the same moment in the model, and the channel is
  keyed off, or releasing, while it happens.
- Missed frames in the game are measured after the change by David (MUSIC_PERF build in Gens KMod). If they stay high
  once the splits are gone, the next steps cost sound or size, and they're the musician's call.
