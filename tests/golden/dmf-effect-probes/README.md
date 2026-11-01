# DMF effect probes (plan T5.5)

Eleven small MODs, each built around one group of effects, converted to DMF by the real pipeline. Listen to the MOD
and the DMF side by side: when they sound alike, the effect translation and the units in
[docs/formats/dmf.md](../../../docs/formats/dmf.md#effect-units) are right. Decisions: [ADR 0044](../../../docs/adr/0044-dmf-effects-and-song-grid.md).

The MODs are made by `tests/Mod2Xgm.Tests/Dmf/DmfEffectProbes.cs`, so they carry no one else's copyright. For each
probe `name`:

| File | What it is |
|---|---|
| `name.mod` | The MOD (play it in OpenMPT or any MOD player) |
| `name.dmf` | What the converter makes of it (open it in DefleMask) |
| `name.txt` | Everything in the DMF, row by row, plus the conversion notes |
| `wav/name.mod.wav` | The MOD rendered by openmpt123 0.8.9 |
| `wav/name.furnace.wav` | The DMF rendered by Furnace 0.6.8.3 |
| `wav/name.deflemask.wav` | The DMF exported by DefleMask (File → Save WAV) |

The WAVs are 16-bit, 22 050 Hz, mono (stereo for `panning`). DefleMask's export is about 5.8 dB quieter than the
others; that is DefleMask's overall level, not the conversion.

## What to listen for (David)

All probes use a sine lead (a 32-frame loop, so the MOD's C-2 sounds C-4, 259 Hz) at 6 ticks a row, 125 BPM (8 rows
a second), unless said otherwise.

| Probe | You should hear, in both | Known differences |
|---|---|---|
| `slide` | The note slides up about four semitones (rows 8–15), back to C, down about four semitones (24–31), back to C, then four small steps up | — |
| `portamento` | C glides up to G (row 16), fast back down to C (32), then up to E while fading out (48) | — |
| `vibrato` | A gentle wobble (rows 0–15), a fast deep one (16–31), the fast one fading (32–43), then a plain note | DefleMask's speed digit is whole: 2.3 Hz against the MOD's 2.6 Hz |
| `arpeggio` | Major-chord trill (rows 0–15), minor (16–31), octave (32–47), then a plain note | — |
| `volume` | Four steps down, 6 dB each; full again; a fade out (33–44) and back in (45–52); three small steps | The quietest steps are up to 2 dB louder on the DMF (the YM2612's DAC) |
| `tremolo` | A slow, deep volume wobble (rows 1–15), a fast one (16–31), then steady | DefleMask's tremolo only goes down from the note's volume; the MOD's goes up and down |
| `retrigger-cut-delay` | Four notes retriggered every 3 ticks; four short notes cut after 2 ticks; a note 3 ticks behind a higher one, twice; a drum retriggered on the DAC | The cut notes ring a little after the cut on the DMF (the FM patch's release) |
| `panning` | Four notes, one per Amiga channel: left, right, right, left; then a drum right, then left | The MOD (openmpt123's default mix) bleeds a little into the other side |
| `dac` | The drum at full, half, quarter and eighth volume; started 1024 and 2048 frames in (shorter, no attack); then an octave up | The octave-up drum plays at the same pitch (the simple mapper gives each DAC sample one pitch, with a note); the drum's tail fades less far on the DMF (the YM2612's DAC floor, about −44 dB) |
| `jumps` | A rising scale that jumps from order 0 row 28 to order 1 row 8, holds one note longer (EE3), speeds up (F03), jumps to order 2 (B02), slows to 100 BPM, and loops to the start | — |
| `noise` | Noise hits on the PSG noise channel (converted with `--no-dac`) at full, half and quarter volume, each fading like the sample | The PSG has three fixed noise rates; the brightest is used |

Tell Claude what you hear; differences go into docs/formats/dmf.md.

## Remaking the files

The `.mod`, `.dmf` and `.txt` files are goldens: `dotnet test` checks them, and `UPDATE_GOLDEN=1 dotnet test` rewrites
them after a change. The WAVs are made by hand afterwards (external applications are allowed only in tests and
tools, AGENTS.md):

```
openmpt123 --render --samplerate 44100 name.mod
furnace -console -nostatus -nocontrols -loops 0 -output name.furnace.wav name.dmf
ffmpeg -i in.wav -ac 1 -ar 22050 -c:a pcm_s16le out.wav
```

`openmpt123` writes `name.mod.wav` next to the MOD (32-bit float); `ffmpeg` makes every render 16-bit 22 050 Hz mono
(`-ac 2` for `panning`). DefleMask: open the `.dmf`, File → Save WAV → Export (multi-channel off).

## Results

2026-10-28, measured by Claude from the WAVs (pitch every 20 ms by autocorrelation, loudness every 20 ms):

- Against the MOD, both renders match within the measurement's resolution (about 20 cents near 260 Hz) on `slide`,
  `portamento`, `arpeggio`, `vibrato` (rate within the speed digit's step, depth within 7 cents) and `jumps`; volume
  steps and slides within 1–2 dB; `noise` within about 3 dB.
- The first DefleMask exports found two DefleMask rules Furnace doesn't copy: a re-written `07xy` stacks until the
  note is silent, and `03xx` re-written without a note freezes the glide. Both are now avoided, and the probes were
  exported again (docs/formats/dmf.md#effect-units).
- David's listening: not done yet.
- 2026-10-31: `noise` changed with the PSG's volume levels ([ADR 0067](../../../docs/adr/0067-psg-volume-levels.md)):
  its fade now ends more gently. `wav/noise.furnace.wav` was rendered again; `wav/noise.deflemask.wav` is still the
  export of the old file (DefleMask's export is by hand).
