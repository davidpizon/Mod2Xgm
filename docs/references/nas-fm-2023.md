# Ye et al. (2023): NAS-FM, searching for FM algorithms and ratios

| | |
|---|---|
| **Citation** | Zhen Ye, Wei Xue, Xu Tan, Qifeng Liu, Yike Guo. "NAS-FM: Neural Architecture Search for Tunable and Interpretable Sound Synthesis based on Frequency Modulation." *Proc. IJCAI 2023*. |
| **DOI** | [10.48550/arXiv.2305.12868](https://doi.org/10.48550/arXiv.2305.12868) |
| **Links** | [arXiv 2305.12868](https://arxiv.org/abs/2305.12868) · [audio samples](https://nas-fm.github.io) |
| **Copyright** | The authors; arXiv version under CC BY 4.0. Summarized in our own words; not copied. |
| **Access status** | Full text read: arXiv HTML for sections 1–5.5, and the PDF text for the search settings, section 5.6 and the conclusion. The training loss is not spelled out in the text we read *(unverified: presumably DDSP's multi-scale spectral loss)*. |
| **Used by** | Plan T8.2 (FM structure search) as background; [fm-matching.md](fm-matching.md) |

## TL;DR for agents

NAS-FM treats an FM synth as a graph of sine oscillators. It searches **which algorithm** (who modulates whom) and
**which integer frequency ratios** to use, with an evolutionary algorithm, scored by Fréchet Audio Distance. To
make scoring cheap, it trains one "supernet" of envelope predictors once, in which oscillators with the same layer
and ratio share an envelope. The search beat hand-picked DX7 patches (DDX7's) on all three instruments. It
matched, but did not beat, the best *fixed* algorithm per instrument. No fixed algorithm won everywhere. For us,
the lesson is to **search the algorithm, not just the ratios**, which our 8-algorithm structure search already does.

## Problem

Making an FM patch that sounds like a recording takes expert work: you choose an algorithm and ratios, then
envelopes. Earlier methods fix one of these:
- Horner's genetic algorithm fixes the algorithm;
- DDX7 learns only envelopes on a structure a human chose;
- preset predictors (Sound2Synth) target one synth's parameter set.

NAS-FM wants both structure and ratios found automatically from recordings, with the result still editable.

## Method

- **FM as a DAG:** an oscillator outputs envelope × sin(phase + the sum of its modulators' outputs). The output is
  the sum of the carriers. Each oscillator's ratio is its frequency over F0, from the integers 1…K; finer steps
  are possible but not tested. **Feedback FM is left out.**
- **Search space:**
  - a carrier layer plus modulator layers; two modulator layers are deemed enough;
  - an oscillator may be modulated only by the layer above it;
  - an unconnected oscillator is dropped.
- **Envelope sharing:** oscillators in the same layer with the same ratio share one envelope. So one trained
  network covers every algorithm in the space.
- **Supernet training:**
  - a 3-layer nested proxy (carrier ← modulator ← modulator), with ratios sampled at random;
  - envelopes are predicted from pitch (CREPE) and A-weighted loudness by a 4-block TCN, with one head per
    (layer, ratio): c = 15 carrier ratios and m = 5 modulator ratios;
  - amplitude caps: 1 for carriers, 2 for modulators;
  - Adam at 3e-4, decayed ×0.98 every 10k steps, 500k steps, batch 16; optional learned reverb (from DDSP).
- **Data:** URMP violin, flute and trumpet; 4 s clips at 16 kHz, frame 2048 / hop 64; split 75/12.5/12.5.
- **Search:**
  - an evolutionary algorithm over genomes holding each oscillator's ratio and connection: top-k selection, then
    crossover (P/2) and mutation (P/2);
  - fitness is FAD on validation clips;
  - genomes with duplicate fitness (dropped oscillators) are forced legal;
  - the best sub-network's weights are taken from the supernet with no fine-tuning.
  - Six-oscillator runs used population 1000 for 50 iterations. The 3-oscillator ablation used population 30
    for 20 iterations per method.

## Results

FAD, lower is better; distribution-level over the test set.

| Six oscillators | Flute | Violin | Trumpet |
|---|---|---|---|
| Test data vs itself (floor) | 1.180 | 0.308 | 0.554 |
| DDX7 with hand-retrieved patches (best of 10 seeds) | 7.841 | 3.497 | 4.442 |
| **NAS-FM** | **7.077** | **3.255** | **3.384** |

| Three oscillators, fixed algorithm vs search | Flute | Violin | Trumpet |
|---|---|---|---|
| Nested FM | 12.75 | **6.02** | **8.24** |
| Formant FM | 14.48 | 7.56 | 8.68 |
| Double FM | **11.16** | 7.27 | 9.91 |
| Single+ FM | 11.20 | 12.07 | 9.28 |
| NAS-FM | **11.16** | **6.02** | **8.24** |

- The search finds the best fixed algorithm for each instrument, and that algorithm differs per instrument.
- Morphing: changing one carrier ratio from 7 to 10 moves a formant from the 7th to the 10th harmonic.
- Timbre interpolation: a fitness of d<sub>v</sub> + d<sub>a</sub> + |d<sub>v</sub> − d<sub>a</sub>| gives a sound between violin and trumpet. It is shown only as spectrograms.

## Limitations

- Three instruments, monophonic; FAD is the only metric; no listening test.
- No feedback; integer ratios only.
- Needs a trained neural envelope model per instrument dataset. Envelopes are continuous per-frame curves, unlike
  the YM2612's ADSR.

## What applies to Mod2Xgm

| Paper idea | Mod2Xgm decision |
|---|---|
| Search the algorithm too; no single algorithm wins | Agrees with T8.2: the structure search tries **all 8 YM2612 algorithms** per sample region ([ADR 0069](../adr/0069-fm-patch-fitting.md)). Keep it; don't prune to a favourite algorithm. |
| Integer ratios from a small set | The YM2612 MULT (0.5, 1…15) is the same kind of discrete choice; the space is small enough to enumerate rather than evolve. |
| Evolutionary search with a cheap fitness | Only worth it if the structure search becomes too slow. Our fitness is a real render through the ymfm port, so a cheap proxy (scoring structures with a quick envelope before the full level fit) is the transferable idea. |
| Envelope sharing / supernet | **Not used.** It needs a neural runtime and training data, and the converter is pure C# under Native AOT with no ML runtime. YM2612 envelopes are a few integer rates, fitted directly. |
| Feedback left out | We can't drop it: FB on operator 1 is one of the YM2612's main timbre controls and stays in the fit. |
| FAD as fitness | **Not adopted.** FAD compares distributions over many clips. We score one sample against one render (log-mel distance, onset F1, loudness), which suits per-sample fitting. FAD could at most be a corpus-level check in the harness. |
| Ratio edits move formants predictably | Fitted patches stay editable in DefleMask: prefer simple patches when scores tie ([ddx7-2022.md](ddx7-2022.md)). |
