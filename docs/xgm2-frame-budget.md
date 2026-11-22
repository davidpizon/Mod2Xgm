# XGM2 frame budget: why FunkyChristmas stutters, and what the XGM2 writer should change

- **Status:** Implemented in plan task T10.1d, decisions in [ADR 0139](adr/0139-xgm2-frame-budget.md). The writer
  now does all four changes below, with the compact commands of xgm2.txt; the rest of this page is the original proposal.
- **Date:** 2026-11-21
- **Found in:** the `sg_SeasonsGreetings` Genesis ROM (SGDK 2.11), which plays
  `FunkyChristmas_xgm2.xgm`, made by `mod2xgm FunkyChristmas.mod --format xgm2 --output-region ntsc --require-dac 19 --force`.

## Summary

The music in the ROM stutters: uneven hitches, heard in BlastEm and Gens. Measurements on the console side show the
stutter does not come from the game code. It comes from the song. In busy rows the XGM2 file holds more FM
commands per frame than SGDK's XGM2 driver takes in one frame.

The converter reports it: **481 frame splits**. At each split, the rest of the frame's FM writes, key-ons included,
play one frame (16.7 ms) late. Busy frames also keep the Z80 busy for longer. Then the driver delays music frames to
refill its PCM buffer, which the game measured as *missed frames*.

The cause is mostly in Mod2Xgm, and it can be fixed without changing the sound:

1. **Send only registers whose value changes.** Every FM note start today rewrites the channel's whole patch, even
   when the channel already holds it.
2. **Use the 31-byte load instrument command (`$2x`) for every full patch load.** Today the writer falls back to
   about 62 bytes of raw writes for most of them.
3. **Move patch writes earlier, into frames with room**, but only while the channel is silent, so the render stays
   bit-identical.
4. **Warn when a frame nears the budget** (80%), not only when it overflows.

The rest of this document gives the evidence, then each change with pointers into the code, its rules and its
acceptance tests.

## What we measured

### The game side (sg_SeasonsGreetings)

The ROM plays the song on two screens: a fireplace screen and a procedural flame screen. The game added an
instrumentation build, off by default (`make EXTRA_FLAGS=-DMUSIC_PERF`). About once a second it logs the
68k CPU load and the XGM2 driver's own debug counters: `XGM2_getDebugMissedFrames()`, `XGM2_getCPULoad()`,
`XGM2_getDMAWaitTime()` and `XGM2_getDebugPCMRate()`.
The log goes through `kprintf`. Gens KMod shows it in its Messages window; BlastEm 0.6.2 doesn't support KDebug.

| | Fireplace screen | Flame screen |
|---|---|---|
| 68k CPU load | ~9% (one main-loop pass per frame) | ~400% (each pass takes 4–5 frames) |
| Z80 DMA wait | 0% throughout | 0% throughout |
| PCM rate | 13,080–13,560 Hz (nominal 13,300) | 13,320–13,620 Hz |
| Z80 load (one-frame snapshots) | mostly 26–45%, spikes to 86–100% | mostly 30–43%, spikes to 74–99% |
| Missed music frames | ~1.2/s overall; 0 for the first 7 s, rising to ~2.7/s | ~1.4/s |

What this rules out:

- **The 68k load.** The fireplace screen leaves the 68k nearly idle and the flame screen overruns by 4–5 frames, yet
  both miss music frames at about the same rate. The Z80 counts frames from its own VBlank interrupt, so a slow 68k
  doesn't slow the music.
- **DMA.** The Z80 never waited on a DMA.
- **PCM starvation in steady state.** The playback rate stays at its nominal value.
- **The screen switch.** The miss rate carries on across the switch from fireplace to flame, rises and falls with
  the song's sections, and doesn't follow the screen.

### What a missed frame is

