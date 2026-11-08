# jsgroth (2025): Emulating the YM2612 (blog series, 7 parts)

| | |
|---|---|
| **Citation** | jsgroth. "Emulating the YM2612", parts 1–7: Interface, Phase, Envelopes, Digital Output, Analog Output, LFO, SSG-EG. *jsgroth's blog*, 24 March – 14 April 2025. |
| **Links** | [Part 1](https://jsgroth.dev/blog/posts/emulating-ym2612-part-1/) · [2](https://jsgroth.dev/blog/posts/emulating-ym2612-part-2/) · [3](https://jsgroth.dev/blog/posts/emulating-ym2612-part-3/) · [4](https://jsgroth.dev/blog/posts/emulating-ym2612-part-4/) · [5](https://jsgroth.dev/blog/posts/emulating-ym2612-part-5/) · [6](https://jsgroth.dev/blog/posts/emulating-ym2612-part-6/) · [7](https://jsgroth.dev/blog/posts/emulating-ym2612-part-7/) |
| **Copyright** | The author, under CC BY-NC-SA 4.0. Summarized in our own words; no text or code copied. |
| **Access status** | Read in the in-app browser, since the site's bot check blocks plain fetches. Parts 1 (interface, DAC channel) and 5 (analog output) read in full. Parts 2–4, 6 and 7 seen only as section lists, not summarized. The author's main source is Nemesis's posts in the SpritesMind "New Documentation: An authoritative reference on the YM2612" thread. |
| **Used by** | Genesis renderer and DAC model ([ADR 0056](../adr/0056-ym3438-model.md), [ADR 0059](../adr/0059-genesis-renderer.md)); [../genesis-audio.md](../genesis-audio.md) |

## TL;DR for agents

A practical, well-sourced description of the YM2612 for emulator writers. For us, the useful parts are the
**output stage**:
- the 9-bit DAC truncates each **carrier** (not each channel) from 14 to 9 bits;
- the discrete YM2612's **ladder effect** adds a fixed step around zero, and also on a channel muted by panning,
  which leaks noise into the "off" speaker;
- the Genesis's own **analog low-pass** is first-order at 3.39 kHz or 2.84 kHz on the best-sounding Model 1
  boards, and is often what makes a capture sound "right".

Our model is the clean YM3438 with no output filter, by David's choice. This page says what that leaves out and
where it could matter.

## Problem

The YM2612 is simple in concept but has many exact details that change how games sound, and Sega's own
documentation is wrong in places. The series explains the chip at a low level. It is not cycle-accurate.

## Method (what the posts describe)

**Clock and rate (part 1)**
- The chip runs on the 68000 clock: NTSC 53 693 175 / 7 Hz, about 7.67 MHz; PAL 53 203 424 / 7.
- It divides that by 6 internally, and makes one full sample every 24 internal cycles: about **53 267 Hz** on NTSC.
- In hardware it cycles through the 6 channels, one every 4 cycles, and **multiplexes** them through the DAC
  rather than mixing them. Emulators usually mix.

**Interface (part 1)**
- Four write ports at Z80 `$4000–$4003`, mirrored through `$5FFF`.
- In reality there is one data port: the chip remembers whether `$4000` (group 1: channels 1–3 and the global
  registers) or `$4002` (group 2: channels 4–6) was written last.
- Register ranges: `$20–$2F` global, `$30–$9F` per operator, `$A0–$BF` per channel. The low 2 bits pick the
  channel, and 3 is invalid.
- The operator index is address bits 2–3 **bit-swapped**: 00 → op 1, 01 → op 3, 10 → op 2, 11 → op 4. The YM2608
  manual has this right and Sega's documentation has it wrong.
- The read port holds busy (bit 7) and the timer B / A overflow flags (bits 1 / 0). The author keeps busy set for
  32 internal cycles after a data write.
- The discrete YM2612 and the YM3438 differ in how `$4001–$4003` reads mirror `$4000`. Two games depend on
  opposite behaviours (Earthworm Jim and Hellfire).

**DAC channel (part 1)**
- `$2B` bit 7 replaces channel 6's output with the unsigned 8-bit value in `$2A`, made signed by subtracting 128.
- It is scaled to the FM's 14-bit range (× 64), so the 9-bit DAC's lowest bit can't be set from the DAC channel.
- Channel 6's L/R pan bits in `$B6` still apply. Nothing else of channel 6's settings does.
- There is no FIFO, so samples must be written at the playback rate. This is why DAC output often sounds crunchy.

**Panning (part 5)**
- Pan bits are `$B4–$B6`: bit 7 is L and bit 6 is R. A side is either fully on or fully off.
- They must power up as 1; some games never set them.
- Model 1 is stereo only on the headphone jack; Model 2 is stereo; Model 3 is mono.

**DAC quantization (part 5)**
- Operators produce signed 14-bit samples, and the DAC takes 9 bits, so the lowest 5 bits are dropped.
- The truncation happens per **carrier**: carriers are summed in a 9-bit accumulator, clamped to −256…255. This
  only makes a difference for the multi-carrier algorithms 4–7.
- Leaving it out makes output too clean; the noise shows in quiet fades.

**Ladder effect (part 5; discrete YM2612 only, not YM3438)**
- The DAC is about linear from −256 to −1 and from 0 to 255, with a large jump between −1 and 0.
- Nuked-OPN2's model, which most emulators now use:
  - each channel gets 4 output slots: the sample, then three "silent" slots;
  - the silent slots output ±1 by the sample's sign, with the −1/0 gap doubled.
- Non-cycle-accurate emulators fold this into one rule, in 9-bit units:
  - an audible channel adds **+4** to a non-negative sample and **−3** to a negative one;
  - a channel **muted by panning** outputs a fixed **±4** by its sign.

  So the gap around zero is 8× a linear step, a quiet wave sounds louder and noisier, and a hard-panned channel
  leaks a sign-dependent buzz into the muted side.
- Some games are written around this effect (Streets of Rage, After Burner II).

**Analog low-pass (part 5)**
- The filter is part of the console, not the chip, and it applies to the FM and the PSG alike. It softens PSG
  noise strongly.
- First-order 3.39 kHz or 2.84 kHz matches the best-regarded Model 1 VA0–VA6 boards. That is only about 20 dB
  down at 20 kHz, but it emphasizes bass.
- Later revisions have a second-order filter that sounds more muffled.
- The author's own judgement: the filter makes the biggest difference of the three effects, most of the time.

**Not summarized here:** phase (part 2), envelopes (part 3), digital output (part 4), LFO and timers (part 6),
SSG-EG (part 7). Our port is checked against ymfm and Nuked-OPN2 directly
([ADR 0056](../adr/0056-ym3438-model.md)), which are better references for those details than a summary.

## Results

A blog, so there are no measurements. The author's A/B recordings (Castlevania Bloodlines, Streets of Rage 2,
Sonic 2, Gunstar Heroes) show each effect by ear.

