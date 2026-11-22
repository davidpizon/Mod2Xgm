# ADR 0138: The model folder: checked by a models set and ONNX Runtime version, exit 7, zips made on a developer PC

- **Status:** Proposed; supersedes in part [ADR 0111](0111-neither-and-listening-models.md) (how a folder from another
  version is judged, the release workflow)
- **Date:** 2026-11-21

## Context

ADR 0111 lets the converter use pretrained listening models from an optional folder, with ONNX Runtime inside that folder
so the converter stays one Native AOT file. Plan T9.4s3 builds the folder's loading, its check, the line every run prints,
`--no-models`, the cache key and the models download. Three points were open, and David settled them (2026-11-21):

- ADR 0111 says a folder "made for another version of the converter is an error". Read literally, every release would
  make users download the 140 MB folder again, even when the models didn't change.
- Which exit code a folder that can't be used gets.
- ADR 0111 has the release workflow build the models download. The CLAP file is exported by `tools/onnx-export`, which
  downloads CLAP's weights and needs PyTorch.

## Decision

- **The folder says what it was made for, in `models.txt`:** the models set (a number the converter bumps when it needs
  other models or other exports of them; set 1 is CLAP), the ONNX Runtime version, and each model's file and SHA-256.
- **"Refuse only if it can't work"** (David). A converter accepts any folder whose models set and ONNX Runtime version are
  the ones it was built for, so a newer converter that kept the models keeps working with the old folder. Every listed
  file's SHA-256 is checked on each run, and ONNX Runtime is loaded from the folder and asked its version.
- **A folder without `models.txt` isn't set up:** it is left alone and nothing is said. The harness's folder of
  ADR 0104 (the same default path, ONNX files only) stays as it was until it is set up.
- **A set-up folder that can't be used is exit 7** (David), a new code, so scripts can tell it from a bad option. The
  message says what is wrong and that `--no-models` converts without it. JSON output calls it `model-folder`.
- **The converter references `Microsoft.ML.OnnxRuntime.Managed`**, the managed API only, with no native library. It loads
  `onnxruntime.dll`, `libonnxruntime.so` or `libonnxruntime.dylib` from the folder and points the managed API's imports at
  it (`NativeLibrary.SetDllImportResolver`), once per process. Native AOT publish has no trim or AOT warnings with it,
  the executable is still one file, and on win-x64 the converter ran CLAP from the folder and matched the export's own
  output (the check of plan T9.4s3, done on David's PC). CI's AOT smoke runs check loading on all three platforms.
- **The models zips are made on a developer PC, never on a CI or release machine** (David: "Do not download or run long
  computes on cloud machines. Do that work locally."). `tools/models-archive/New-ModelsArchive.ps1` takes ONNX Runtime
  from the local NuGet cache and CLAP from the folder `tools/onnx-export` wrote, and writes
  `mod2xgm-models-set<N>-<platform>.zip` for win-x64, linux-x64 and osx-arm64; David attaches them to a release by hand.
  `release.yml` doesn't change. With `-Install` the script sets up the folder on the PC it runs on.
- **Options made in code use no models.** The command line gives the default folder unless `--no-models` is given; the
  `ConversionOptions` that tests and the harness build have none unless they set one, so their results never depend on a
  PC's folder.
- **The cache key gains the models only where a result depends on them.** Nothing does yet: FM fits, PSG and PCM
  candidates are made without the models, so their keys stay as they are and the fits already cached stay valid. The
  first cached result the models change (plan T9.4s6) adds the models set to its key.

## Options considered

- **Chosen: models set and ONNX Runtime version, exit 7, zips made locally.**
- **The same release only** (ADR 0111 read literally). Simple to check, but a 140 MB download with every release. David
  chose "refuse only if it can't work".
- **Exit 1, as a usage error.** No new code, but scripts couldn't tell a broken folder from a bad option. David chose a
  new code.
- **The release workflow exports CLAP, or copies it from a fixed release.** Nothing to do by hand, but it downloads and
  computes on cloud machines, which David doesn't want.
- **The full `Microsoft.ML.OnnxRuntime` package in the converter.** It would put tens of MB of native libraries next to the
  executable, which ADR 0111 rules out.
- **Checking each model by running it on a test signal** (the export's `*.expected.txt`). It would catch a wrong ONNX
  Runtime build too, but it runs CLAP (seconds) on every conversion; the SHA-256 and the version check catch a damaged
  or mismatched folder for a fraction of that.

## Consequences

- `docs/cli.md` gains `--no-models`, exit 7 and a "Listening models" section; `docs/cli-output.md` the `model-folder` error.
- Every run with a set-up folder reads the models once to hash them (CLAP is 122 MB: well under a second on an SSD).
- The tests reference the full `Microsoft.ML.OnnxRuntime` package for its native library, which they copy into a test
  model folder; its version and the managed package's are kept equal, and a test checks it.
- ONNX Runtime, once loaded, stays loaded from the first folder for the life of the process.
- The CLAP checkpoint's licence is still marked "to verify" in `THIRD-PARTY-NOTICES.md`; it is checked before a zip is
  attached to a release.
- AGENTS.md's approved list: the converter now references the managed ONNX Runtime package and loads the native library
  from the model folder.
