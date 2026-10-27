# Horner, Beauchamp & Haken (1993): genetic algorithms for FM matching synthesis

| | |
|---|---|
| **Citation** | Andrew Horner, James Beauchamp, Lippold Haken. "Machine Tongues XVI: Genetic Algorithms and Their Application to FM Matching Synthesis." *Computer Music Journal* 17(4):17–29, 1993. MIT Press. |
| **DOI** | [10.2307/3680541](https://doi.org/10.2307/3680541) |
| **Links** | [HKUST research portal](https://researchportal.hkust.edu.hk/en/publications/machine-tongues-xvi-genetic-algorithms-and-their-application-to-f/) · [ResearchGate](https://www.researchgate.net/publication/269879200_Machine_Tongues_XVI_Genetic_Algorithms_and_Their_Application_to_FM_Matching_Synthesis) · related thesis: A. Horner, *Spectral Matching of Musical Instrument Tones*, Ph.D., University of Illinois, 1993 ([IDEALS](https://www.ideals.illinois.edu/items/72254), restricted) |
| **Copyright** | MIT Press. Not copied into this repository. |
| **Access status** | **Full text not read.** Sources: the published abstract (HKUST portal), the related thesis abstract, and general knowledge of the method. Items marked *(unverified)* need checking against the paper. |
| **Used by** | Plan T8.2 (FM patch fitting); [fm-matching.md](fm-matching.md) |

## TL;DR for agents

FM parameters that reproduce a recorded harmonic tone can be found by a **genetic algorithm over the discrete,
multi-modal choices** (which frequencies the carriers and modulators sit on, and the modulation indices), with
the remaining amplitude parameters fitted by a fast deterministic method. The abstract reports the **best
results with time-invariant modulation indices and a multiple-carrier "formant FM" model**. For the YM2612:
search algorithm + MULT/DT with a GA or a pruned exhaustive search, and fit levels and envelopes locally.

## Problem

FM synthesis can produce rich spectra cheaply, but programming it to imitate a *specific* natural sound was
traditionally trial and error. The paper automates **FM matching synthesis**: given a recorded tone with
harmonic partials, find FM parameters whose output matches it.

## Method (from the abstract; details unverified)

- **Target representation:** the tone's harmonic partial amplitudes over time, from short-time harmonic
  analysis *(unverified: the analysis tool and frame rate)*.
- **FM model:** several carriers, each placed at a harmonic of the fundamental (near the spectral formant it
  should reproduce), modulated so that each carrier spreads energy over neighbouring harmonics. The abstract
  names this a **multiple-carrier formant FM** model.
- **Invariant modulation indices:** the indices are **fixed over time** rather than enveloped. The time
  variation of the sound comes from the carriers' amplitude envelopes. This was the best-performing
  configuration in the abstract.
- **Genetic algorithm:** evolves the discrete and hard-to-optimize choices, the carrier frequency multiples
  and the modulation indices.
- **Inner fit:** for each candidate, the carrier amplitude envelopes are computed by a fast deterministic
  method, such as a least-squares fit per analysis frame *(unverified)*. Fitness is then cheap to evaluate.
- **Fitness:** relative spectral error between the target's and the candidate's harmonic amplitudes,
  accumulated over the analysis frames *(unverified: the exact error formula)*.

## Results

The abstract reports that invariant modulation indices with a multiple-carrier formant model gave the best
matches, and that the paper discusses extensions and generalizations. Numbers are in the paper (not read).

## What applies to Mod2Xgm

| Paper idea | YM2612 equivalent | How we use it |
|---|---|---|
| Discrete frequency choices searched by a GA | **MULT** (0–15, 0 = ½) and **DT** (0–7) per operator; **8 algorithms** | GA or pruned exhaustive search over algorithm + MULT/DT (T8.2 step 1) |
| Fast inner fit of amplitudes | **TL** per operator, **FB**, envelope rates **AR/DR/D2R/RR**, **SL** | Local search on the integer grid inside each structure candidate (T8.2 step 2) |
| Invariant modulation indices | Modulator **TL** fixed during a note; the envelopes of carriers give time variation | Start with modulator envelopes near-flat (AR max, DR/D2R 0) and let carrier envelopes shape the note; relax only if the fit is poor |
| Multiple carriers at formant harmonics | Algorithms 4–7 have 2–4 carriers | Prefer multi-carrier algorithms for vowel-like or formant-heavy samples |
| Spectral error over frames | Our scoring on the time-varying spectrum | Fit against the whole note, not one averaged spectrum |
| Matching a set of tones (thesis) | Per-pitch-region patches | One patch per pitch region the MOD actually uses |

## Caveats

- The YM2612's envelopes and levels are coarse integers, and its sine output is quantized, so a perfect
  parameter fit can still differ audibly. Always select by rendering through the in-app YM2612 model.
- The paper matches **harmonic** tones. Percussive and noisy samples (drums, cymbals) are poor FM targets;
  those stay PCM or PSG noise.
