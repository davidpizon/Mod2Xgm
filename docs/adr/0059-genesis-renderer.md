# ADR 0059: Genesis renderer: the plan played on the chip models, with Furnace's levels (T7.5)

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

Plan task T7.5: "`GenesisRenderer`: plan → stereo WAV through T7.2–T7.4. Renders corpus; performance budget
recorded". The optimizer (M9) has to hear its candidates as a Genesis plays them, in-app and in pure C#
([ADR 0010](0010-multi-pass-optimizer-sgdk-compliance-pure-csharp.md)). The pieces exist: the YM3438 port
([ADR 0056](0056-ym3438-model.md)), the SN76489 port ([ADR 0057](0057-sn76489-model.md)) and the three PCM driver
models ([ADR 0058](0058-pcm-driver-models.md)). Until now the only renders of a conversion came from Furnace and
DefleMask playing the DMF, outside the converter.

The renderer needs three things the chip models don't give: what to play (notes, pitch, volume, pan, voice), when
(tick timing), and how loud each chip is in the mix.

## Decision

(David, 2026-10-31, from the T7.5 questions.)

- **Source: the plan plus the tick player.** The `GenesisPlan` says which Genesis channel plays each note and with which
  voice (FM patch, DAC sample, PSG noise). `ModTickPlayer`'s `Playback` gives each note's pitch, volume and pan on
  every tick. `GenesisPlanPlayer` turns them into register writes at the first YM3438 sample of each tick. It does not
  read a DMF or an XGM file, so the optimizer can render a candidate plan without writing one.
- **The voices are measured as the DMF measures them.** `PlanVoicing` (`src/Mapping`) holds the measures the DMF
  translator used to keep to itself: the heard pitch of a sample, the FM and noise levels, the noise rate and the pan
  side (hard left or right beyond a third of the way out). Both use it, so the render and the DMF agree. DMF output is
  unchanged (golden tests).
- **All three PCM drivers** (`GenesisRenderOptions.Driver`): DefleMask's DAC, XGM and XGM2. With the DMF's DAC a
  note's volume is a copy of the sample at 3 dB steps, as the DMF writer makes them (silent past 15 steps), played
  at the DMF's nearest rate. XGM and XGM2 get the sample resampled to the driver's beat rate at the exact gain
  (`--resampler`), on PCM channel 0 at priority 15. A sample loop is unrolled for at most 10 s.
- **Furnace's levels.** The YM3438 output is scaled so a full-scale DAC peaks at 0.381 of full scale, and a PSG
  square is −20.68 dBFS RMS, as Furnace renders `levels.dmf` ([ADR 0050](0050-voice-levels.md)). The PSG
  goes to both sides through a 5 Hz high-pass (the chip's output is unipolar; a real Genesis has a coupling
  capacitor).
- **Note ends like the DMF.** An FM note rings until the next note or a note-off; a note that starts again releases
  its key for one YM3438 sample first, because the chip only sees key edges. A note delayed past its row's end (`EDx`)
  is left out, as ProTracker plays nothing, with a conversion note.
- **PSG noise** follows the sample: each tick's volume is the RMS of that tick's stretch of the sample against the
  sample's loudest stretch, in 2 dB steps.
- **Output:** the chips run at the YM3438's own rate (PAL 52 781 Hz, NTSC 53 267 Hz). `ChipRateResampler` (a 32-tap
  Kaiser-windowed sinc, β = 8, 1024 phases, cut at 0.92 of the lower Nyquist frequency) brings it to 44.1 kHz.
  NWaves took 27 s on 70 s of music; this takes under 0.5 s.
- **Where it shows:**
  - The CLI flag **`--render-wav [path]`** writes a 16-bit stereo 44.1 kHz WAV, by default next to the output as
    `song.genesis.wav` (same rules as the output file). It works with `--info` (only the WAV is written). With
    `--format xgm`/`xgm2` it renders with that driver and writes the WAV but no song file, then exits 1, because
    those writers don't exist yet.
  - The harness has a **`genesis`** column. It renders the default conversion with the DMF's DAC in OpenMPT's tick
    lengths, so it is scored without alignment, like `mod-renderer`.
- **Speed: measured, no target** (David). The harness logs each song's render time.

## Measured

Corpus run, 2026-10-31: `dotnet run --project tools/Mod2Xgm.Harness -c Release`, 44.1 kHz, Release build, 4 songs at
once. Lower log-mel is closer; `furnace` plays the same DMF.