## Limitations

- Not cycle-accurate. The author says their own implementation had bugs found while writing.
- Hardware differs per console revision, so there is no one right answer for the filter or the ladder.
- Read-port and busy-flag details don't matter to us: we write files, not register streams at run time.

## What applies to Mod2Xgm

| Blog point | Mod2Xgm decision |
|---|---|
| 9-bit truncation per carrier, clamped after each | Already modelled: `Ym3438` shifts each carrier >> 5 and clips at −256…255 after every carrier (`src/Render/Fm/Ym3438.cs`). Consistent; nothing to change. |
| DAC value × 64 into the 14-bit range; channel 6 pan applies | Matches the DAC model ([ADR 0058](../adr/0058-pcm-driver-models.md)): an 8-bit DAC value is the 9-bit DAC's bits 8–1. |
| Ladder effect: +4/−3, and muted pan → ±4 | **Not modelled, by decision** ([ADR 0056](../adr/0056-ym3438-model.md), [ADR 0059](../adr/0059-genesis-renderer.md)). But it explains genesis-audio.md's "stereo playback can leak faint duplicates": on a Model 1, a **hard-panned** channel (the DAC included) buzzes faintly in the muted speaker. It also adds weight to "keep PCM loud": quiet DAC notes sit near the 8-step jump. A ladder option for the renderer would be a new ADR if David wants Model 1 listening. |
| First-order analog low-pass (3.39 / 2.84 kHz), applies to the PSG too | **Open idea**, matching [ADR 0071](../adr/0071-pcm-candidates.md)'s "an output filter for the ruler". These are concrete values to try. The harness could score with and without it. It would make harsh PSG noise and DAC aliasing count for less. To decide by ear before adopting. |
| About 53 267 Hz native rate (NTSC) | Our renderer's YM clock is from the same master clock. Nothing new. |
| Operator index bit-swap (01 → op 3) | Relevant to any register writer (XGM / XGM2 register streams). The writers follow SGDK and the YM2608 order. Worth one round-trip test per operator slot if one doesn't already exist. |
| Model 3 is mono; Model 1's TV output is mono | Panning choices can't rely on stereo. Another reason to avoid sound that depends on side jumps (plan T9.4c). |
