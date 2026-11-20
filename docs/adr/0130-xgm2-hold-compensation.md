# ADR 0130: Recordings don't make up for the driver's hold (XGM and XGM2)

- **Status:** Rejected
- **Date:** 2026-11-20

## Context

The DMF sanity check ([ADR 0129](0129-dmf-sanity-check.md)) puts DefleMask's own VGM writes through the XGM2 writer.
Its FM and PSG come back identical. Rendered alone, its recordings arrive whole, on time to within the ADR 0119 lead, and
as loud. But they are 1.3–3 dB quieter than DefleMask's DAC between 4 and 6.6 kHz (angel_island: −2.9 dB at 4–6.6 kHz,
acid_rain: −1.3 dB).

Two things take the treble:

- SGDK's XGM2 driver holds each byte on the DAC for one beat, at 13.3 kHz (XGM: 14 kHz). A hold at rate r turns a tone
  at f down by sinc(f / r): 0.4 dB at 2 kHz, 2.2 dB at 5 kHz, 3.1 dB at 6 kHz. DefleMask plays the same recordings at
  up to 32 kHz, where the loss stays under 0.4 dB.
- The resampler's anti-alias filter cuts at 0.95 of the driver rate's Nyquist (6.3 kHz). Nothing above that can be played
  at 13.3 kHz anyway.

The first is a fixed, known curve, so it could be undone ahead of time.

## Decision

Recordings are written as they are: resampled to the driver's rate and made 8-bit, with no treble lift.

The lift was built and tried, and is not kept:

- **What was tried.** After resampling and before 8-bit, each XGM and XGM2 recording went through a 7-tap symmetric FIR
  fitted by least squares to 1 / sinc(f / r) up to 0.45 r (within 0.17 dB, no delay), with taps
  [−0.009607249, 0.027261430, −0.099550900, 1.158723606, −0.099550900, 0.027261430, −0.009607249]. Where the lift would
  push a point past full scale, the point kept its unlifted value (without that guard, k0w-rsnd's pre-mixes clipped).
  The optimizer's PCM note renderer used it too.
- **Listening round r33** (David, 2026-11-20): five blind pairs, XGM2, the optimizer's plan with and without the lift
  (10kdub, drozerix_-_neon_techno, k0w-rsnd, kc-dancinonamiga, musix-cute-october, 0:30–0:50). "Can't tell" on all
  five: "Both are excellent conversions." David asked for whichever version turned out smaller.
- **Size.** Uncompressed, the files are byte for byte the same length (the lift changes values, not counts). Compressed
  (gzip −9, standing in for any packer a game might use), the unlifted file is smaller in all 10 of r33's songs, by
  0.4–4.5 %: more treble makes the bytes less predictable.

So the lift adds code and bytes for no difference anyone hears.

## Measurements

- **DMF sanity check, control path** (log-mel distance to DefleMask's VGM, lower is closer): with the lift, acid_rain
  2.21 → 2.01 dB, cyborgrider 1.61 → 1.52 dB, blue_electric 1.00 → 0.96 dB; songs with few samples unchanged.
- **r33 features** (10 songs, 20 s; `mel_db` to the MOD rendered by OpenMPT, lifted minus unlifted): −0.04, +0.12,
  +0.27, +0.27, +0.23, +0.24, +0.34, +0.40, +0.58, +0.35 dB. Against the MOD the lifted version measured *further* in 9
  of 10, likely because the lift also raised the hold's images above 6.6 kHz while most MOD samples have little real
  treble to restore. The two versions differed by 25–33 dB below the music (k0w-rsnd: 16 dB).

## Options considered

- **No lift (chosen).** What David couldn't tell apart, at the smaller size.
- The 7-tap lift described above. 3 taps miss by 0.7 dB at 6 kHz; 9 or 13 taps gain under 0.1 dB.
- A longer filter that also boosts above 0.45 r: the anti-alias filter has already taken that band, so there's nothing
  to lift.

## Consequences

- XGM and XGM2 recordings stay about 2–3 dB short of DefleMask's DAC at 5–6 kHz. The DMF sanity report keeps showing
  that gap on the control path; it is the driver's, not a writer defect.
- The taps above are kept here should a later round (brighter source material, a different driver rate) want the lift
  back.