In SGDK 2.11's `src/snd/xgm2/drv_xgm2.s80` (VInt handler, near the `MISSED_FRAME` increment), a music frame is due
at each VBlank. If the PCM buffer is low at that moment (`isPCMLessThan 150`), the driver refills the buffer first and
processes the frame later. The counter counts these delays. One delay is one music frame heard late, which is a
hitch. The buffer runs low when the previous frame's commands took the Z80 a long time to process. So **heavy
frames cause missed frames**.

### What the conversion holds

A dry run with the same options (`--info --verbose`) printed:

- `481 frame splits: some frames hold more FM writes than SGDK's XGM2 driver takes at once, so the writes after each split play one frame later`
- `Mapped 41 orders: 6 FM patches, 7 DAC samples`; `Recordings need 31 PCM slots, 2 PCM voices at once, 180992 PCM bytes`
- Samples 1, 2, 14 and 15 are recorded chords. Each of their notes is played as 3 FM notes on separate channels
  ([ADR 0114](adr/0114-fm-chords.md), [ADR 0125](adr/0125-chord-samples.md)). Sample 14 alone has 221 such notes.
- `3605 FM notes let go a tick early` ([ADR 0124](adr/0124-release-before-key-on.md)). That's one more burst of
  writes, 4 release rates and a key-off, the tick before many notes.
- `114 drum notes ... played by the PSG noise channel`

The dry run's output wasn't compared byte for byte with the committed `.xgm`. Counting `$F0` commands in the
committed file's FM stream would confirm it holds the same splits. That's the first check for the implementing agent.

### Sample 19 is not the cause

The song's user wants sample 19, the opening swell, to stay a recording (`--require-dac 19`). It isn't the problem:

- It plays only in orders 0–2, about the first 19 s at speed 5 and 125 BPM. That's where the game measured the
  fewest misses: none in the first 7 s.
- The song never needs more than **2 PCM voices at once**; XGM2 mixes 3.
- The Z80's PCM cost depends on how many voices play, not how long a recording is. Sample 19's 9 stored recordings
  (~11 KB each at 13,317 Hz) cost ROM space, not Z80 time.

## The limit: bytes per frame, not register writes

A frame's FM commands must stay under 256 bytes. There are two reasons:

- xgm2tool's packer takes frames under 256 bytes (`XGCPacker.FRAME_MAX_SIZE`).
- The driver's FM command buffer is a 256-byte ring (`FM_COM_BUFFER`, `$100`, in `drv_xgm2.s80`).

`Xgm2Writer` keeps a 31-byte margin for a packer edge case (`MaxFrameBytes = 256 - 31`). `Command()` splits when a
command would take the frame past `MaxFrameBytes - 1 - FrameEndBytes`. So the **budget is 222 bytes of commands per
frame**, plus the 1–2-byte wait that ends it.

What a split does: the writer puts `$F0` (`FMCom_frameDelay`) and the driver stops reading the frame there. The rest
runs on the next frame. Key-ons are in "the rest", so notes start 16.7 ms late. The writer pays the frame back on the
next wait (`_fmLag`), so the song doesn't drift, but the notes after the split are late.

Since the budget is bytes, how many register writes fit depends on the commands used:

| Command | Bytes | Used for |
|---|---|---|
| `$Ex` raw writes | 1 + 2 per write, up to 8 writes of one part | most registers |
| `$9x` total level | 2 | every TL (the driver keeps each slot's TL to add the game's volume) |
| `$2x` load instrument | 31 | a channel's whole patch (7 registers × 4 slots, `$B0`, `$B4`) |
| `$F8` key | 2 | every key-on and key-off |

## Why busy rows overflow today

`GenesisPlanPlayer.Start` (its `VoiceKind.Fm` case) calls `LoadPatch(c, voice.Index)` for **every** FM note start. That calls
`FmVoice.LoadPatch`, which writes all 29 patch registers: for each of 4 operators `$3x $4x $5x $6x $7x $8x $9x`,
then `$B0`. `$B4` (pan, LFO sensitivity) is written only when it changes (`GenesisPlanPlayer.LoadPatch`).

