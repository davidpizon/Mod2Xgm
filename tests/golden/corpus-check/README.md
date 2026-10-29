# Corpus check (plan T5.7)

Does what the converter makes of real songs open and play in DefleMask, and survive SGDK's path (DefleMask VGM export →
rescomp `XGM2`)? The songs are the corpus ([tests/data/LICENSES.md](../../data/LICENSES.md), plan T6.1). This is the
check for the first playable release, **0.1.0**. It needs David's listening sign-off.

Everything made here goes into `out/` (git-ignored). Only this README and `corpus.res` are committed.

## What to do (David): listen

For each song, play the MOD (`tests/data/corpus/name.mod`, in OpenMPT or any MOD player) and then DefleMask's
render of the conversion (`out/wav/name.wav`), or open `out/dmf/name.dmf` in DefleMask. Write in the last column of the
results table: **OK** (clearly the same song), **rough** (the same song, but something is off: say what), or **wrong**
(broken: silence, wrong notes, wrong speed).

Expected with the simple mapper ([ADR 0040](../../../docs/adr/0040-simple-mapper.md)), not bugs for this check:

- Melodic samples play on one of five **placeholder FM patches** (Sine, Organ, Lead, Brass, Pluck), so they sound like
  FM, not like the sample. Fitted patches come in M8.
- Only **one drum at a time** plays on the DAC; a second drum on the same row moves to FM or the PSG noise channel.
- A DAC sample plays at **one pitch** (its most-used one).
- **Pattern loops (`E6x`) play once**, and sample offsets (`9xx`) on FM are left out. The conversion notes say so.

## Results

2026-10-29. Claude converted every song with `mod2xgm` (default options, `--quality normal`, which is the simple mapper
for now), then used computer use in DefleMask 1.x to open each DMF, export it as VGM and as WAV (File → Save VGM, File →
Save WAV, loop count 0), and ran rescomp on all 19 VGMs.

| Song | Conversion notes | DMF output | DMF KB | Opens | VGM KB | rescomp `XGM2` KB | WAV | Sounds (David) |
|---|---|---|---|---|---|---|---|---|
| `10kdub` | 12 | 3 FM, 1 noise, 4 DAC (5 KB PCM) | 10 | ✅ | 60 | ✅ 11 | 70 s | |
| `8bit_castle` | 20 | 3 FM, 3 DAC (7 KB) | 15 | ✅ | 57 | ✅ 12 | 131 s | |
| `cancan` | 19 | 2 FM, 1 noise, 8 DAC (81 KB) | 150 | ✅ | 106 | ✅ 64 | 69 s | |
| `drozerix_-_ai_renaissance` | 32 | 2 FM, 7 DAC (18 KB) | 46 | ✅ | 424 | ✅ 81 | 235 s | |
| `drozerix_-_neon_techno` | 26 | 4 FM, 1 noise, 15 DAC (71 KB) | 139 | ✅ | 233 | ✅ 83 | 212 s | |
| `drozerix_-_stardust_jam` | 57 | 5 FM, 3 noise, 34 DAC (**274 KB**) | 525 | ✅ | 544 | ✅ 252 | 355 s | |
| `drozerix_-_the_hawks_claw` | 33 | 5 FM, 1 noise, 25 DAC (**183 KB**) | 334 | ✅ | 347 | ✅ 200 | 213 s | |
| `fx-poly1` | 6 | 1 FM, 8 DAC (12 KB) | 22 | ✅ | 23 | ✅ 9 | 31 s | |
| `hit` | 52 | 2 FM, 4 noise, 40 DAC (**398 KB**) | 720 | ✅ | 549 | ✅ 332 | 296 s | |
| `jam_-_heatwave_cruise` | 11 | 1 FM, 6 DAC (48 KB) | 92 | ✅ | 66 | ✅ 34 | 85 s | |
| `jam_-_latenightcoffee` | 21 | 2 FM, 5 noise, 15 DAC (**129 KB**) | 235 | ✅ | 147 | ✅ 89 | 73 s | |
| `jam_-_xlr8` | 14 | 2 FM, 8 DAC (42 KB) | 79 | ✅ | 127 | ✅ 32 | 173 s | |
| `k0w-rsnd` | 13 | 2 FM, 1 noise, 5 DAC (14 KB) | 29 | ✅ | 81 | ✅ 15 | 177 s | |
| `kc-dancinonamiga` | 12 | 2 FM, 3 DAC (19 KB) | 39 | ✅ | 81 | ✅ 17 | 189 s | |
| `musix-cute-october` | 11 | 3 FM, 1 noise, 2 DAC (14 KB) | 25 | ✅ | 44 | ✅ 11 | 54 s | |
| `musix-omg-plz-help` | 31 | 4 FM, 3 noise, 15 DAC (**187 KB**) | 333 | ✅ | 285 | ✅ 153 | 93 s | |
| `musix-rubbish` | 33 | 5 FM, 1 noise, 10 DAC (**358 KB**) | 644 | ✅ | 391 | ✅ 330 | 130 s | |
| `sharpojects_cantpleaseeveryone` | 43 | 2 FM, 3 noise, 40 DAC (**696 KB**) | 1302 | ✅ | 784 | ✅ 598 | 269 s | |
| `soundtracker15` | 6 | 2 FM, 2 DAC (11 KB) | 18 | ✅ | 18 | ✅ 12 | 31 s | |

- **Every song converts** (exit 0), **opens in DefleMask** without an error, and plays.
- **Every VGM goes through rescomp `XGM2`**: ResComp 4.02 with `xgm2tool`, exit code 0, no warnings, 2.3 MB in all.
- **Every WAV has sound**: −15 to −25 dBFS RMS, nothing silent or clipped flat.
- Not checked here: playing the XGM2 in an SGDK ROM (plan T10.2/T10.3), and song length against openmpt123 (the harness,
  T6.3). DMF timing already matches the tick player (T5.4).

### Found

- **Fixed in this PR:** the "frames clipped converting to 16-bit PCM" note named a DAC sample only by its name, so it
  read `Sample : …` for the many corpus samples with an empty name. It now says `Sample 13 "": …` like every other note.
- **Left for later: PCM over budget.** Seven songs (in bold) carry more PCM than the 128 KB default budget, up to 696 KB,
  and nothing says so yet. The capacity warning and consolidation are plan T9.2/T9.4. DefleMask and rescomp accept
  them anyway, but an SGDK game would rarely spare that much ROM for one song.
- **Left for later: long DMFs.** The DAC copies (one per loudness step and start point, ADR 0044) make the biggest DMF
  1.3 MB. That's fine for DefleMask; the optimizer should weigh it (M9).

## Remaking the files

From the repository root:

```
dotnet build -c Release src/Mod2Xgm.csproj -o artifacts/t57
```

Then run `artifacts/t57/Mod2Xgm.exe tests/data/corpus/name.mod --output tests/golden/corpus-check/out/dmf/name.dmf` for
each song. In DefleMask, open each `out/dmf/name.dmf`, then File → Save VGM to `out/vgm/name.vgm` and File → Save WAV →
Export (multi-channel off, loop count 0) to `out/wav/name.wav`. Then, in this folder:

```
java -jar "%GDK%\bin\rescomp.jar" corpus.res out\corpus.s
```
