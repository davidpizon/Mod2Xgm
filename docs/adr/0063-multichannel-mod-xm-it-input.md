# ADR 0063: Multichannel MOD, XM and IT input

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

David asked what it would take to read Impulse Tracker (`.it`) files next to MOD and MIDI. Nothing in the docs
covered it: [README.md](../README.md) listed S3M/XM/IT input as "not in v1", and the MOD reader still rejects 6- and
8-channel MODs ([formats/mod.md](../formats/mod.md#variant-detection)). What the code assumes today (checked at
`ff126d8`):

- **The tick player reads the MOD, not the song.** `ModTickPlayer.Play` takes a `ModModule`
  ([ADR 0033](0033-tick-player.md)) and holds ProTracker's rules, so each new format needs its own tick player. The
  layers after it are already format-neutral: `ModRenderer`, `SimpleMapper`, the DMF builder and `GenesisRenderer`
  read `Song` + `Playback`.
- **One voice per pattern channel.** `Playback` has one `ChannelTick` per song channel, and `GenesisPlanPlayer`
  rejects a playback whose channel count differs from the song's. IT's New Note Actions (NNA) keep the old note
  sounding when a new one starts on the same channel, so one pattern channel can hold several voices.
- **The model is ProTracker-shaped** ([ADR 0029](0029-song-model-types.md)): each `Instrument` points at one
  `Sample`, `SampleLoop` is a forward loop, and the effect list is ProTracker's. XM and IT have instruments that pick
  a sample per note, volume and panning envelopes (IT also pitch/filter), fadeout, ping-pong and sustain loops,
  16-bit and stereo samples, global and channel volume, and many more effects. IT adds a resonant filter, Zxx MIDI
  macros and random volume/pan variation.
- **More voices than the Genesis has.** A 4-channel MOD fits 6 FM + 3 PSG + DAC; a 16- to 64-channel module doesn't,
  so it needs the optimizer's voice stealing (T9.4), like MIDI ([ADR 0048](0048-midi-input.md)).
- **The reference already plays them.** libopenmpt, the harness's reference ([ADR 0047](0047-accuracy-harness.md)),
  plays multichannel MOD, XM and IT, so these formats get an accuracy reference without new tools.
- **The CLI and report are sample-shaped** ([ADR 0008](0008-report-on-every-conversion.md),
  [ADR 0028](0028-console-report-sections-1-2.md)): `--ignore-samples` and `--require-fm/dac/psg` take MOD sample
  numbers 1–31; `--player` and `--input-region` describe Amiga trackers.

Claude described the work and asked five questions on 2026-10-31; David answered three and asked Claude to recommend
the other two, then accepted the recommendations ("write it up").

## Decision

**By David, 2026-10-31:**

1. **IT comes after MIDI** (M13).
2. **NNA is emulated** (background voices, duplicate checks, note fade). What matters is that the converted songs
   sound similar to the originals.
3. **Stepping stones: multichannel MOD, then XM, then IT.** S3M is not planned.

**Recommended by Claude, accepted by David, 2026-10-31:**

4. **The XM and IT tick players port OpenMPT's playback rules** (BSD-3), not the original trackers' replayers and not
   OpenMPT's whole player:
   - **Ported:** what decides how XM and IT play: effects and their memory, envelopes, fadeout, auto-vibrato, NNA and
     duplicate checks, and the compatibility behaviors OpenMPT applies to XM and IT files. They are reshaped into one
     shared voice/envelope engine with an `XmTickPlayer` and an `ItTickPlayer` on top, each producing the existing
     `Playback`. Only the XM/IT paths are taken from OpenMPT's player, which serves every format it reads.
   - **Not ported:** OpenMPT's mixer and resamplers. `ModRenderer` already matches libopenmpt by measurement
     ([ADR 0054](0054-mod-renderer.md)) and gains only what the formats need: ping-pong loops, sustain-loop release,
     16-bit samples and IT's filter.
   - **Checked** feature by feature with probe files against libopenmpt, as T7.6 did for MOD
     ([ADR 0060](0060-openmpt-pitch-and-loops.md)).
   - The port pins an OpenMPT release, checks the license file at that commit, credits it in
     `THIRD-PARTY-NOTICES.md` and gets its own library ADR in T15.3, like the ymfm port ([ADR 0056](0056-ym3438-model.md)).
5. **The command line keeps MOD's pattern:**
   - `--ignore-samples`, `--require-fm`, `--require-dac` and `--require-psg` name **samples**, with the same list
     syntax (`3,7,12-14,0x1F`). Capacity, consolidation and routing work on samples, as for MOD.
   - **Numbering:** 1-based, as the tracker shows it. IT samples are numbered in the file. XM samples belong to
     instruments, so they are numbered through the file in order (instrument 1's samples first), as OpenMPT shows
     them.
   - **The range check follows the file**: 1 to the file's sample count instead of 1–31, with today's message
     ("this module has samples 1-N only").
   - **Report section 2** lists every sample with its number and the instruments that play it, so the numbers can be
     read off the report, and also the full instrument table: XM and IT instrument names often hold the credits, like
     MOD sample names.
   - **`--player` and `--input-region`** keep applying to multichannel MOD. On XM and IT they are a usage error
     (exit 1), the rule ADR 0048 set for options that don't fit MIDI.
   - Naming a whole instrument at once may follow in its own ADR.

**Design (Claude's proposal, accepted with the answers above):**

- **Detection by content**, as for MIDI: `Extended Module: ` at offset 0 means XM, `IMPM` means IT, the MOD tag
  decides between 4-channel and multichannel MOD; anything else goes to the MOD reader as today.
- **Own readers, no library**: `src/Xm/XmReader` → `XmModule`, `src/It/ItReader` → `ItModule`, like
  `ModReader` → `ModModule`; then `XmToSong` / `ItToSong`. Each reader has a builder test helper like `ModBuilder` and
  a format doc (`docs/formats/xm.md`, `docs/formats/it.md`) with **Verify** markers.
- **Multichannel MOD** (`6CHN`, `8CHN`, `xxCH`, `FLT8`, `CD81`, `OKTA`): the MOD reader accepts them; FLT8's split
  patterns are joined. The tick player follows how OpenMPT plays them, measured in T14.1.
- **Model** (extended, not duplicated): instruments with a note → sample map, envelopes with loops and sustain,
  fadeout, auto-vibrato, loop kinds (forward, ping-pong, sustain), global and channel volume, the new effects;
  `Playback` gets **voices**, which may outnumber the song's channels, each knowing the pattern channel it came from.
  The test that keeps MOD types out of `src/Model` covers XM and IT types too.
- **IT's resonant filter, for sounding similar:** the reference renderer emulates it, so scores stay honest. For the
  Genesis, a note whose cutoff doesn't change has the filter baked into its sample data; a changing cutoff is
  approximated, with a conversion note. Only OpenMPT's default filter macros for Zxx are understood; other macros are
  ignored, one note each.
- **Random volume and pan variation (IT)** uses the converter's `--seed`; the reference's random values can't be
  matched, so files that use it get a conversion note and their scores are read with that in mind.
- **Envelopes on the Genesis:** XGM and XGM2 write registers every frame, so envelopes play per tick. DMF FM
  instruments have no macros ([formats/dmf.md](../formats/dmf.md)), so FM envelopes become per-row volume changes;
  PSG instruments use their volume macro. Ping-pong loops are unrolled into forward loops (more PCM bytes), stereo
  samples are mixed to mono, each with a conversion note.
- **Capacity:** XM and IT samples are often 16-bit, high-rate and many; the existing consolidation
  ([sample-capacity.md](../sample-capacity.md)) handles them, and heavy consolidation is expected.

## Options considered

- **Port OpenMPT's XM/IT playback rules (chosen).** The harness measures "sounds similar" against libopenmpt, so
  matching its rules is the shortest way there; its compatibility behaviors already encode the documented
  FastTracker 2 and Impulse Tracker quirks. BSD-3 allows the port.
- **Port the original replayers** (Impulse Tracker's released source, FastTracker 2). The MOD tick player started as
  a port of ProTracker 2's replayer and then had to be measured and corrected toward OpenMPT (ADR 0060); IT's source
  is x86 assembly. Lost on both counts.
- **Write the tick players from the format specs alone** (ITTECH.TXT, the XM spec). The specs leave out much of what
  the trackers really do; every gap would be found by measurement.
- **Port OpenMPT's whole player and mixer.** One class serving every format, with its own mixer next to ours; the
  measured `ModRenderer` would be duplicated.
- **Schism Tracker's player** as the source. GPL: read only, never copied.
- **Name instruments instead of samples on the command line.** Closer to how XM/IT users think, but capacity and
  routing count samples, and it would break MOD's pattern.
- **One voice per channel for IT (no NNA).** Simpler, but songs written for NNA lose their sustained and overlapping
  notes; David chose to emulate it.
- **S3M as a stepping stone.** Adds the volume column and more channels, which XM covers too; not planned.
- **IT before MIDI.** David chose after.

## Consequences

- `docs/implementation-plan.md` gets **M14 — multichannel MOD**, **M15 — XM input** and **M16 — IT input** after M13,
  in that order; all three need M9's voice stealing. M14 could run alongside M13 if wanted, since it needs only M9.
- [README.md](../README.md) no longer lists XM/IT input as out of scope; S3M stays out.
- In the milestones' PRs: `docs/architecture.md` (new readers and tick players, `Playback` voices),
  `docs/formats/mod.md` (multichannel tags supported), new `docs/formats/xm.md` and `docs/formats/it.md`,
  `docs/cli.md` (input detection, sample numbering and range, usage errors, report sections for XM and IT),
  `docs/sample-capacity.md`, and AGENTS.md (required behavior item 1 becomes "the input's metadata and full
  sample and instrument tables", repository layout). ADR 0048 makes the same change for MIDI; whichever lands first
  writes it.
- `GenesisPlanPlayer`, `ModRenderer` and the mappers read voices instead of assuming one per song channel (T16.2).
- T15.3 adds the OpenMPT port's ADR and its `THIRD-PARTY-NOTICES.md` entry.
- Freely licensed (Public Domain or CC0) multichannel MOD, XM and IT files join the corpus, each with its checksum
  ([testing.md](../testing.md)); tests use modules built in code.
- Every lossy step the new formats bring (filter approximated, ping-pong unrolled, stereo mixed, random variation,
  dropped macros, voices stolen) becomes a conversion note ([ADR 0004](0004-conversion-notes-and-logging.md)).
