# YM3438 probes

Register-write scripts that check the converter's YM3438 model (`src/Render/Fm/`, plan T7.2,
[ADR 0056](../../../docs/adr/0056-ym3438-model.md); the chip's differences from ymfm, plan T7.7,
[ADR 0063](../../../docs/adr/0063-ym3438-chip-differences.md)), and what ymfm and Nuked-OPN2 play for each.

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
| `clip-order` | Carriers loud enough to clip channel 1, at different multiples, in algorithms 7, 6 and 4 (ADR 0063) |
| `modulation-order` | Strong modulators in algorithms 0, 1, 2, 3 and 5, each note from silence (ADR 0063) |
| `ssg-shapes` | SSG-EG shapes 0–7 with fast decays, held long enough to turn several times, released into silence (ADR 0063) |
| `top-note` | Block 7, F-number `$7FF` on one operator with each detune, multiples 1 and 15 (ADR 0063) |
| `envelope-rates` | Decay rates 20–31 after the instant attack, long enough for the envelope timer to wrap (ADR 0063) |

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
- The port plays every probe that none of the chip's differences reaches sample for sample as ymfm does (today `dac`
  and `top-note`).
- Every probe a difference reaches is listed under that difference's name, plays differently from ymfm, and plays at
  least as many samples as Nuked-OPN2 does as ymfm manages.
- The one-channel probes play sample for sample as Nuked-OPN2 does on at least a per-probe share (90–99.9 %), at the
  delay of Nuked's pipeline and within half a Nuked step. The probes that sound several channels are left out: Nuked's
  writes take time, so their channels start apart.
- The port is within a log-mel distance of Nuked-OPN2: 0.6 dB, or a probe's own bar for what the model can't remove
  ([ADR 0063](../../../docs/adr/0063-ym3438-chip-differences.md#what-is-left)). Nuked's output is scaled by 128/18 to
  match ymfm's 128/6.
- Every probe renders within a minute, so a change that loops forever fails instead of hanging the run.

## Updating

After changing a probe:

1. `UPDATE_GOLDEN=1 dotnet test --filter FullyQualifiedName~Ym3438ProbeTests` rewrites the scripts.
2. `bash tools/ym3438-reference/run.sh` remakes both outputs for every script (needs a C++17 g++; on Windows,
   MinGW-w64 on `PATH`). See [its README](../../../tools/ym3438-reference/README.md).
3. Run the tests again, and look at any Nuked distance that changed before moving a bar.
