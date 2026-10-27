# References: matching sounds with FM synthesis

Research on finding FM synthesizer parameters that reproduce a recorded sound. It underpins the FM-candidate
pass of the optimizer ([../multi-pass.md](../multi-pass.md#candidates)) and plan task **T8.2**.

> **Why no copies of the papers here:** all three are copyrighted and this repository is public. Each entry
> below gives the citation, links and a summary in our own words. If you need the full texts offline, keep
> them in a local, git-ignored folder (`docs/references/papers/` is ignored for that purpose).

---

## 1. Horner, Beauchamp & Haken (1993): genetic algorithms for FM matching

**Citation:** Andrew Horner, James Beauchamp, Lippold Haken. "Machine Tongues XVI: Genetic Algorithms and Their
Application to FM Matching Synthesis." *Computer Music Journal* 17(4), 1993. MIT Press.
Links: [ResearchGate](https://www.researchgate.net/publication/269879200_Machine_Tongues_XVI_Genetic_Algorithms_and_Their_Application_to_FM_Matching_Synthesis) ·
related thesis: A. Horner, *Spectral Matching of Musical Instrument Tones*, Ph.D., University of Illinois, 1993
([IDEALS](https://www.ideals.illinois.edu/items/72254)).

**Access note:** the full text was **not** available to us (ResearchGate refused the request; the thesis is
restricted). The summary below is from the abstract of the related thesis and general knowledge of the
method. Check it against the paper before relying on any detail.

**Summary**

- **Problem:** choose FM parameters (carrier/modulator frequency ratios, modulation indices, amplitude
  envelopes) so that an FM instrument reproduces the **time-varying harmonic spectrum** of a recorded tone,
  measured by short-time harmonic analysis.
- **Method:** a **genetic algorithm** searches the hard, discrete, multi-modal part of the space: the
  *frequency ratios* and modulation settings. The remaining amplitude parameters are fitted by a fast
  deterministic method (least squares) inside each fitness evaluation.
- **Fitness:** relative spectral error between the target's and the candidate's harmonic amplitudes, summed
  over analysis frames.
- **Outcome:** good matches with few carriers. Horner's thesis compares this with a principal-components
  wavetable approach and generalizes it to matching a *set* of tones from one instrument.

**What it means for Mod2Xgm**

- The YM2612's frequency-ratio parameters are already discrete: **MULT** 0–15 (0 = ½) and **DT** detune 0–7
  per operator, plus 8 **algorithms**. That is exactly the kind of space a GA, or exhaustive search guided by
  harmonic analysis, handles well.
- **Hybrid search:** the GA (or exhaustive search) picks algorithm + MULT/DT, and a fast local fit picks
  levels (TL, FB) and envelopes (AR, DR, SL, D2R, RR) for each candidate.
- **Fit the time-varying harmonic spectrum**, not one averaged spectrum. Sampled instruments change timbre
  over the note, and the YM2612's envelopes are what reproduce that.
- **Fit a set of tones:** the same idea as our per-pitch-region patches, where one patch serves the notes an
  instrument actually plays.

---

## 2. Chen et al. (2022): Sound2Synth

**Citation:** Zui Chen, Yansen Jing, Shengcheng Yuan, Yifei Xu, Jian Wu, Hang Zhao. "Sound2Synth: Interpreting
Sound via FM Synthesizer Parameters Estimation." *Proceedings of IJCAI-22* (AI and Arts track), pp. 4921–4928,
2022. DOI [10.24963/ijcai.2022/682](https://doi.org/10.24963/ijcai.2022/682).
Links: [arXiv 2205.03043](https://arxiv.org/abs/2205.03043) · [IJCAI](https://www.ijcai.org/proceedings/2022/682) ·
code: github.com/Sound2Synth.

**Summary**

- **Target:** Dexed (an open-source DX7: 6 operators, 32 algorithms). It estimates 155 parameters (87
  continuous, 66 discrete) from a fixed note (C4, full velocity). **The algorithm is fixed per model**; the
  authors suggest one model per algorithm and keeping the result with the lowest audio-space loss.
- **Parameters as classification:** continuous parameters are discretized into 64 classes. Training uses
  cross-entropy with **Gaussian label smoothing**, so near-miss classes cost less.
- **Multi-modal input:** STFT and mel spectrograms (VGG-style CNN), a CQT chromagram (their **Prime-Dilated
  Convolution**, whose dilations follow harmonic ratios on a log-frequency axis), MFCCs (LSTM) and summary
  statistics (envelope, RMS, zero-crossing rate, Wiener entropy).
- **Data:** about 6,200 real presets, augmented by re-sampling some parameters, plus random patches filtered for
  audibility; roughly 30,000 training examples.
- **Results (MFCC distance, lower is better):** hill climbing 21.96, **genetic algorithm 31.32**, earlier
  learned methods 14.7–22.6, Sound2Synth 6.32, multi-modal 5.36. The authors cite about 10–15 as the range
  of human perceptual similarity.
- **Limitations:** a single algorithm and a single fixed note were tested; training uses parameter-space
  losses, which only approximate audio similarity; a synth can't reproduce every sound.

**What it means for Mod2Xgm**

- **Search per algorithm, choose by rendered audio.** The YM2612 has only 8 algorithms, so fitting each one
  and keeping the best-sounding result is affordable.
- **Our parameters are already classes.** Every YM2612 operator field is a small integer, so the space is
  natively discrete and the 64-class trick isn't needed.
- **A plain GA over all parameters did worst** in their 6-operator comparison. That supports the hybrid
  approach: a GA or exhaustive search only over the structural choices (algorithm, ratios), plus local
  refinement, seeded from a patch library.
- **MFCC distance** is a cheap, widely used audio metric with a published rough perceptual threshold. It's
  worth adding to `src/Scoring` next to the multi-resolution log-mel distance, so the two can be compared on
  the corpus.
- **No neural network inside the converter for now:** a learned estimator needs a training set and a model
  shipped inside a pure-C# AOT binary. Keep this as a possible later step, trained offline on rendered
  YM2612 patches.

---

## 3. Caspe, McPherson & Sandler (2022): DDX7

**Citation:** Franco Caspe, Andrew McPherson, Mark Sandler. "DDX7: Differentiable FM Synthesis of Musical
Instrument Sounds." 2022 ([arXiv 2208.06169](https://arxiv.org/abs/2208.06169), ISMIR 2022).
Links: [project page](https://fcaspe.github.io/ddx7/) · code: github.com/fcaspe/ddx7.

**Summary**

- A small neural decoder (a temporal convolutional network, about 400k parameters versus 4.5M for the DDSP
  baseline) is conditioned on **pitch and loudness** frames. It drives the **envelopes** of a differentiable
  FM synth with only a few oscillators; a learnable reverb is also included.
- The **FM configuration and frequency ratios are fixed**; only envelopes are learned. Configurations of 6, 4
  (several topologies) and 2 operators were tested.
- **Design constraints matter:** a standard spectral loss gives little guidance on pitch, so the ratios are
  fixed and the maximum modulation index **I<sub>max</sub>** is bounded. A bad I<sub>max</sub> stops training
  from converging.
- **Results (Fréchet Audio Distance):** comparable to the DDSP baseline on violin, flute and trumpet. A
  **2-operator** model was enough for trumpet; violin and flute benefited from more operators. It runs in
  real time on a laptop CPU.
- **Limitations:** three instruments, excerpts only; I<sub>max</sub> and reverb behavior left to future work.

**What it means for Mod2Xgm**

- **Fix structure first, then fit envelopes:** choose algorithm and MULT/DT from harmonic analysis or search,
  then fit envelopes and levels. This maps directly onto the YM2612's per-operator envelope generator (AR, DR,
  SL, D2R, RR) and TL.
- **Bound the modulation depth:** constrain modulator TL (and feedback) to a sensible range during fitting,
  rather than letting the optimizer explore extreme modulation that ruins convergence.
- **Few operators can be enough:** a 2-operator fit may match some instruments. That also supports
  **CH3 special mode**, which runs two 2-operator voices on one channel (combined candidates in
  [../multi-pass.md](../multi-pass.md#candidates)).
- **Use pitch-conditioned analysis:** fit against the sample at the pitches it's actually played, the same idea
  as per-pitch-region patches.
- A differentiable YM2612 model isn't planned. The converter fits a *static patch* per instrument and pitch
  region (an FM chip patch can't be driven by a neural network at playback time), so derivative-free search
  is the right tool.

---

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
