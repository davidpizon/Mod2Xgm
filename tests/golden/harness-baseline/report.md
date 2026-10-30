# Accuracy harness report

Reference: libopenmpt 0.8.9. Converter: Mod2Xgm 0.1.0. Scored mono at 44100 Hz.
Every number is the conversion against the reference; **lower is closer** except onset F1 (1 = every note start found).
Metrics: docs/adr/0042-scoring-metrics.md. The combined score is the log-mel distance; it agrees only weakly with listening (docs/adr/0049-simple-mapper-baseline.md).

## Scores

| Song | Renderer | Combined | Log-mel dB | MFCC | Onset F1 | Timing ms | Lag ms | Length Δ s | Remark |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| `10kdub` | furnace | 10.21 | 10.21 | 54.28 | 0.85 | 2.4 | 0 | -0.10 |  |
| `8bit_castle` | furnace | 12.29 | 12.29 | 55.89 | 0.49 | 2.5 | 0 | -0.10 |  |
| `cancan` | furnace | 10.80 | 10.80 | 71.98 | 0.77 | 1.4 | 0 | -0.10 |  |
| `drozerix_-_ai_renaissance` | furnace | 13.66 | 13.66 | 66.83 | 0.56 | 13.6 | 0 | -1.51 |  |
| `drozerix_-_neon_techno` | furnace | 11.16 | 11.16 | 86.22 | 0.78 | 5.4 | 0 | -0.10 |  |
| `drozerix_-_stardust_jam` | furnace | 10.90 | 10.90 | 63.73 | 0.45 | 24.0 | 575 | 1.34 |  |
| `drozerix_-_the_hawks_claw` | furnace | 14.16 | 14.16 | 83.96 | 0.48 | 22.1 | 151 | -29.78 |  |
| `fx-poly1` | furnace | 7.02 | 7.02 | 50.53 | 0.81 | 0.2 | 0 | -0.10 |  |
| `hit` | furnace | 9.66 | 9.66 | 63.25 | 0.78 | 8.2 | 0 | -0.10 |  |
| `jam_-_heatwave_cruise` | furnace | 10.23 | 10.23 | 70.57 | 0.84 | 2.5 | 0 | -0.10 |  |
| `jam_-_latenightcoffee` | furnace | 8.39 | 8.39 | 61.81 | 0.93 | 1.2 | 0 | -0.10 |  |
| `jam_-_xlr8` | furnace | 12.39 | 12.39 | 74.63 | 0.58 | 7.8 | 0 | -0.10 |  |
| `k0w-rsnd` | furnace | 10.76 | 10.76 | 69.64 | 0.98 | 1.1 | 0 | -0.10 |  |
| `kc-dancinonamiga` | furnace | 9.80 | 9.80 | 66.81 | 0.53 | 24.8 | 743 | 0.04 |  |
| `musix-cute-october` | furnace | 9.40 | 9.40 | 58.78 | 0.67 | 8.6 | 0 | -0.10 |  |
| `musix-omg-plz-help` | furnace | 9.78 | 9.78 | 66.94 | 0.49 | 24.8 | -81 | -1.86 |  |
| `musix-rubbish` | furnace | 12.75 | 12.75 | 72.84 | 0.27 | 17.5 | -975 | 0.86 |  |
| `sharpojects_cantpleaseeveryone` | furnace | 11.82 | 11.82 | 68.85 | 0.59 | 14.6 | 6 | -25.08 |  |
| `soundtracker15` | furnace | 15.06 | 15.06 | 81.95 | 0.69 | 12.5 | 0 | -0.10 |  |

## First seconds only (listening clips)

The same scores for the clips in `wav/*.clip.wav` (`--clip`).

| Song | Renderer | Combined | Log-mel dB | MFCC | Onset F1 |
|---|---|---:|---:|---:|---:|
| `10kdub` | furnace | 10.01 | 10.01 | 54.40 | 0.87 |
| `8bit_castle` | furnace | 12.36 | 12.36 | 70.77 | 0.55 |
| `cancan` | furnace | 10.50 | 10.50 | 65.25 | 0.85 |
| `drozerix_-_ai_renaissance` | furnace | 13.37 | 13.37 | 67.32 | 0.47 |
| `drozerix_-_neon_techno` | furnace | 12.77 | 12.77 | 85.23 | 0.80 |
| `drozerix_-_stardust_jam` | furnace | 12.38 | 12.38 | 79.13 | 0.43 |
| `drozerix_-_the_hawks_claw` | furnace | 12.36 | 12.36 | 75.72 | 0.42 |
| `fx-poly1` | furnace | 6.99 | 6.99 | 50.33 | 0.81 |
| `hit` | furnace | 9.98 | 9.98 | 62.09 | 0.65 |
| `jam_-_heatwave_cruise` | furnace | 10.10 | 10.10 | 70.62 | 0.85 |
| `jam_-_latenightcoffee` | furnace | 8.54 | 8.54 | 62.05 | 0.90 |
| `jam_-_xlr8` | furnace | 11.85 | 11.85 | 73.30 | 0.57 |
| `k0w-rsnd` | furnace | 10.77 | 10.77 | 69.91 | 0.97 |
| `kc-dancinonamiga` | furnace | 8.71 | 8.71 | 61.70 | 0.65 |
| `musix-cute-october` | furnace | 11.03 | 11.03 | 60.85 | 0.62 |
| `musix-omg-plz-help` | furnace | 9.36 | 9.36 | 71.01 | 0.57 |
| `musix-rubbish` | furnace | 13.21 | 13.21 | 84.32 | 0.25 |
| `sharpojects_cantpleaseeveryone` | furnace | 6.79 | 6.79 | 49.21 | 0.52 |
| `soundtracker15` | furnace | 15.04 | 15.04 | 82.28 | 0.69 |