In `Xgm2Writer.FlushRaw`, a channel gets `$2x` only when all **30** patch registers (`PatchRegisters`) were written in
the frame. Without `$B4`, most patch loads miss that test and go out as raw writes:

| Part of one FM note start | Today | With `$2x` | Same patch already on the channel |
|---|---|---|---|
| 24 operator registers + `$B0` (+ `$A4`/`$A0` frequency) | 4 × `$Ex`, ~58 bytes | 31 bytes (`$2x`) + 5 (frequency) | ~9–13 bytes (frequency, plus `$8x` if a release was shortened) |
| 4 total levels | 8 bytes (`$9x`) | inside `$2x`, plus any `$9x` the gain changes | 0–8 bytes |
| Key-on | 2 bytes | 2 bytes | 2 bytes |
| **Total** | **~68 bytes** | **~38 bytes** | **~11–23 bytes** |

At about 68 bytes, **3 note starts fit in 222 bytes and a 4th splits the frame.** A row where two chord samples start
makes 6 FM note starts, about 400 bytes: one split, and sometimes two. The writer already removes a register written
twice in one frame ("a register written twice is sent once, with its last value"). It doesn't check whether a value
matches what the chip already holds from earlier frames.

## Proposed changes

Order matters. Change 1 gives the biggest saving at the lowest risk. Change 2 covers patches that really do change.
Change 3 is only for what's left. Splitting stays as the last resort. Change 4 can ship at any time.

