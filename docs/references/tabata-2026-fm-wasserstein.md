# Tabata, Iwaya & Kondo (2026): FM frequency estimation on a Wasserstein loss landscape

| | |
|---|---|
| **Citation** | Ryoya Tabata, Masaki Iwaya, Kazunobu Kondo (Yamaha Corporation). "FM Parameter Estimation with Low-Order Rational Constraints on Wasserstein Loss Landscape." *Proc. 29th Int. Conf. on Digital Audio Effects (DAFx26)*, Cambridge, MA, USA, 2026, pp. 298–305. |
| **Links** | [DAFx26 paper 37 (PDF)](https://dafx.de/paper-archive/2026/papers/DAFx26_paper_37.pdf) |
| **Copyright** | © 2026 the authors, open access under CC BY 4.0. Summarized here in our own words; not copied. |
| **Access status** | Full text read (PDF text extracted, 8 pages). Figures were not seen; Table 2's layout was rebuilt from the extracted text and checked against the paper's own description. |
| **Used by** | Plan T8.2 (FM fitting) as background; [fm-matching.md](fm-matching.md) |

## TL;DR for agents

With a 1-D Wasserstein (earth mover's) distance between power spectra, the loss for 2-operator FM is smooth
*except* along lines of **low-order rational ratio** f<sub>m</sub>/f<sub>c</sub> (1/4, 2/7, 1/3, 2/5, 1/2, 2/3, 1, 2…).
Splitting the ratio range at those lines and running one bounded local search per piece finds the true
frequencies 96.7 % of the time, against 68 % for random starts. For the YM2612 the ratio comes from discrete
choices (a MULT per operator, plus a small DT detune), and our fitter already searches them discretely, so the
lessons are mostly for possible experiments: **W1 as a smoother distance for a local fit, and, if a continuous
refinement is ever tried, bounding it to one ratio cell.**

## Problem

Estimating FM *frequency* parameters (carrier f<sub>c</sub> and modulator f<sub>m</sub>) from a sound is hard,
because many settings give similar spectra. Pointwise spectral losses (spectral MSE) are full of local minima,
and they give almost no gradient unless the peaks already line up. Earlier differentiable work avoided this:
it fixed the ratio or took f<sub>c</sub> from a pitch tracker. This paper estimates f<sub>c</sub> and
f<sub>m</sub> together, with the modulation index β known within each run.

## Method

- **Model:** 2-operator FM, y(t) = A·sin(2πf<sub>c</sub>t + β·sin(2πf<sub>m</sub>t)). By the Jacobi–Anger expansion,
  the spectrum has lines at f<sub>c</sub> + n·f<sub>m</sub>, with weight |J<sub>n</sub>(β)|. Lower sidebands with
  negative frequency **fold** back to |f<sub>c</sub>(1 + nρ)|, where ρ = f<sub>m</sub>/f<sub>c</sub>.
- **Loss:** W1 between normalized power spectra (squared FFT magnitude), treated as 1-D probability
  distributions. In 1-D, W1 is the integral of |CDF<sub>a</sub> − CDF<sub>b</sub>|; for discrete spectra it is a sum over
  the merged sorted support points. It gives a useful gradient even when no peaks overlap. W2 gives the same
  landscape shape, but W1 was the more stable to optimize.
- **Where the ridges come from:** the loss is non-differentiable along rational ratios ρ = m/n, for two reasons:
  - *folding*: a folded sideband's frequency is V-shaped in ρ, with a cusp at ρ = 1/n;
  - *reordering*: two folded sidebands k<sub>1</sub> and k<sub>2</sub> swap order at ρ = 2/|k<sub>1</sub> + k<sub>2</sub>| (for
    example 2/3, 2/5 and 2/7), which changes the CDF.

  The ridge *positions* depend only on ρ, not on β. Their *strength* grows with β, because more sidebands carry
  energy (Carson's rule: about β + 1 of them). So the boundaries can be listed in advance, for the largest β
  expected.
- **Constraint:** within one cell (ℓ, u) between consecutive boundaries, ρ = ℓ + (u − ℓ)·sigmoid(a), and the
  optimizer works on the unbounded a. f<sub>m</sub> = ρ·f<sub>c</sub>, so the ratio never leaves its cell.
- **Multi-start:** one run per cell, each starting at the cell's midpoint, and the lowest W1 wins. With boundaries
  {1/4, 2/7, 1/3, 2/5, 1/2, 2/3, 1, 2} there are K = 9 cells, including below 1/4 and above 2.
- **Setup:**
  - 44.1 kHz, 65 536 samples (about 1.5 s), Hann window, FFT of 131 072 points (2× zero padding, about 0.34 Hz bins);
  - Adam, learning rate 0.02, 1500 iterations; f<sub>c</sub> and ρ optimized together, the 9 cells batched;
  - about 3.4 s per case on one GPU.
  - Search box: f<sub>c</sub> 30–2000 Hz, f<sub>m</sub> 20–1500 Hz. All sidebands stay below Nyquist, so only 0 Hz
    folding is tested, not aliasing.

## Results

- 90 cases, a full grid of:
  - f<sub>c</sub> ∈ {200, 400, 700} Hz;
  - ρ ∈ {1/4, 0.30, 1/3, 2/5, 1/2, 0.55, 2/3, 0.85, 1.5, 1.8}, which puts some cases on the ridges on purpose;
  - β ∈ {1, 2, 3}.
- Each case was run three times, so 270 trials per method.

| Method | Success (ρ error < 1 %) | ρ error, median / p90 | f<sub>c</sub> | f<sub>m</sub> |
|---|---|---|---|---|
| Constrained (whole run) | **96.7 %** | 0.11 / **0.64 %** | 0.10 / 0.42 % | 0.16 / 0.56 % |
| Init-only (constrained start, then free) | 80.0 % | 0.22 / 11.38 % | 0.08 / 7.48 % | 0.14 / 14.46 % |
| Random starts | 68.1 % | 0.25 / 44.99 % | 0.09 / 24.59 % | 0.17 / 24.95 % |

- The methods differ in their **worst cases**, not their medians. The constrained errors are bimodal: either under
  1 % or over 10 %.
- All 9 constrained failures were ρ = 1/3 at β = 3. There the true ratio sits *on* a boundary, so neither
  neighbouring cell contains it.
- Audio metrics, median / p90:

  | Metric | Constrained | Init-only | Random |
  |---|---|---|---|
  | log-mel MSE | 0.42 / 5.60 | 0.67 / 34.62 | 0.52 / 98.87 |
  | log spectral distance (dB) | 3.73 / 7.80 | 3.89 / 9.24 | 4.28 / 11.96 |

  The constrained method's median W1 is slightly *higher* (0.76, against 0.72 and 0.69). The sigmoid costs a
  little accuracy near a boundary.
- The constraint matters most at **high β** and **low ρ (< 0.5)**:
  - at β = 3, the p90 ρ error is 4.2 % constrained, 35.5 % init-only and 351 % random;
  - at β = 1, it is 0.3 %, 1.1 % and 2.0 %.

  At low ρ the cells are narrow: at f<sub>c</sub> = 400 Hz, the 1/4–2/7 cell spans only about 14 Hz of f<sub>m</sub>.

## Limitations

- Only 2-operator FM; synthetic FM targets only, no recorded sounds; no listening test.
- β is given, not estimated, and there are no envelopes. Amplitude and phase are outside the magnitude-only loss.
- A ratio exactly on a boundary can be missed. The authors suggest overlapping cells as a fix.
- Aliasing past Nyquist is not studied. A first look at 3-operator FM shows extra ridges at rational
  *combinations* of the two modulator ratios.
- GPU and PyTorch autodiff are used. The idea is method-level, though, and a CPU port is straightforward.

## What applies to Mod2Xgm

| Paper idea | Mod2Xgm decision |
|---|---|
| Ridges sit at low-order rational ratios, so search per cell, not across them | Already true by construction. The YM2612's MULT gives only ratios 0.5 and 1…15. The structure search enumerates a pruned set of them, with carriers at the region's strongest harmonics and modulators at 1–5 and 7 (T8.2, [ADR 0069](../adr/0069-fm-patch-fitting.md)). This confirms that a continuous local search **across** MULT values would be the wrong design. |
| DT as fine detune inside a cell | MULT is a per-operator multiplier, not a ratio cell: carrier MULT 3 with modulator MULT 1 sits on the ρ = 1/3 ridge, and DT can move the ratio to either side of it. The existing local search deliberately takes discrete MULT and DT steps ([ADR 0069](../adr/0069-fm-patch-fitting.md), `FmLocalSearch`), so the paper's result doesn't apply to it directly. It applies to a **bounded continuous W1 refinement** of the frequencies, which is an unadopted experiment: if tried, bound each run to one ratio cell. |
| W1 (spectral optimal transport) is smoother than pointwise spectral losses | **Candidate, not adopted.** It could serve as the ruler for the level/envelope local search, where log-mel distance may have flat or noisy areas. To try in the harness (`--fm-fit`) against log-mel before any change. Final selection stays by log-mel on rendered audio. |
| Constraint strength grows with β (modulation index) | High modulator levels (low modulator TL, high FB) make the loss more rugged. Another reason to bound modulator TL during fitting ([ddx7-2022.md](ddx7-2022.md)). |
| Folding at 0 Hz shapes the spectrum | Low MULT on the carrier with strong modulation folds lower sidebands. The ymfm port renders this exactly, so render-and-score already accounts for it. No analytic model needed. |
| Boundary cases fail | The paper's failures happen exactly on rational cell boundaries. Our discrete search doesn't cover this by itself: it prunes MULT choices (`FmExhaustiveSearch.ModulatorMults` has only 1–5 and 7), so it doesn't try every neighbour. Overlapping cells, as the authors suggest, belong to the continuous W1 experiment, if it is ever tried. |
| f<sub>c</sub> estimated jointly, without a pitch tracker | Not needed. MOD notes give the pitch exactly (period → frequency), and the analysis pass measures the sample's own cycle. |
