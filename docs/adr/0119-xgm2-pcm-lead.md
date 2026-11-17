# ADR 0119: XGM2 sends PCM commands 2 frames ahead, after a silent head

- **Status:** Accepted
- **Date:** 2026-11-17
- **Revisit:** whenever SGDK's XGM2 driver (`drv_xgm2.s80`) changes. See [When to revisit](#when-to-revisit).

## Context

SGDK's XGM2 Z80 driver, measured on an emulated Z80 ([ADR 0066](0066-pcm-drivers-checked-against-sgdk.md), SGDK
commit `9a78a44`), does two things to every PCM play:

1. **It is heard late.** It mixes ahead into a ring buffer, so a play or a stop is heard 443 beats (about 34 ms;
   1.68 PAL frames, 2.0 NTSC frames) after the frame that holds it. SGDK's xgm2tool doesn't move PCM commands ahead
   (xgmtool does, for XGM v1).
2. **Its first 64 beats play twice.** The first buffer fill after a play sees the play's "parameters changed" flag and
   drops its own advance (`updatePCM` in `drv_xgm2_pcm_mac.i80`), so the first 64 bytes (32 at half speed) sound, then
   sound again.

ADR 0066 left it to the XGM2 writer to decide what to do about this, and the writer (T10.1, [ADR 0099](0099-xgm2-writer-records-the-player.md))
did nothing. In `brun_jul` every sleigh-bell hit came in about 40 ms late, with its sharp strike smeared by the
repeat. What was left of each hit, four times a second, sounded like a buzz. Every drum in every XGM2 file had the same
problem. David chose to fix both (2026-11-17, plan T10.1b).

## Decision

- **Lead.** The XGM2 writer sends every PCM play and stop `LeadFrames` whole frames ahead of its own:
  `floor((443 + 2 × 64) / beats per frame)`. That is as many as fit in the start delay and the padded, repeated head
  together, so a sound is never heard before its frame. It is **2 frames on both PAL and NTSC**. The first beat of the
  real sound is then heard 43 beats (3.3 ms) after its frame on PAL and 127 beats (9.5 ms) after it on NTSC, where it
  was 443 beats (34 ms) plus the smeared repeat. A stop is heard 85 beats (6.4 ms) before its frame on PAL, and 1 beat
  before it on NTSC.
  - Plays and stops move by the same amount, so their order on a channel never changes.
  - A command in the song's first 2 frames can't go before frame 0. It is sent there, in order, and is heard up to
    2 frames late, with a conversion note.
  - On a song that loops back to frame *L*, a command in frames *L* to *L* + 1 is sent twice: 2 frames earlier, which plays only the first time
    through, and in the last 2 frames of the song, 2 frames before the loop comes round to it, for every time after.
- **Silent head.** Every XGM2 recording is stored after 64 silent bytes (32 at half speed): the part the driver plays
  twice is silence, and the sound starts once. This costs 64 bytes a recording before the 256-byte alignment, often
  nothing (`brun_jul`'s five recordings take 307 200 bytes with or without it). Capacity counts it
  (`PcmTargets.Bytes`).
- **The render follows the file.** `Xgm2PcmDriver` takes the lead in beats (`GenesisChips` sets it from the region)
  and plays a command as if it had come that much earlier. When that is already past, the play starts at once, as far
  in as it would be by then. The silence makes that exact. The render plays the silent head as the file does.
- The optimizer's PCM note renderer (`PcmNoteRenderer`) keeps the driver without a lead, as before. Its scores compare
  copies with each other, and every copy has the same lead.

## Options considered

- **Chosen: lead and silent head.** On time within 3.3 ms (PAL) or 9.5 ms (NTSC), with clean attacks.
- **Lead only.** On time, but every attack still stutters for 4.8 ms.
- **Silent head only.** Clean, but 4.8 ms later still.
- **3 frames of lead on NTSC.** The sound would be 95 beats (7.1 ms) early, before its FM and PSG. A late drum is
  heard as part of the beat sooner than an early one, so the lead is rounded down.
- **Leave it, as a known limitation.** Every XGM2 drum and recording late and smeared.

## Consequences

- XGM2 files and their `--render-wav` renders play recordings on time with clean attacks. On `brun_jul`, the bell
  strike lands within 5 ms of the original's (it was 40 ms late, peaking 60 ms in).
- One more conversion note when a recording starts in the song's first 2 frames.
- The XGM (v1) writer, when it comes (M11), follows xgmtool's own 3-frame (NTSC) / 2-frame (PAL) shift instead
  ([formats/xgm.md](../formats/xgm.md#driver-behavior-measured)).

## When to revisit

The lead and the head both come from measurements of one driver build. Re-measure, and change or drop them, when SGDK's
XGM2 driver changes:

1. Bump `SGDK_COMMIT` in `tools/pcm-driver-reference/run.sh` and run it ([its README](../../tools/pcm-driver-reference/README.md)).
2. `PcmDriverProbeTests` compare the model with the new captures. A failure in the start delay or the repeated head
   means `Xgm2PcmDriver.StartDelayBeats` or `RepeatedBeats` has moved, and with it `LeadFrames` and `HeadPadBytes`.
3. If SGDK's xgm2tool starts moving PCM commands ahead itself, or the driver stops repeating its first fill, drop that
   part here with a new ADR that supersedes this one.
