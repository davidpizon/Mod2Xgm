# Testing

Test projects live in **`tests/`**, one folder each (see [`tests/README.md`](../tests/README.md)). The main one is
`tests/Mod2Xgm.Tests` (**xUnit v3**), referenced from the root `Mod2Xgm.slnx`. Run with `dotnet test`.

## Layers
1. **Reader unit tests** — build MOD bytes in code (a small `ModBuilder` test helper) and assert
   every field: variant detection, 15-sample heuristics, bad tags, truncated files, loop in bytes vs words.
2. **Model tests** — period→note, finetune, Fxx speed/BPM split, Bxx/Dxx flow and loop detection.
3. **Writer unit tests** — exact bytes for headers, tables, alignment, endianness; DMF decompresses
   with `ZLibStream` and re-parses with a test-only DMF reader.
4. **Golden-file tests** — whole conversions of generated MODs compared to checked-in expected outputs
   under `tests/golden/`. Updating goldens is a deliberate act (`UPDATE_GOLDEN=1 dotnet test`).
5. **CLI tests** — argument parsing (incl. `--ignore-samples` lists, ranges, hex), output naming, exit
   codes, no overwrite without `--force`, console report content (metadata, empty-slot names, statuses).

## Code coverage

`main` has a GitHub ruleset that requires **≥ 80 % line coverage** and **no more than a 10-point drop** versus
`main` on every pull request. Coverage comes from **coverlet.collector** (Cobertura XML) and is uploaded to
GitHub Code Quality by the `Code coverage` job in `.github/workflows/ci.yml` (`actions/upload-code-coverage@v1`,
needs `code-quality: write`). It runs on pushes to `main` (baseline) and on pull requests.

Measure locally:

```
dotnet test --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory artifacts/coverage
```

Only the `Mod2Xgm` assembly is measured. Don't exclude code from coverage to pass the gate — write the test,
or make the code testable (e.g. `Program.Run` takes the work as a delegate so the error path is covered).

## Accuracy harness (drives design decisions)

The goal is the most accurate **sounding** output, so it is measured, not guessed:

1. Render the MOD to WAV with a reference player (`openmpt123 --render` or libxmp).
2. Render the output: DefleMask WAV export for `.dmf`; emulator audio capture (BlastEm / Genesis Plus GX)
   of an SGDK test ROM for `.xgm` / `.xgm2`.
3. Align and compare with objective metrics — multi-resolution log-mel spectral distance and onset/timing
   error — plus a short A/B listening check.
4. Keep a small corpus of freely licensed MODs with scores per strategy; a change to mapping or consolidation
   must not lower the corpus score without a recorded reason in [decisions.md](decisions.md).

Unit tests for the capacity logic: build a song in code that needs more slots / voices / bytes than the
target allows and assert the capacity warning, the consolidation steps taken, and exit 6 with `--no-consolidate`.

External applications (openmpt123, DefleMask, SGDK, BlastEm, Genesis Plus GX) are allowed **here and in
`tools/` only** — the converter itself never launches another program.

## Manual / oracle checks (not CI)
- Open `.dmf` in DefleMask; play it. Export VGM from DefleMask and compare register activity when useful.
- Build an SGDK test ROM that plays the `.xgm` / `.xgm2`; run in BlastEm / Genesis Plus GX.
- Compare MOD playback reference with OpenMPT or libxmp.

## Test data rules
- **Do not commit copyrighted MOD files.** Generate modules in code, or use files with an explicit
  free license and record the license next to the file in `tests/data/LICENSES.md`.
- Keep fixtures small (< 100 KB each).
