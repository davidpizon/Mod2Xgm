# ADR 0038: Sample processor and `--resampler` (T5.1)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T5.1: every output stores samples at fixed rates (DMF: one rate code per sample; XGM ~14 kHz; XGM2
13.3 / 6.65 kHz), so each (sample, pitch) a song plays needs its own resampled copy, in the target's bit depth, with
loops played out (no target loops DAC samples the way Paula does). The mapper (T5.3) and the budgeter decide which
copies are needed; T5.1 provides the tools.

## Decision

- **Toolbox only** (David): `src/Audio/SampleProcessor.cs` resamples, unrolls a loop to a length, cuts data past a
  loop end, trims trailing silence, applies a gain, and converts to 16-bit or 8-bit signed PCM. Nothing calls it yet;
  T5.3 picks rates, lengths and volumes.
- **Two resampling modes, chosen with `--resampler smooth|amiga`, default `smooth`** (David):
  - `smooth`: the sound between the points is smooth, as OpenMPT (the harness reference) plays it — windowed sinc.
  - `amiga`: Paula's steps (each point held until the next), with the fizzy overtones they give. The steps are drawn
    at 8 × the target rate and band-limited down to it, so the overtones stay up to the target's limit and nothing
    aliases. The A500's analog low-pass and the LED filter are not modeled.
- **Smooth resampling uses the NWaves library** (David, over writing our own or libsamplerate): NWaves 0.9.6, MIT,
  pure C# (netstandard2.0 / net5.0), no reflection, so it works under Native AOT on all three platforms.
  `Resampler.Resample` with a Hann-windowed sinc of ±32 points (NWaves' default is ±15).
  - Rates are passed in hundredths of a hertz, because NWaves takes whole numbers and MOD rates aren't (8287.14 Hz):
    the pitch error is under 0.002 cents.
  - **Going down, our own anti-aliasing filter runs first.** NWaves' built-in one (101 taps) runs as a stream, which
    delays the sound by 50 points and drops its end. We run a 255-tap Blackman low-pass (cutoff 0.95 × the new
    limit) as a full convolution, remove its delay, and pass it to `Resample` so NWaves doesn't filter again.
  - Measured in a model of the same arithmetic: a 440 Hz tone 8287 → 32000 Hz stays within 0.0001 of the ideal sine;
    a 6000 Hz tone 32000 → 8000 Hz is removed to about −120 dB; in `amiga` mode Paula's first image of a 440 Hz tone
    at 8287 Hz is at about −25 dB, as theory says (sinc(7847 / 8287) ≈ 0.056).
- **8-bit and 16-bit: round to the nearest step, no dither** (David): halves round away from zero, so MOD's 8-bit
  points survive exactly. Points past ±1 (gain, or a sinc's overshoot) are clipped, with one conversion note per
  sample: `Sample {Sample}: {Frames} of {Total} frames clipped converting to {Bits}-bit PCM (peak {Peak})`.
- **Loops follow ProTracker 2:** a looping sample plays from its start to its loop end, then the loop; data past the
  loop end never plays, so cutting it is lossless. Trailing silence is trimmed only from samples without a loop, and
  only points that round to 0 at the target depth, so trimming is lossless too.

## Options considered

- **NWaves** (chosen): pure C#, MIT, AOT-friendly. Its last release (0.9.6) is from 2022, and its downsampling filter
  delays the sound, which we work around as above.
- **Writing our own windowed-sinc resampler**: no dependency and full control; David chose a library.
- **libsamplerate** (BSD-2): best-known quality, but native C with no NuGet package for win-x64, linux-x64 and
  osx-arm64, so CI would have to build and statically link it.
- **Dither before 8-bit**: hides the stair-step in quiet fades but adds faint hiss; the harness can test it later.
- **Only one mode**: `smooth` matches the harness reference; `amiga` matches a real Amiga. David wanted both.

## Consequences

- New option `--resampler` in [cli.md](../cli.md); `ConversionOptions.Resampler`.
- NWaves added to `Directory.Packages.props` and [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).
- T5.3 (mapper) and the budgeter use these tools; the harness (M6) can compare `smooth` and `amiga` against
  openmpt123 and a real-Amiga-style render.
