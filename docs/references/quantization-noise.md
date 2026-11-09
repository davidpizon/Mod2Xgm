# Quantization noise: the textbook model, and what it means for 8-bit PCM

| | |
|---|---|
| **Source** | ScienceDirect Topics, ["Quantization Noise Power"](https://www.sciencedirect.com/topics/engineering/quantization-noise-power): excerpts from Elsevier books (below), not one paper. |
| **Books excerpted** | Randall & Tordon, "Data Acquisition", *Encyclopedia of Vibration* (2001) · Ortmanns et al., *Comprehensive Microsystems* vol. 1 (2008) · Tan & Jiang, *Digital Signal Processing*, 2nd ed., ch. 12 (2013) · Garg & Wang, *The Electrical Engineering Handbook* (2005) · Rouphael, *RF and Digital Signal Processing for Software-Defined Radio*, ch. 8 (2009) · Elliott, *Handbook of Digital Signal Processing* (1987) · Demler, *High-Speed Analog-to-Digital Conversion* (1991) · Lai, *Practical Digital Signal Processing*, ch. 2 (2003) · Garg, *Wireless Communications & Networking* (2007) |
| **Copyright** | Elsevier and the authors. Summarized in our own words; standard textbook formulas restated, nothing copied. |
| **Access status** | The page's excerpts read in full in the in-app browser (David signed in). The full chapters were not read. |
| **Used by** | PCM sample processing and loudness ([ADR 0038](../adr/0038-sample-processor.md), [ADR 0052](../adr/0052-pre-mixed-pcm.md)); the DAC note in [../genesis-audio.md](../genesis-audio.md) |

## TL;DR for agents

- Rounding to a step Δ adds an error of power **Δ²/12**, which is **fixed by the bit depth**, not by the signal.
- A full-scale sine at N bits has **SQNR ≈ 6.02·N + 1.76 dB**, which is about **49.9 dB at 8 bits**.
- Every dB a sound is turned down *before* storage is a dB of SQNR lost. So quiet PCM is noisy PCM, which is why
  we keep recordings loud.
- Quantizing once is better than quantizing parts and adding them: noise powers add.
- At very low levels the error stops being noise and becomes **harmonic distortion**. Dither turns it back into
  noise, at the price of more noise power.

## The model

- **Uniform error:**
  - rounding to the nearest level leaves an error e in (−Δ/2, Δ/2);
  - modelled as uniform, zero-mean, white and uncorrelated with the signal, its power is Δ²/12 (RMS Δ/√12);
  - truncating instead leaves e in [0, Δ): the same spread, but a mean of Δ/2 (a bias).
- **Step:** Δ = (full range) / 2<sup>N</sup>, so the noise power = A²·2<sup>−2N</sup>/12. Each extra bit is about 6 dB.
- **SQNR:**
  - against the peak: 20·log<sub>10</sub>(2<sup>N</sup>) ≈ 6·N dB, the "dynamic range" form (16 bits gives 96 dB);
  - for a full-scale sine: 6.02·N + 1.76 dB. The books tabulate 31.8, 37.8, 43.8 and 49.8 dB for 5–8 bits;
  - in general: SQNR = 4.77 + 6.02·N − PAR (dB), where PAR is the signal's peak-to-average power ratio. A peaky
    signal gets less.
- **Where the noise goes:** sampled at f<sub>s</sub>, the noise spreads evenly over 0…f<sub>s</sub>/2.
- **Oversampling:** if only the band up to f<sub>max</sub> matters, filtering off the rest keeps 2·f<sub>max</sub>/f<sub>s</sub> of
  the noise: about 3 dB (half a bit) per doubling of the oversampling ratio. Equivalent bits:
  n = m + ½·log<sub>2</sub>(f<sub>s</sub> / 2f<sub>max</sub>). The gain only exists if something filters the out-of-band noise away.
- **Saturation (clipping):** worse than quantization noise. Keep the signal inside the range.
- **Adding noise sources:** a later rounding step costs 10·log<sub>10</sub>((P<sub>ref</sub> + P<sub>q</sub>) / P<sub>ref</sub>) dB against the noise
  already there.

## When the model fails

- **The model assumes** the signal crosses many levels and is busy. It is only true for signals that vary over
  the full range with many levels (Tan & Jiang cite Lipshitz et al. 1992 and Maher 1992).
- **A quiet signal is the counter-example.** Lai's example is a sine of only 0.75 Δ. It uses three levels, and its
  error is **odd harmonics** (3f<sub>0</sub>, 5f<sub>0</sub>…), which alias back in-band as tones unrelated to the pitch.
  That is a tonal buzz that follows the note, not hiss.
- **With heavy oversampling** the error correlates with the signal again, and the gain stops.
- **Dither** fixes both, by adding a small random signal before rounding:
  - rectangular dither doubles the noise power (+3 dB), and triangular (TPDF) triples it (+4.8 dB);
  - the error becomes white and independent of the signal: steady hiss instead of distortion.
- **Companding** (μ-law, A-law) is the other answer. It spends more levels on quiet amplitudes, but needs a
  non-linear decoder, which the YM2612 DAC isn't.

## What applies to Mod2Xgm

Worked numbers for the Genesis DAC (8-bit data, 9-bit DAC; [genesis-audio.md](../genesis-audio.md)):

| Model fact | Mod2Xgm consequence |
|---|---|
| 8-bit full-scale sine ≈ 49.9 dB SQNR | The best a DAC recording can do. MOD samples are 8-bit too, so a sample stored at its own level loses little, but **any gain change before storage re-quantizes**. |
| Turning down before storage costs SQNR 1:1 | A note baked at −12 dB (four 3 dB volume steps) is left with about 38 dB; at −24 dB, about 26 dB. This is the textbook reason behind "keep PCM loud" ([genesis-audio.md](../genesis-audio.md)), and behind preferring to store one loud copy over several quiet ones where the driver can scale volume instead. |
| Quiet signals give harmonic distortion, not noise | Fade-outs and quiet tails on the DAC buzz in tune with the note. In the scoring, that may count against the DAC more than a plain noise floor would. It is one more reason a quiet note can sound better on FM than on the DAC. |
| Noise powers add; quantize once | Supports pre-mixing at full precision and quantizing once ([ADR 0052](../adr/0052-pre-mixed-pcm.md)). When XGM2 mixes 3 voices, each already 8-bit, the three errors add: about +4.8 dB of noise against one pre-mixed recording, before any volume scaling in the driver. |
| Dither: +3 to +4.8 dB noise, no distortion | **Candidate, not adopted.** TPDF dither when making DAC copies, especially quiet or volume-baked ones, would trade buzz for hiss. To decide by ear in a blind listening round, and to measure with the harness on quiet-tail songs. Must stay deterministic (a fixed seed, or a fixed noise table). |
| Oversampling gains need filtering | Little gain here. DAC data is 8-bit at its own rate (about 13.3 kHz for XGM2, 14 kHz for XGM). Playing it on the 53 kHz chip doesn't add bits; only the console's analog low-pass removes some of the noise above about 3 kHz ([jsgroth-2025-ym2612-emulation.md](jsgroth-2025-ym2612-emulation.md)). |
| Truncation biases by Δ/2 | Any downscale to 8 bits should **round**, not truncate. The YM2612 itself truncates each carrier by 5 bits (part of the chip, already modelled). |
| Clipping is worse than quantization noise | Supports scaling a pre-mix down rather than letting it clip ([ADR 0052](../adr/0052-pre-mixed-pcm.md)). |
| PAR lowers SQNR | Drums (peaky) get less SQNR than pads at the same peak level. Normalizing by peak, as we do, is right for avoiding clipping, but it leaves peaky samples noisier. |
