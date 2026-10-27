# Chen et al. (2022): Sound2Synth, FM synthesizer parameter estimation

| | |
|---|---|
| **Citation** | Zui Chen, Yansen Jing, Shengcheng Yuan, Yifei Xu, Jian Wu, Hang Zhao. "Sound2Synth: Interpreting Sound via FM Synthesizer Parameters Estimation." *Proceedings of IJCAI-22* (AI and Arts track), pp. 4921–4928, 2022. |
| **DOI** | [10.24963/ijcai.2022/682](https://doi.org/10.24963/ijcai.2022/682) |
| **Links** | [IJCAI page](https://www.ijcai.org/proceedings/2022/682) · [PDF](https://ijcai.org/proceedings/2022/0682.pdf) · [arXiv 2205.03043](https://arxiv.org/abs/2205.03043) · code: github.com/Sound2Synth |
| **Copyright** | IJCAI; arXiv license not confirmed. Not copied into this repository. |
| **Access status** | **Full text read** (IJCAI proceedings PDF). |
| **Used by** | Plan T8.2 (FM fitting), T6.2 (scoring metrics); [fm-matching.md](fm-matching.md) |

## TL;DR for agents

A neural network predicts the full patch of **Dexed** (an open-source 6-operator DX7) from audio. It treats every
parameter as **classification** over discrete bins and combines several audio representations. It reaches an
**MFCC distance of 5.36**, versus 31.32 for a plain genetic algorithm and 21.96 for hill climbing. Lessons for
Mod2Xgm: **fit per algorithm and keep the best by rendered audio**; YM2612 parameters are already discrete;
don't run a plain GA over every parameter; track **MFCC distance** as a metric.

## Problem

Given an audio clip, find the synthesizer parameters that reproduce its timbre (*synthesizer parameter
estimation*). Hard because FM's parameter-to-sound mapping is highly non-linear, and many parameters are
discrete.

## Setup

- **Synth:** Dexed, a DX7 clone with 6 operators and 32 algorithms.
- **Parameters:** 155 in total: 87 continuous, 66 discrete, and 2 fixed. The fixed ones are **Algorithm** (one
  fixed algorithm in the experiments) and **Output** (volume at maximum).
- **Note:** fixed input, C4 at full velocity, held 4 beats with 8 beats recorded at 120 bpm, 48 kHz / 32-bit.
- **Per-algorithm models:** the authors suggest training one model per algorithm and keeping the result with the
  lowest audio-space loss.

## Method

- **Parameters as classes:** each continuous parameter is discretized into **64 classes**, so all estimation is
  classification.
- **Gaussian label smoothing:** the one-hot target is blurred with a Gaussian over neighbouring classes, so
  near misses cost less than far misses.
- **Multi-modal inputs**, each with its own backbone, concatenated into 2048-d features:

  | Representation | Backbone |
  |---|---|
  | STFT spectrogram, mel spectrogram | VGG11-style CNN |
  | CQT chromagram | **Prime-Dilated Convolution (PDC)** |
  | MFCC | LSTM |
  | Statistics: amplitude envelope, RMS, zero-crossing rate, Wiener entropy | MLP |

- **Prime-Dilated Convolution:** on a log-frequency (CQT) axis, harmonics sit at log₂(k) offsets, which aren't
  evenly spaced. PDC uses dilations at integer approximations of B·log₂(p) for primes p, so stacking a few
  layers reaches any harmonic (every integer is a product of primes). Asymmetric and symmetric variants.
- **Heads:** a linear layer, then masked linear layers grouping features per operator or parameter; a small MLP
  per parameter.
- **Loss:** the true goal is audio similarity (MFCC distance), but they train on a **parameter-space**
  cross-entropy because audio-space losses misalign. Optional **gradient-inspired weighting** estimates each
  parameter's importance from the audio-loss gradient; it's expensive, so it was only used on small datasets.
- **Training:** AdamW (weight decay 1e-4), peak learning rate 2e-4 with warm-up and cosine annealing, up to 30
  epochs, virtual batch 64, Gaussian-noise augmentation, stochastic weight averaging.

## Data

- More than 100k presets were collected online; 6,191 were sampled for training.
- **Augmentation:** keep most of a preset's parameters and re-sample the rest uniformly; drop inaudible results.
- 1,678 random-walk patches were added to training and validation only.
- Totals: 30,106 training/validation points (80/20 split), 1,679 test points from held-out preset themes.

## Results (MFCC distance, lower is better; 6 operators)

| Method | MFCCD |
|---|---|
| Genetic algorithm | 31.32 |
| APVST LSTM++ | 22.59 |
| Hill climbing | 21.96 |
| PresetGen VAE (previous best) | 14.70 |
| Sound2Synth | 6.32 |
| Sound2Synth, multi-modal | **5.36** |

The paper cites roughly **10–15 MFCCD** as the range of human perceptual similarity. 13 and 40 MFCC bands gave
similar results.

## Limitations (stated or implied)

- One synth (Dexed), one fixed algorithm, one fixed note; output volume fixed.
- Parameter-space training only approximates audio similarity.
- Gradient weighting doesn't scale yet.
- Synths are **non-surjective**: some sounds can't be reproduced by any patch.

## What applies to Mod2Xgm

| Paper idea | Mod2Xgm decision |
|---|---|
| One model per algorithm; keep the best by audio loss | Fit each of the YM2612's **8 algorithms** separately and choose by rendered-audio score (T8.2) |
| Discretize continuous parameters | Not needed: every YM2612 operator field is already a small integer (TL 0–127, AR 0–31, MULT 0–15, …) |
| Label smoothing (near misses are better) | Local search on the integer grid; neighbouring values sound similar, so use neighbourhood moves |
| Plain GA scored worst | Don't run a GA over the full parameter vector; use GA or exhaustive search only for structure (algorithm, MULT/DT), then local search (see [horner-1993-ga-fm-matching.md](horner-1993-ga-fm-matching.md)) |
| Multi-modal features | Score with several representations: multi-resolution log-mel plus **MFCC distance** (T6.2) |
| ~10–15 MFCCD ≈ perceptually similar | A first rough threshold for "FM is close enough" before PCM is preferred; calibrate on our corpus |
| Learned estimator | **Not now**: needs a training set and a model inside a pure-C# AOT binary. A possible future seed generator, trained offline on rendered YM2612 patches |
| Fixed note C4 | We fit at the **pitches the MOD actually plays** (per pitch region) |
| Non-surjective synths | Some samples will never fit FM well; the optimizer must fall back to PCM/PSG |