The rule for every change is the user's: **it must not change the sound.** That means a bit-identical render (see
[Acceptance tests](#acceptance-tests-for-every-change)).

### 1. Send only registers whose value changes

**Where:** `Xgm2Writer.Encoder`, in `FlushRaw()`, which already turns each frame's writes into commands. It's the
right layer: the player and the renderer stay as they are, and only the XGM2 bytes change.

**How:** keep a second register map, `_fmSent`: what the stream has sent for each `(part, register)`, like
`_toneSent` and `_volumeSent` on the PSG side. `_fm` stays as it is: what the player wrote, which the loop restore and
`LoadInstrument` read. In `FlushRaw()`, drop a raw or `$9x` write when `_fmSent` holds the same value. Update `_fmSent`
for everything sent, including the fields a `$2x` carries.

**Rules (each is a way to get this wrong):**

- **Never skip a register with no sent value.** Its first write always goes, so a register first set after the loop
  frame is still sent on the second time through.
- **Frequency latch.** On the YM2612, `$A4`–`$A6` (block, F-number high) only take effect when `$A0`–`$A2` is written.
  The same goes for `$AC`–`$AE` and `$A8`–`$AA` in CH3 mode. If the high byte is sent, send its low byte too, even
  when the low byte didn't change, and after it, as `RestoreOrder` already does at the loop. A skipped high byte
  doesn't force a low-byte write.
- **Keys, LFO, CH3 mode and PCM commands always go.** `$F8` key-ons retrigger; leave `$F9` `$FA` `$FB` and `$1x` as
  they are. They're small, and skipping them isn't needed for the budget.
- **The loop point.** `StartLoop` writes every register in `_fm` so the chip is right on the second time through. Keep
  that complete: don't dedupe the loop restore against `_fmSent`, and set `_fmSent` from it. Frames after the loop
  then dedupe against the state the restore guarantees on both passes.
- **The driver's own TL state.** The driver adds the game's volume to the TL it stored from `$9x` and `$2x`. Skipping a
  `$9x` whose level equals the last one sent leaves that store correct. Check `drv_xgm2_ym_fct.i80` to confirm `$2x`
  updates the stored TLs the same way (`FM_loadInst`), before counting them as sent.
- **Pause and stop.** The driver mutes FM on pause or stop by writing max `SL`/`RR` (`setMaxSLRR`). Check whether
  resuming restores those registers. If it doesn't, a deduped `$8x` after a resume could stay at max release. That
  only matters to games that pause music mid-song, but record the finding in the ADR.

**Expected saving:** most of a note start's 58 bytes whenever a channel plays the same patch again, as chord voices
and repeated notes usually do. Measure how many FM note starts keep the channel's patch, and report it, before and
after.

### 2. Use `$2x` for every full patch load

**Where:** `Xgm2Writer.Encoder.FlushRaw()`, the `patches` test.

**How:** count a channel as a whole patch when the frame writes all 29 registers `LoadPatch` writes (every operator
register and `$B0`) **and** `_fm` already holds a value for `$B4`. `LoadInstrument` then fills the `$B4` byte from
`_fm`: the value the chip holds, so writing it again changes nothing.

**Rules:**

- If `$B4` was never written, keep today's path. The chip's `$B4` at power-on and the driver's stored pan are not
  something to guess.
- Apply change 1 first, then decide: if only a few patch registers differ from `_fmSent`, raw writes for those are
  smaller than 31 bytes. Pick the smaller encoding per channel per frame.
- `$2x` also writes the four TLs. Make sure the driver's stored TL and the `$9x` dedupe (change 1) agree after a
  `$2x`.

### 3. Move patch writes earlier, while the channel is silent

This is for frames that are still over the budget after changes 1 and 2: patches that really change, on a busy row.

**Where:** a new step between recording and writing. `Xgm2Writer` encodes frame by frame and can't look back, so:

- First collect each frame's writes and their encoded size.
- Then move writes from frames over the budget into earlier frames with room.
- Then encode as today.

This works like the PCM lead of [ADR 0119](adr/0119-xgm2-pcm-lead.md), which already sends plays 2 frames early.

**What may move:** the patch registers of an FM channel (`$3x`–`$9x` and `$B0`), the TLs of that channel, and its
frequency (`$A4`/`$A0`) for a note that keys on at frame *K*. Moving one register means moving all of that frame's
writes to that channel's patch together.

**Where it may go:** frame *J* < *K*, only if, at the start of every frame from *J* to *K* − 1:

- the channel is keyed off, and
- every operator's envelope is at maximum attenuation in the YM3438 model, or the channel has never sounded, and
- no other write to the same register falls between *J* and *K*.

The key-on itself never moves.

**Where it may not go:**

- before the key-off that ends the channel's previous note, or into the frames while that note is still releasing;
- across the loop frame;
- before a frame the song's start trims (`--trim-silence`).

**Which frame:** the latest *J* that fits, so the stream changes as little as possible. Prefer frames below the
warning threshold (change 4) over frames that would only just fit. If no frame fits, leave the write where it is: the
split stays, and so does its warning.

**Why it doesn't change the sound:** a key-on resets each operator's phase. On a silent, keyed-off channel the patch
registers have no effect on the output until that key-on. So moving them changes no sample. The silence test must
be read from the model's envelope state, not estimated from release times. The acceptance test then proves it
render-wide.

**Where it helps, and where it doesn't:** [ADR 0124](adr/0124-release-before-key-on.md) lets a note go one tick before
the next key-on, with release rate 14, about 12 ms to silence. During legato that gives a window of about one tick, so
there may be no silent frame before *K*. The move then can't help, and that's the correct answer. It helps after rests
and on channels whose last note ended earlier. Report how many writes moved and how many couldn't.

### 4. Warn when a frame nears the budget

**Where:** `Xgm2Writer.Write`, next to the existing frame-splits note. Use `ConversionNotes.Add`, which logs at
warning level and keeps the note for the summary.

**What:**

- Measure each frame's final FM bytes, after changes 1–3, including the wait that ends it.
- **Warn at 80% of the budget: 178 bytes or more.** Keep the existing note for frames that still split; a split is
  the stronger warning.
- Say it in bytes, and give the register-write count too, since that's what a musician thinks in. The shape of the note, with placeholders:
  `{Count} frames use 80% or more of the 222 bytes of FM commands SGDK's XGM2 driver takes per frame; the busiest: frame {Frame} ({Time}, order {Order} row {Row}) {Bytes} bytes, {Writes} register writes on {Channels}`.
- List the 5 busiest frames with the frame number, the song time, the order and row, the bytes and the writes. A
  musician can then find them in the tracker, and an agent can find them in the plan.
- Carry the same numbers in `--progress json`, following [cli-output.md](cli-output.md).
- Make the threshold a named constant next to `MaxFrameBytes`, with a comment on where 80% comes from.

A later step, not part of this proposal, could also estimate Z80 time per frame from the cycle counts commented in
`drv_xgm2_ym_fct.i80`. Bytes are a good first proxy: missed frames come from Z80 time, and bytes are what the Z80
spends it on.

## Acceptance tests for every change

- **Bit-identical render.** Decode the XGM2 written before and after each change into chip writes, using the test-only
  `tests/Mod2Xgm.Tests/Dmf/Sanity/Xgm2Decoder.cs` (with `ChipLog` / `LogComparer`). Render both through the YM3438,
  SN76489 and XGM2 PCM models, then compare the renders sample by sample. Any difference fails the test. Run it on
  FunkyChristmas and on the corpus ([testing.md](testing.md)).
- **Unit tests for each rule** in change 1 (first write, frequency latch, keys, loop restore, TL after `$2x`), change 2
  (`$B4` known or not, smaller encoding wins) and change 3 (no move while sounding, releasing, across the loop, or past
  the trim).
- **Golden files.** `Xgm2Writer` has byte-exact golden tests. They will change. Update them in the same PR, with the
  bit-identical render as the proof that the new bytes sound the same.
- **Numbers to report in the PR** for FunkyChristmas, with the same options as the game: frame splits (481 before),
  frames at 80% or more of the budget, FM stream bytes, writes skipped, `$2x` loads, writes moved and writes that
  couldn't move.

## Why this should fix the stutter

1. **Splits.** A split is the converter delaying writes, key-ons included, by a frame. It's a hitch by construction,
   and it lands on the busiest rows, usually downbeats, where it's easiest to hear. Today a 4th note start in a frame
   splits it. With changes 1 and 2, a typical note start drops from about 68 bytes to between 11 and 38, so a row's 6
   chord voices fit in one frame. Expect most of the 481 splits to go, and change 3 to take care of most of the rest.
   **High confidence**: the converter counts the splits itself.
2. **Missed frames.** The driver delays a music frame when the previous frame's commands kept the Z80 too busy to keep
   the PCM buffer full. Fewer bytes per frame means less Z80 time per frame, so fewer delays. **Medium
   confidence**: the game measured ~1.2–1.4 misses a second on both screens, and the Z80 load spikes to 100%. That
   points at heavy frames, but some of the load is PCM mixing that these changes don't touch. Measure it rather than
   assume it.

### How to check it in the game

1. Reconvert: `make music` in `sg_SeasonsGreetings`, with `MOD2XGM` pointing at the new build. Then `make remake`.
2. Build the instrumented ROM: `make all EXTRA_FLAGS="-DMUSIC_PERF"`. Run it in Gens KMod and copy the `MUSIC_PERF`
   lines from the Messages window.
3. Compare `missed+N` per `videoFrames=` with the numbers above. The goal: the converter reports 0 splits, and misses
   are near 0 on both screens. Listen for the hitches at the same time.

If the splits are gone and misses stay high, the remaining Z80 load is PCM mixing plus normal FM work. The next
options then cost sound or size, so they're the musician's call: fewer simultaneous PCM voices (`--require-fm` or
`--require-psg` on the drums), or fewer chord voices.

## Also found (not Mod2Xgm)

- The flame screen in `sg_SeasonsGreetings` runs at about 12–15 fps: each update takes 4–5 frames. It doesn't affect
  the music, but the animation is choppier than intended. It's tracked in that repository.
- The dry run took about 22 minutes, almost all of it in step 7 (`fit`). `--time-budget 5m` didn't limit it,
  because the optimizer isn't built yet (plan M9) and the budget doesn't cover the fitting step. Worth a note in
  [cli.md](cli.md), or a cap of its own.
