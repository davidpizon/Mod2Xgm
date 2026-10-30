# Harness baseline: the simple mapper (plan T6.4)

The accuracy harness's scores for the simple mapper ([ADR 0040](../../../docs/adr/0040-simple-mapper.md)) on the corpus
([tests/data/LICENSES.md](../../data/LICENSES.md)), taken 2026-10-30. Later mappers, the optimizer (M9) first, are
measured against them. What they mean, and how far to trust them: [ADR 0049](../../../docs/adr/0049-simple-mapper-baseline.md).

| File | What it holds |
|---|---|
| `report.md` | The harness report: scores per song, the first-30-second scores, the average, the song timeline |
| `scores.csv` | Whole-song scores ([harness.md](../../../docs/harness.md#output) lists the columns) |
| `clip-scores.csv` | The same for each song's first 30 seconds: the audio David rated |
| `durations.csv` | The tick player's song timeline against libopenmpt |
| `listening.tsv` | David's ratings of the 30-second clips, `song<TAB>rating`, 1–10, 10 = sounds almost the same |

## How it was made

From the repository root, with libopenmpt 0.8.9 and Furnace installed ([harness.md](../../../docs/harness.md#setup)):

```
dotnet run --project tools/Mod2Xgm.Harness -c Release -- -o artifacts/harness-baseline --no-deflemask --clip 30
```

Then the four report files were copied here. Two earlier runs gave identical score and length files.

## Comparing a later run

Run the same command with another `-o` folder, then compare its `scores.csv` with this one. Look song by song, not only
at the average. A lower `mel_db` is closer. The score agrees only weakly with listening so far (ADR 0049), so for
changes that matter, also listen to the `wav/*.clip.wav` pairs.
