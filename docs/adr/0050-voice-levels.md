# ADR 0050: Voice levels: FM and PSG noise as loud as their samples

- **Status:** Accepted
- **Date:** 2026-10-30

## Context

While rating the T6.4 listening clips ([ADR 0049](0049-simple-mapper-baseline.md)), David heard the relative volume of
FM channels often far from the MOD's. The DMF writer set an FM note's volume from the MOD volume column alone
(`127 + 20·log10(v) / 0.75`). It never accounted for how loud the sample is, or how loud the library patch
([ADR 0040](0040-simple-mapper.md)) plays at full volume. The DAC plays the sample's own data, so it keeps the MOD's
level. A quiet sample on the Sine patch came out much louder than in the MOD, and a loud one on the Organ much quieter
(its four carriers sum to −16 dB). The PSG noise had the same problem: its instrument's loudest tick is always `F`.

## Decision

- **One scale for every voice: a full-scale sample on the DAC.** Furnace's render of the new `levels` probe
  (tests/golden/dmf-probes) measured it:

  | Voice at full volume | RMS (dBFS) |
  |---|---:|
  | FM: one TL-0 sine carrier, volume `7F` | −11.41 |
  | FM: the same at `77` (8 steps) | −17.29 (5.9 dB lower: 0.74 dB a step) |
  | DAC: full-scale 440 Hz sine | −11.44 |
  | PSG: square at `F` | −20.68 |
  | PSG: white noise at `F` | −20.95 |

  So an FM carrier at TL 0 is exactly a full-scale sample on the DAC, as on the chip, where the DAC replaces channel 6's
  output at the same scale. PSG noise at `F` has an RMS of 0.236 on that scale (`VoiceLevel.PsgNoise`).
- **The level of a sample** (`VoiceLevel.Sample`, full scale = 1): for a looped sample, the RMS of its loop (what a held
  note sounds like); for a one-shot, the RMS of its loudest 256-frame window (how hard it hits).
- **The level of an FM patch** (`VoiceLevel.Fm`) is worked out from its registers, not measured. Each carrier is a sine
  whatever modulates it, with amplitude 10^(−0.75·TL/20), and the carriers' powers add. A looped sample is compared
  with the patch's **sustained** level, where each decaying carrier has settled at its sustain level (3 dB per SL step).
  A one-shot is compared with its **attack**. The carriers' amplitudes added up give the patch's peak.
- **The FM volume is the MOD volume × g**, where g = the sample's level ÷ the patch's level. The same holds for volume
  slides, fine slides and volume-column changes. g is at most 1, or 1 ÷ the peak for a patch whose carriers would add up
  past full scale (Lead, Brass, Pluck), so the chip doesn't clip.
- **PSG noise likewise**: the noise volume is the MOD volume × g, where g = the sample's loudest level ÷ 0.236, at most 1.
- **A sample louder than its voice can play keeps its dynamics.** g is capped, not each note's volume, so its notes at full
  MOD volume play at the voice's loudest and softer ones stay softer. A conversion note says how many notes play quieter
  than in the MOD.
- The DAC is unchanged: it already plays the sample as stored.

## Options considered

- **Match by RMS, sustained against sustained and attack against attack** (chosen). Loudness follows RMS more than peak,
  and comparing like with like keeps a patch's own envelope (Brass's attack swell) as designed.
- **Measure each patch by rendering it** (the in-app YM2612 model, plan T7.2, or Furnace): more exact for heavily
  modulated or feedback patches, but neither is available in the converter yet. Working it out from the registers is
  exact for the carriers' amplitude, which modulation doesn't change.
- **Match peaks**: a sample's peak says little about how loud it sounds. A single spike would make a whole voice quiet.
- **Cap each note's volume instead of g**: every loud note then plays at the same level, flattening the accents of a loud
  hi-hat or lead.
- **Rewrite the library patches' carrier TLs so all patches are equally loud**: it fixes the patches but not the samples.
  The fitted patches of plan M8 replace the library anyway, and they need the same level match.
- **Leave PSG noise as it was**: the same imbalance on the noise channel. The measured level makes the fix the same.

## Consequences

- New `src/Mapping/VoiceLevel.cs`. `DmfPatternTranslator` applies g on every FM and noise volume it writes.
  [formats/dmf.md](../formats/dmf.md#effect-units) gives both formulas.
- New probe `levels.dmf` (tests/golden/dmf-probes) with a listening line in its README. The effect probes'
  (tests/golden/dmf-effect-probes) FM volumes changed.
- The PSG level is Furnace's. DefleMask's may differ, and on a real Genesis it varies by model. A DefleMask export of
  `levels.dmf` would check it.
- The fitted patches of plan M8 use the same match: `VoiceLevel.Fm` works on any `FmPatch`.
- Corpus result: see "Results" below.

## Results

2026-10-30, the corpus through the harness (Furnace, `--clip 30`), compared with the T6.4 baseline
([tests/golden/harness-baseline/](../../tests/golden/harness-baseline/README.md)):

- **Listening (David), blind A/B** on the 30-second clips: the original, then the old and new conversion in a hidden,
  shuffled order. The new levels sound closer on **11** songs and the old on **4**; **4** sound about the same.

  | Closer with the new levels | Closer with the old | About the same |
  |---|---|---|
  | `10kdub`, `cancan`, `drozerix_-_the_hawks_claw`, `fx-poly1`, `hit`, `jam_-_latenightcoffee`, `k0w-rsnd`, `kc-dancinonamiga`, `musix-cute-october`, `musix-omg-plz-help`, `musix-rubbish` | `drozerix_-_stardust_jam`, `jam_-_heatwave_cruise`, `jam_-_xlr8`, `sharpojects_cantpleaseeveryone` | `8bit_castle`, `drozerix_-_ai_renaissance`, `drozerix_-_neon_techno`, `soundtracker15` |

- **Scores**: average log-mel 11.07 → **10.98 dB** on whole songs, 10.85 → 10.76 dB on the clips. MFCC 67.87 → 69.00.
  Biggest moves on the clips: `k0w-rsnd` −1.9 dB, `musix-cute-october` −1.2, `cancan` −1.1; `kc-dancinonamiga`
  +2.6 and `8bit_castle` +0.9.
- Where the clip score moved by more than 0.2 dB and David heard a difference, they agree on 7 of 9 songs.
  `kc-dancinonamiga` scores much worse but sounds better, so the whole-mix score keeps missing balance
  ([ADR 0049](0049-simple-mapper-baseline.md)).
- Conversion notes: on most songs a few FM samples are louder than their patch can play (Lead, Brass and Pluck are
  capped below full volume so they don't clip). Those notes keep their dynamics but sit lower than in the MOD.
