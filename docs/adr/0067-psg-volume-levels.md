# ADR 0067: The PSG plays Nuked-PSG's volume levels, and the converter picks PSG volumes from them (T7.8)

- **Status:** Accepted; supersedes decision 1 (the volume levels) of [ADR 0065](0065-sn76489-chip-differences.md)
- **Date:** 2026-10-31

## Context

[ADR 0065](0065-sn76489-chip-differences.md) (T7.8) moved the PSG model's counters and noise to the chip, as Nuked-PSG
has them, and kept MAME's volume levels for David to decide (difference 1, "measure first, decide later"). MAME's
levels are the TI datasheet's even 2 dB steps. Nuked-PSG, a model of the YM7101 VDP's PSG made from the die, falls off
faster: 2.2 dB at the loud end, 6.5 dB between volumes 13 and 14, so volume 14 is 41 dB below the loudest where MAME has
28 dB. Neither Nuked-PSG's source nor its README says where its table comes from, and no hardware measurement of the
Genesis's levels turned up. ADR 0065 measured that swapping in Nuked's table alone made the corpus 0.02 dB worse, because
the DMF's noise volumes were still chosen for 2 dB steps, and noted that taking the table would mean choosing PSG volumes
from it too.

The converter's goal is XGM and XGM2 played on a real Genesis (David); the DMF follows where DefleMask allows.

## Decision

- **The PSG model plays Nuked-PSG's levels** (David: "Nuked's (from the chip)"). `src/Render/Psg/PsgLevels.cs` holds
  them, and `Sn76489` takes its volume table from there:

  | Volume | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 |
  |---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
  | Level | 8191 | 6324 | 5095 | 3973 | 3129 | 2375 | 1875 | 1425 | 1081 | 786 | 589 | 417 | 278 | 155 | 73 | 0 |
  | dB | 0 | −2.2 | −4.1 | −6.3 | −8.4 | −10.7 | −12.8 | −15.2 | −17.6 | −20.4 | −22.9 | −25.9 | −29.4 | −34.4 | −41.0 | off |
  | MAME dB | 0 | −2 | −4 | −6 | −8 | −10 | −12 | −14 | −16 | −18 | −20 | −22 | −24 | −26 | −28 | off |

- **Measured, not read** (David). The levels are the sixteen values Nuked-PSG plays in the `volumes` probe
  (`tests/golden/sn76489/volumes.nuked.gz`, its output × 8192 as the reference tool records it), with the loudest at
  MAME's 8191 so the chip's full scale and the levels calibrated on it (ADR 0050) don't move. `PsgLevelsTests` checks
  the table against that recording. No GPL code was read or copied for it. Where Nuked's table comes from stays
  **Verify**: a real Mega Drive's levels would settle it.
- **PSG volumes are picked from the levels.** A wanted level gets the volume whose level is nearest in decibels; below
  half the quietest level (−47 dB) it is off. `PsgLevels.Attenuation` gives the unrounded volume, even in decibels
  between two levels, for slides and tremolo depths. The Genesis renderer picks each tick's noise volume this way, as
  the XGM and XGM2 writers will (M10, M11): they write the chip's volume directly.
- **The DMF picks its volume column, slides, tremolo depths and noise fades the same way.** DefleMask adds a noise
  instrument's volume macro to the channel volume in steps (Furnace: `VOL_SCALE_LOG`), and the macro is chosen for a
  note at full volume. Below full volume the steps add up to more decibels than the fade has, so the fade's quiet end
  comes out quieter than the sample's: a conversion note says how many noise notes that is.

## Options considered

- **Nuked's levels, PSG volumes picked from them** (chosen): the model sounds like the die-based model of the
  Genesis's own PSG, and the converter picks the levels that model plays.
- **Nuked's levels in the model only**: measured in ADR 0065: 0.02 dB worse on the corpus, since every volume would
  still be chosen for 2 dB steps.
- **Keep MAME's levels**: no change and no doubt about the source, but MAME's are the TI datasheet's ideal steps, and
  quiet PSG notes and fades would play up to 13 dB louder than on the chip if Nuked is right.
- **Read the table from Nuked-PSG's source**: the same numbers, but by reading GPL code; the measurement needs none.

## Consequences

- The port no longer matches MAME sample for sample on any probe that plays a volume other than 0 and 15: `chord`,
  `noise-restart`, `periods`, `volumes` and `write-quirks`. `Sn76489ProbeTests.ChipDifferences` lists them under the
  volume levels, so they must come closer to Nuked-PSG than MAME does. `tone`, `white-noise`, `periodic-noise` and
  `power-on-latch` still equal MAME.
- `volumes` and `noise-restart` lose their own bars and meet the default 0.3 dB; `chord`'s bar drops from 0.9 to
  0.7 dB, for the reference tool's one-sample offset that remains (ADR 0065).
- `VolumesPlayMamesLevelsNegated` became `VolumesPlayNukedsLevelsNegated`. The DMF effect probe `noise` was written
  again (its fade ends more gently) and its Furnace render remade; its DefleMask export is still the old file's.
- Furnace and DefleMask may play the DMF's PSG at even 2 dB steps; the DMF is chosen for the chip.
- Docs: [genesis-audio.md](../genesis-audio.md), [formats/dmf.md](../formats/dmf.md#effect-units),
  `tests/golden/sn76489/README.md`, `THIRD-PARTY-NOTICES.md`, ADR 0065's Status line.

## Results

### The probes

Each probe against Nuked-PSG, log-mel distance after averaging every four samples, as the tests measure it:

| Probe | MAME's levels dB (ADR 0065) | Nuked's levels dB |
|---|---|---|
| tone | 0.03 | 0.03 |
| volumes | 2.07 | 0.010 |
| periods | 0.10 | 0.008 |
| chord | 0.72 | 0.64 |
| white-noise | 0.005 | 0.005 |
| periodic-noise | 0.01 | 0.011 |
| noise-restart | 1.90 | 0.003 |
| write-quirks | 0.16 | 0.019 |
| power-on-latch | 9.98 | 9.98 |

### On the corpus

The harness on the 19 corpus songs (2026-10-31, libopenmpt 0.8.9, Furnace from its Windows install, no DefleMask
column), whole-song log-mel distance from libopenmpt, `main` → this change:

| Song | furnace | genesis |
|---|---|---|
| `cancan` | 10.18 → 10.15 | 10.54 → 10.53 |
| `drozerix_-_the_hawks_claw` | 14.04 → 14.03 | 11.49 → 11.48 |
| `hit` | 9.71 → 9.69 | 10.35 → 10.33 |
| `k0w-rsnd` | 8.56 → 8.57 | 8.62 (same) |
| `musix-cute-october` | 8.97 → 8.94 | 9.85 (same) |
| the other 14 | within 0.005 | within 0.005 |
| **Average** | 10.912 → 10.908 | 11.535 → 11.533 |

Where ADR 0065 found Nuked's table alone 0.02 dB worse on `genesis`, picking the volumes from it makes both columns a
little better; no song moves by more than 0.03 dB, and onset F1 moves by about 0.01 at most (`cancan`, `musix-cute-october`
up in Furnace). As in ADR 0065, the conversion's own differences dwarf the chip's: the PSG carries only noise drums in
the simple mapper. In the XGM2 renders of `drozerix_-_the_hawks_claw` and `musix-cute-october` the difference between
before and after is 44 and 37 dB below the mix.
