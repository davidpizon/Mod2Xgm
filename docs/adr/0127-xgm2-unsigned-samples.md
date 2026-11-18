# ADR 0127: XGM2 files store samples unsigned, as xgm2tool reads them

- **Status:** Proposed
- **Date:** 2026-11-18

## Context

SGDK's `xgm2.txt` says the XGM2 sample block (SDAT) holds 8-bit **signed** PCM, and the XGM2 writer (plan T10.1,
[ADR 0099](0099-xgm2-writer-records-the-player.md)) wrote it that way: silence `$00`, padding `$00`.

An SGDK game never plays the file directly: rescomp runs xgm2tool, which compiles it to the `.xgc` the Z80 driver
plays. xgm2tool reads the sample bytes as they are (`XGM.java`, sample table parse) and, when it writes the `.xgc`,
adds `$80` to every byte (`XGM.getPCMDataArray`, "sign the sample"). Its internal samples are unsigned, as they come
from VGM. So the driver got every sample with its sign bit flipped: silence became −128 and quiet sounds near full
scale.

It was found on a real game (2026-11-18): FunkyChristmas converted to XGM2 played in Gens as steady full-scale white noise
on the DAC, covering the music. The same noise came out of SGDK's real XGM2 driver on the emulated Z80
(`tools/pcm-driver-reference`'s machine fed the compiled song), while SGDK's own `sor2.vgm` compiled by xgm2tool played
clean drums in both. Flipping the file's sample bytes before rescomp made the Gens recording match the `--render-wav`
preview: same level curve, beat, brightness, every band within 2 dB. The preview never showed it because it renders the
plan, not the file, and plan task T10.2 (playing the output through SGDK) hasn't run yet.

## Decision

- The XGM2 writer stores sample bytes **unsigned**: each signed byte with its sign bit flipped, silence and the
  256-byte padding as `$80`. After xgm2tool the driver gets the signed bytes the plan meant.
- What xgm2tool does decides, not the spec text: SGDK compliance means playing correctly through rescomp
  (AGENTS.md#sgdk-compliance).
- `Xgm2SgdkTests.Xgm2Tool_WithConvertedSong_CompilesSignedSamples` checks it against the installed xgm2tool (skipped
  where SGDK isn't installed).

## Options considered

- **Store unsigned (chosen).** The only way a file rescomp reads plays right; one line in the writer.
- **Keep signed, per the spec.** Plays as noise in every SGDK game.
- **Write the compiled `.xgc` instead and have games include it with `BIN`.** Sidesteps xgm2tool but drops the
  `XGM2` resource path the docs and users rely on, and packing would have to be ours.

## Consequences

- `.xgm2` files made before this change play their PCM as noise through SGDK; convert them again.
- docs/formats/xgm.md says the file's samples are unsigned and why.
- Revisit if SGDK's xgm2tool stops adding `$80` (a changed `getPCMDataArray`); the SGDK test would then fail.
- Open: whether xgmtool (XGM v1, T11.2) does the same to XGM files; check before writing `XgmWriter`.