## Average per renderer

| Renderer | Songs | Combined | Log-mel dB | MFCC | Onset F1 |
|---|---:|---:|---:|---:|---:|
| furnace | 19 | 11.07 | 11.07 | 67.87 | 0.66 |

## Song timeline: tick player against the reference (±1 tick)

The tick player's ticks timed OpenMPT's way must start every row, and end the song, within one tick of the reference.
*Amiga − OpenMPT* is how much a real Amiga's timing (the tick player's own) differs from OpenMPT's rules (docs/harness.md#song-timeline).

| Song | Amiga s | OpenMPT rules s | Reference s | Δ s | Amiga − OpenMPT s | 1 tick s | Within | First row off | Conversion notes | Error |
|---|---:|---:|---:|---:|---:|---:|---|---|---:|---|
| `10kdub` | 70.402 | 70.400 | 70.400 | 0.000 | 0.002 | 0.020 | ✅ |  | 12 |  |
| `8bit_castle` | 130.564 | 130.560 | 130.560 | 0.000 | 0.004 | 0.020 | ✅ |  | 20 |  |
| `cancan` | 68.522 | 68.520 | 68.520 | 0.000 | 0.002 | 0.020 | ✅ |  | 19 |  |
| `drozerix_-_ai_renaissance` | 236.241 | 236.178 | 236.178 | 0.000 | 0.063 | 0.024 | ✅ |  | 32 |  |
| `drozerix_-_neon_techno` | 212.286 | 212.280 | 212.280 | 0.000 | 0.006 | 0.020 | ✅ |  | 26 |  |
| `drozerix_-_stardust_jam` | 354.212 | 354.001 | 354.001 | 0.000 | 0.211 | 0.025 | ✅ |  | 57 |  |
| `drozerix_-_the_hawks_claw` | 242.745 | 242.697 | 242.697 | 0.000 | 0.048 | 0.020 | ✅ |  | 33 |  |
| `fx-poly1` | 30.721 | 30.720 | 30.720 | 0.000 | 0.001 | 0.020 | ✅ |  | 6 |  |
| `hit` | 295.689 | 295.680 | 295.680 | 0.000 | 0.009 | 0.020 | ✅ |  | 52 |  |
| `jam_-_heatwave_cruise` | 84.483 | 84.480 | 84.480 | 0.000 | 0.003 | 0.020 | ✅ |  | 11 |  |
| `jam_-_latenightcoffee` | 72.962 | 72.960 | 72.960 | 0.000 | 0.002 | 0.020 | ✅ |  | 21 |  |
| `jam_-_xlr8` | 172.805 | 172.800 | 172.800 | 0.000 | 0.005 | 0.020 | ✅ |  | 14 |  |
| `k0w-rsnd` | 176.645 | 176.640 | 176.640 | 0.000 | 0.005 | 0.020 | ✅ |  | 13 |  |
| `kc-dancinonamiga` | 189.224 | 189.131 | 189.131 | 0.000 | 0.094 | 0.022 | ✅ |  | 12 |  |
| `musix-cute-october` | 54.009 | 54.000 | 54.000 | 0.000 | 0.009 | 0.020 | ✅ |  | 11 |  |
| `musix-omg-plz-help` | 93.382 | 93.385 | 95.000 | -1.615 | -0.003 | 0.026 | ✅ (stops at F00; end not compared) |  | 31 |  |
| `musix-rubbish` | 129.243 | 129.192 | 129.192 | 0.000 | 0.051 | 0.020 | ✅ |  | 33 |  |
| `sharpojects_cantpleaseeveryone` | 293.561 | 293.531 | 293.531 | 0.000 | 0.030 | 0.020 | ✅ |  | 43 |  |
| `soundtracker15` | 30.720 | 30.720 | 30.720 | 0.000 | 0.000 | 0.020 | ✅ |  | 6 |  |

19 of 19 songs within one tick; 0 songs failed.
