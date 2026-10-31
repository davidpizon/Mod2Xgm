# ADR 0062: A sample 1 word long is read as OpenMPT reads it

- **Status:** Accepted; amends [ADR 0060](0060-openmpt-pitch-and-loops.md) (its first known difference)
- **Date:** 2026-10-31

## Context

[ADR 0060](0060-openmpt-pitch-and-loops.md#known-differences) left one known difference in the MOD renderer: in
`jam_-_heatwave_cruise` and `jam_-_latenightcoffee` it ran a constant 0.11 ms and 0.25 ms early against libopenmpt from
the first note (window correlation about 0.96). Those two songs, which use only `Cxx` and `Dxx`, were the worst
`mod-renderer` scores at 44.1 kHz (0.49 and 0.62 dB log-mel). The suspects were the mixer: the interpolation filter, the
volume ramps, where a sample starts, the click removal.

## Measurements

With **libopenmpt 0.8.9**, the harness's clips cross-correlated in short windows, and probe MODs:

- **Not the mixer.** With one channel playing at a time the lead was different on each channel: 0.255, 0.27, 0.088 and
  0.18 ms in `latenightcoffee`.
- **A lead in source samples, not in time.** One note of `latenightcoffee`'s sample 18 at periods 113, 226 and 856 led
  by 0.128, 0.255 and 0.964 ms: always **4 source samples**.
- **The cause is OpenMPT's loader.** `MODSampleHeader::ConvertToMPT` (`soundlib/MODTools.cpp`) sets a sample 1 word long
  (2 bytes) to length 0, so the loader never reads its 2 bytes. Every later sample's data then starts 2 bytes earlier in
  the file: it begins with the 2 bytes before it and loses its own last 2. Each such sample adds 2 bytes to the shift.
  A synthetic MOD confirmed it (nearest-sample interpolation, no ramps): a ramp sample after a 2-byte sample `[90, 90]`
  plays 90, 90, 10, 11, …; after a 4-byte sample it plays 10, 11, …. A 15-sample file does the same.
- Only these two corpus songs have 1-word samples: sample 1 in `heatwave_cruise`; samples 4 and 8 in `latenightcoffee`,
  so its later samples move by 4 bytes. All three hold 2 zero bytes, and the files' sizes count them (header + patterns +
  every header's length, exactly). ProTracker reads them as stored, as Mod2Xgm did.

## Decision

- **`ModReader` reads a sample 1 word long as empty and doesn't read its 2 bytes**, as OpenMPT does (David), in
  31-sample and 15-sample files. The samples after it start 2 bytes earlier for each one, and the file's last bytes go
  unread. One conversion note lists the samples and the shift.
- The pattern count and the "bytes after the last sample" check still use the headers' lengths: the bytes OpenMPT
  leaves unread at the end are explained by the new note, not reported as extra data.

## Results

Whole-song log-mel distance of the in-app MOD renderer to libopenmpt (the `mod-renderer` column), before → after:

| Song | 44.1 kHz | 22.05 kHz | 8 kHz |
|---|---|---|---|
| `jam_-_heatwave_cruise` | 0.49 → 0.10 | 0.46 → 0.05 | 0.47 → 0.17 |
| `jam_-_latenightcoffee` | 0.62 → 0.06 | 0.62 → 0.05 | 0.62 → 0.17 |

Both songs now match libopenmpt sample for sample (lag 0, window correlation 1.000 over the first 30 s). No other song
has a 1-word sample, and none changed. The mean at 44.1 kHz goes from 0.13 to 0.08 dB, and the worst song there is now
`soundtracker15` (0.41). The `genesis` column hardly moves: `heatwave_cruise` 11.45 → 11.50 dB, `latenightcoffee` 9.52
→ 9.52.

`latenightcoffee`'s onset F1 drops from 0.98 to 0.90 although the two renders differ only by noise about 50 dB under the
signal. The extra onsets (151 against 128) come where about 9 of the top mel bands sit at the scorer's −80 dB floor in
libopenmpt's render and just above it in ours: a band rising off the floor counts as a note start. This is the onset
metric, not the renderer; the onset weight in the combined score is 0.

## Options considered

- **Read 1-word samples as OpenMPT does** (chosen): the converter, the tick player and both renderers use the same
  sample data as the reference the harness scores against.
- **Keep ProTracker's reading** and record the difference: the bytes as stored, but a constant lead against the
  reference in every song with a 1-word sample, and those songs scoring worst.
- **Only in the MOD renderer:** the renderer reads the song model, so it would need a second copy of the sample data;
  and the Genesis renderer would aim at different data than the reference.

## Consequences

- `src/Mod/ModReader.cs`: a 1-word sample is empty; the later samples' data and data offsets move.
- The DMF, XGM and XGM2 samples of such songs change by 2 bytes a 1-word sample: zeros or the tail of the sample
  before at the start, 2 bytes fewer at the end.
- docs/formats/mod.md (sample header, damaged files) and harness.md's numbers are updated; ADR 0060's known difference
  is closed.
- Follow-up: the onset metric could ignore rises from its floor.
