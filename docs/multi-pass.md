# Multi-pass conversion (optimizer)

> Status: **adopted** (2026-10-27). Built after the first DMF release (simple mapper, M5); becomes the default
> mapping once it beats the simple mapper on the accuracy harness. See [implementation-plan.md](implementation-plan.md).

## Idea

Instead of one fixed mapping, the converter searches for the combination of FM, PSG and PCM renditions that
**sounds closest to the MOD** (minus samples removed with `--ignore-samples`) while staying a **valid,
SGDK-playable** output. Every instrument gets several candidate renditions with a measured error and a
resource cost; an allocator chooses among them; the whole song is rendered in-app and compared with the MOD,
and the worst parts are refined over several passes.

PCM is the scarce resource (voices, slots, 128 KB budget), so FM/PSG are used wherever they are *close
enough* — freeing PCM for sounds FM can't imitate (drums, voices, breaks, orchestral hits). The allocator
decides by error and cost, not by a fixed "FM first" rule.

## Passes

| Pass | What happens |
|---|---|
| 1. Read & analyze song | Apply `--ignore-samples`; per sample: notes/pitches used, durations, volumes, trigger counts; per pair of instruments: co-onset rate and pitch interval (locked-pair detection); repeated overlaps of PCM notes, with offsets ([ADR 0052](adr/0052-pre-mixed-pcm.md)) |
| 2. Analyze samples | Tonal vs percussive, pitch stability, amplitude envelope, loop, spectral envelope per pitch region |
| 3. Generate candidates | Per instrument and pitch region: **fitted FM patch(es)**, **PSG** tone/noise, **PCM variants** (target rate, per pitch for XGM/XGM2), **combined** candidates (CH3 special for locked pairs, pre-mixed PCM for repeated overlaps). Each candidate gets an error score (rendered vs original, at the pitches actually played) and a cost |
| 4. Allocate | Assign candidates to channels over time, section by section, within the target's limits; greedy start, then local search; objective = importance-weighted total error |
| 5. Render & refine | Render the whole song in-app (Genesis model) and compare with the in-app MOD render; find the worst sections; re-fit patches or re-allocate there; repeat until no improvement or the time budget is spent |
| 6. Write | Emit DMF / XGM / XGM2; every substitution logged as a conversion note; report section 3 lists the final instruments |

## Candidates

- **FM patch fitting.** Hybrid search, following the research in
  [references/fm-matching.md](references/fm-matching.md):
  1. **Structure (discrete):** for each of the 8 algorithms, choose MULT/DT per operator by a genetic
     algorithm or by exhaustive search pruned with harmonic analysis.
  2. **Levels and envelopes:** with the structure fixed, fit TL, FB, AR/DR/SL/D2R/RR and SSG-EG by local search
     on the integer parameter grid, with modulator TL bounded, against the sample's **time-varying** harmonic
     spectrum.
  3. **Select** by rendered audio through the in-app YM2612 model.

  Every search is seeded from the nearest built-in library patch and is deterministic (`--seed`). A sample's
  timbre changes with pitch, so fit **per pitch region** (typically 1–3 patches per instrument).
- **PSG.** Square or noise approximations for simple tones and hi-hats/cymbals.
- **PCM variants.** Pre-resampled copies; costs slots, voices and bytes (see [sample-capacity.md](sample-capacity.md)).
- **Combined instruments**:
  - **CH3 special mode** (only for *locked* pairs — they start together at a fixed pitch interval, e.g.
    layered bass+kick, octave doubling, chord stabs): channel 3's four operators get independent frequencies → two
    2-operator voices on one channel. Allowed for **DMF** (system `0x42`, "Genesis ext. CH3") and **XGM2** (`$FA`/`$FB`, `$3x` b3).
    **XGM v1:** the format can express it, but `$27` also holds the YM timer controls — enabled only after a
    probe ROM confirms it plays correctly (T11.1).
  - **Pre-mixed PCM** ([ADR 0052](adr/0052-pre-mixed-pcm.md)): for any overlap of two or more PCM notes that
    repeats, including offset starts (a snare a tick after the kick) and partial overlaps (a kick over the start of a
    bass note). One sample holds the overlap's window; a sound that outlasts it continues as a **tail** of its own
    sample on the same voice. Windows are found from the note events and deduplicated by their rendered bytes. Saves
    a PCM voice per overlap, at the cost of the window's bytes and slot.

## Constraints the allocator enforces

**User constraints first:** samples in `--require-fm` only ever get FM candidates; samples in `--require-dac` only
PCM candidates; samples in `--require-psg` only PSG candidates. The allocator never trades them away. See
[sample-capacity.md](sample-capacity.md#required-fm-dac-and-psg-samples) for overflow handling.

| Resource | DMF | XGM | XGM2 |
|---|---|---|---|
| FM channels | 6 (5 if DAC used) | **5** (driver owns FM6/DAC) | 5 (FM6 only if DAC off via `$FD` — verify) |
| CH3 special | allowed (system 0x42) | after probe ROM (T11.1) | allowed |
| PSG | 3 tone + noise | same | same |
| PCM voices | 1 | 4 − `--reserve-sfx-channels` | 3 − `--reserve-sfx-channels` |
| PCM slots / bytes | 255 / — | 63 / 128 KB | 122 / 128 KB |

## In-app rendering (pure C#)

All in-process, no native libraries and no external programs:

- **MOD renderer** — mixer driven by the IR tick player that sounds like OpenMPT's default (8-tap sinc, 75/25 panning,
  volume ramps; [ADR 0054](adr/0054-mod-renderer.md)), not like a real Amiga. Validated against libopenmpt renders in
  the harness ([harness.md](harness.md#in-app-mod-renderer)).
- **YM2612 model** — C# port of **ymfm**'s YM3438, the clean chip without the ladder effect (`src/Render/Fm/`,
  [ADR 0056](adr/0056-ym3438-model.md); BSD-3, notice in `THIRD-PARTY-NOTICES.md`), with the chip's differences from
  ymfm modeled as Nuked-OPN2 has them ([ADR 0064](adr/0064-ym3438-chip-differences.md)).
- **SN76489 model** — C# port of **MAME**'s Sega PSG (`src/Render/Psg/`, [ADR 0057](adr/0057-sn76489-model.md);
  BSD-3, notice in `THIRD-PARTY-NOTICES.md`), with the chip's differences from MAME modeled as Nuked-PSG has them
  ([ADR 0065](adr/0065-sn76489-chip-differences.md)).
- **PCM driver models** — SGDK's XGM (4 ch, 14 093 Hz) and XGM2 (3 ch, 13 317 / 6 658 Hz) software mixing and
  DefleMask's DMF DAC, as the `$2A`/`$2B` bytes they put on the YM3438's DAC (`src/Render/Pcm/`,
  [ADR 0058](adr/0058-pcm-driver-models.md)), timed as SGDK's real drivers play them ([ADR 0066](adr/0066-pcm-drivers-checked-against-sgdk.md)).
- **Scoring** — the same `src/Scoring` library the harness uses (multi-resolution log-mel distance, onset error).

## Time and quality

Default conversions may take **minutes**. `--quality fast|normal|best` (default `normal`): `fast` = simple
mapper, no fitting; `normal` = full passes with a 5-minute budget; `best` = run until no improvement.
`--time-budget` overrides. Fitted patches are cached locally per sample hash (user cache directory,
`--no-cache` to disable) so reruns are fast. Progress is logged at `Information`.

## SGDK compliance

Every output must be playable through SGDK — see [AGENTS.md](../AGENTS.md#sgdk-compliance).
