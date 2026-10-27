# FM matching synthesis: what the research says for Mod2Xgm

The combined view of the FM-matching research. It underpins the FM-candidate pass of the optimizer
([../multi-pass.md](../multi-pass.md#candidates)) and plan task **T8.2**. Each paper has its own detailed
summary; read those for methods, numbers and caveats.

| Paper | One-line takeaway | Summary |
|---|---|---|
| Horner, Beauchamp & Haken 1993, *Computer Music Journal* 17(4) | A GA over the discrete frequency choices plus a fast inner amplitude fit matches harmonic tones; time-invariant modulation indices with multiple carriers worked best | [horner-1993-ga-fm-matching.md](horner-1993-ga-fm-matching.md) |
| Chen et al. 2022, *IJCAI* (Sound2Synth) | Learned DX7 patch estimation; fit per algorithm, parameters as classes; a plain GA over everything scored worst (MFCCD 31 vs 5) | [sound2synth-2022.md](sound2synth-2022.md) |
| Caspe, McPherson & Sandler 2022 (DDX7) | Fix structure and ratios, learn envelopes, bound the modulation index; 2 operators can be enough | [ddx7-2022.md](ddx7-2022.md) |

## Where they agree

- **Separate structure from levels.** Choose the discrete structure (algorithm, frequency ratios) by global
  search, then fit levels and envelopes locally. Horner does it with a GA plus an inner fit, DDX7 fixes the
  structure, and Sound2Synth trains one model per algorithm.
- **Bound the modulation.** Fixed indices (Horner) or a bounded I<sub>max</sub> (DDX7) keep the search sane.
- **Judge by the sound, over time.** Use a time-varying spectral error (Horner), MFCC distance (Sound2Synth)
  and multi-scale spectral loss (DDX7), never parameter distance alone.
- **Not everything fits FM.** Noisy, percussive or reverberant samples belong on PCM or PSG noise.

## Resulting design guidance for FM fitting (T8.2)

1. **Analyze** each sample per pitch region: fundamental, short-time harmonic amplitudes, envelope.
2. **Structure search (discrete):** for each of the 8 algorithms, choose MULT/DT per operator by GA or by
   exhaustive search pruned with the harmonic analysis (Horner; Sound2Synth's per-algorithm models).
3. **Level and envelope fit:** with the structure fixed, fit TL, FB and AR/DR/SL/D2R/RR by local search on the
   integer grid, with modulator TL bounded (DDX7's I<sub>max</sub>), against the time-varying harmonic spectrum
   (Horner).
4. **Select** the best candidate by rendered audio through the in-app YM2612 model: multi-resolution log-mel
   distance, with MFCC distance tracked alongside (Sound2Synth).
5. **Seed** every search from the nearest built-in library patch, and keep the search deterministic (`--seed`).
