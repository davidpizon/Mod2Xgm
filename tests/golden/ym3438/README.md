# YM3438 probes

Register-write scripts that check the converter's YM3438 model (`src/Render/Fm/`, plan T7.2,
[ADR 0056](../../../docs/adr/0056-ym3438-model.md)), and what ymfm and Nuked-OPN2 play for each.

| Probe | What it plays |
|---|---|
| `sine` | One sine operator at 440 Hz, held, then released |
| `algorithms` | Algorithms 0–7 in turn with the same four operators, feedback 4 |
| `feedback` | Operator 1 alone with feedback 0–7, until it turns to noise |
| `envelopes` | A slow attack, decay to a sustain level, sustain rate, fast release, key scaling, attack rate 31 set after the key-on |
| `detune-multiple` | Every detune and multiple at 80 Hz and 1200 Hz, then the top of the range (block 7, `$7FF`) and the bottom |
| `lfo` | Tremolo and vibrato at full depth, LFO rates 0–7, then the LFO off with AM still on |
| `ssg-eg` | SSG-EG shapes 0–7, each keyed off while still sounding |
| `ch3-special` | CH3 special mode: a chord from channel 3's four operators, then normal mode again |
| `dac` | A 9-bit sawtooth on the DAC (bit 0 through `$2C`) with an FM note, then the DAC off |
| `six-channels` | All six channels of full-level carriers, so each clips; panned left, right, both and none |
| `write-quirks` | The F-number latch, changes during a note, a key-on without a key-off, single operators, key-ons to the missing channel 3 |

## Files

- **`<probe>.txt`** — the script, built in code by `Ym3438Probes` (`tests/Mod2Xgm.Tests/Render/Fm/`). One step per
  line: `w part reg value` writes a register (part 0 or 1, register and value in hex), `s count` plays that many
  samples (decimal), `#` starts a comment. A sample is 144 master clocks, 53 267 Hz on NTSC.
- **`<probe>.ymfm.gz`** — what ymfm's `ym3438` plays, at commit `81aec25`.
- **`<probe>.nuked.gz`** — what Nuked-OPN2 plays in YM3438 mode, at commit `335747d`: the output pins summed over each
  sample's 24 clocks.

Both outputs are gzipped little-endian int32 pairs (left, right), one pair per sample.

## The checks (`Ym3438ProbeTests`)

- The checked-in script is the one `Ym3438Probes` builds.
- The port plays every probe sample for sample as ymfm does.
- The port is within a log-mel distance of Nuked-OPN2: 0.6 dB, or a probe's own bar where ymfm is known to differ from
  the chip ([ADR 0056](../../../docs/adr/0056-ym3438-model.md#known-differences-from-the-chip)). Nuked's output is
  scaled by 128/18 to match ymfm's 128/6.

## Updating

After changing a probe:

1. `UPDATE_GOLDEN=1 dotnet test --filter FullyQualifiedName~Ym3438ProbeTests` rewrites the scripts.
2. `bash tools/ym3438-reference/run.sh` remakes both outputs for every script (needs a C++17 g++; on Windows,
   MinGW-w64 on `PATH`). See [its README](../../../tools/ym3438-reference/README.md).
3. Run the tests again, and look at any Nuked distance that changed before moving a bar.