| Song | Music s | Render s | × real time | `genesis` log-mel dB | `furnace` log-mel dB | Δ dB | `genesis` onset F1 | `furnace` onset F1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `10kdub` | 70.4 | 2.05 | 34 | 11.74 | 10.10 | +1.63 | 0.86 | 0.86 |
| `8bit_castle` | 130.6 | 2.23 | 59 | 15.52 | 13.63 | +1.89 | 0.49 | 0.49 |
| `cancan` | 68.5 | 1.50 | 46 | 10.56 | 10.17 | +0.39 | 0.82 | 0.81 |
| `drozerix_-_ai_renaissance` | 236.2 | 5.06 | 47 | 16.46 | 13.83 | +2.63 | 0.56 | 0.55 |
| `drozerix_-_neon_techno` | 212.3 | 4.60 | 46 | 12.79 | 11.76 | +1.03 | 0.76 | 0.76 |
| `drozerix_-_stardust_jam` | 354.1 | 7.70 | 46 | 9.82 | 10.53 | -0.70 | 0.80 | 0.50 |
| `drozerix_-_the_hawks_claw` | 242.6 | 4.27 | 57 | 11.52 | 14.07 | -2.55 | 0.79 | 0.48 |
| `fx-poly1` | 30.7 | 0.58 | 53 | 6.72 | 6.74 | -0.03 | 0.81 | 0.88 |
| `hit` | 295.7 | 6.11 | 48 | 10.34 | 9.71 | +0.63 | 0.77 | 0.77 |
| `jam_-_heatwave_cruise` | 84.5 | 1.72 | 49 | 11.45 | 10.16 | +1.29 | 0.84 | 0.83 |
| `jam_-_latenightcoffee` | 73.0 | 1.77 | 41 | 9.52 | 8.24 | +1.28 | 0.92 | 0.93 |
| `jam_-_xlr8` | 172.8 | 3.07 | 56 | 13.02 | 11.59 | +1.42 | 0.55 | 0.56 |
| `k0w-rsnd` | 176.6 | 3.20 | 55 | 8.63 | 8.72 | -0.09 | 0.99 | 0.98 |
| `kc-dancinonamiga` | 189.1 | 3.27 | 58 | 11.79 | 11.35 | +0.45 | 0.48 | 0.44 |
| `musix-cute-october` | 54.0 | 0.87 | 62 | 9.85 | 8.97 | +0.87 | 0.58 | 0.69 |
| `musix-omg-plz-help` | 93.3 | 1.93 | 48 | 9.14 | 9.73 | -0.58 | 0.84 | 0.48 |
| `musix-rubbish` | 129.1 | 3.11 | 42 | 12.20 | 12.40 | -0.20 | 0.53 | 0.27 |
| `sharpojects_cantpleaseeveryone` | 293.5 | 5.67 | 52 | 9.04 | 11.82 | -2.78 | 0.83 | 0.58 |
| `soundtracker15` | 30.7 | 0.42 | 73 | 19.52 | 15.08 | +4.44 | 0.82 | 0.70 |

- **Where Furnace plays in time** (alignment lag 0, same length) and apart from `soundtracker15`, `genesis` is −0.1 to +1.9 dB of log-mel further from the
  MOD than Furnace (median of all songs +0.63 dB). The ladder effect explains part of it (Known differences below);
  the rest is not explained yet, and T7.9's captures are what will tell which of the two is nearer a Genesis.
- **Where Furnace drifts** (DefleMask's frame timing: lag up to 975 ms, or a song up to 30 s short), `genesis` is closer,
  because it keeps the tick player's time.
- `soundtracker15` (+4.44 dB) is the outlier. Its overall level matches Furnace's within 0.2 dB on each side, and Furnace's
  render has only 60 % of the MOD's note starts, against 95 % for `genesis` and 96 % for DefleMask's own (stale)
  export, which scores 21.56 dB. It is recorded, not fixed.

Speed: 2 938 s of music in 59.1 s of rendering, **50× real time** overall, 34–73× per song, while the harness scored 4
songs at once. Through the CLI (one song, nothing else running), `10kdub` (70.4 s) renders in 1.7 s, 41× real
time; the chip models take 1.2 s of that.

## Options considered

- **Plan + tick player** (chosen).
- **Play the written DMF.** Measures the file itself, but the optimizer would have to write a DMF for every candidate,
  and XGM files don't exist yet.
- **Mix levels from the hardware.** There is no capture to measure yet (T7.9); Furnace's levels already set the
  voice levels (ADR 0050).
- **Model the YM2612's ladder effect** (Furnace's default chip). Left out: the YM3438 model is clean, as the
  Genesis 2 and later are. It is the main difference from Furnace (below).
- **Keep NWaves for the output resampling.** Too slow for the optimizer's budget.

## Consequences

- New: `src/Render/GenesisRenderer.cs`, `GenesisPlanPlayer.cs`, `GenesisChips.cs`, `ChipRateResampler.cs`,
  `WavEncoder.cs` (the harness's `WavFile` now uses it), `src/Mapping/PlanVoicing.cs`.
- Docs: [cli.md](../cli.md) (`--render-wav`), [architecture.md](../architecture.md), [harness.md](../harness.md).
- Known differences from Furnace's render of the same DMF:
  - **The ladder effect.** Furnace's YM2612 makes quiet, busy DAC sounds louder: a hi-hat is 2.3 dB louder, and
    about −50 dB of it leaks onto the muted side. On `10kdub` the right channel is 2.4 dB quieter here.
  - **Noise volume rounding.** DefleMask rounds the volume column and the macro separately; the render rounds once.
    About 1.7 dB on a quiet noise stem.
  - **Timing.** The render plays the tick player's time; DefleMask and the XGM drivers run on frames. Every write
    lands at the start of its tick.
- The optimizer (M9) scores candidates with this renderer. T7.9 checks the chip and driver models against captures
  from real drivers.
- `--render-wav` with no path, before the input (`mod2xgm --render-wav song.mod`), takes the input as its path. Put
  it after the input or give a path.
