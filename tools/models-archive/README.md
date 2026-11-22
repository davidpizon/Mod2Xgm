# tools/models-archive

Makes the listening-models downloads the converter uses from its model folder
([docs/cli.md#listening-models](../../docs/cli.md#listening-models), [ADR 0138](../../docs/adr/0138-model-folder.md)), or
sets that folder up on this PC. It runs on a developer PC only, never on a CI or release machine.

## Before

1. Export the models with [`tools/onnx-export`](../onnx-export/README.md): it writes `clap.onnx` to
   `LocalApplicationData/Mod2Xgm/models`.
2. Restore the harness once, so ONNX Runtime's native libraries are in the NuGet cache:
   `dotnet restore tools/Mod2Xgm.Harness/Mod2Xgm.Harness.csproj`.

## Set up this PC

```powershell
pwsh tools/models-archive/New-ModelsArchive.ps1 -Install
```

It adds ONNX Runtime for this system, its licences, `NOTICE.txt` and `models.txt` next to the models. From then on every
conversion uses the folder and says so; `--no-models` converts without it.

## Make the zips for a release

```powershell
pwsh tools/models-archive/New-ModelsArchive.ps1
```

It writes `artifacts/models/mod2xgm-models-set<N>-<platform>.zip` for win-x64, linux-x64 and osx-arm64. Attach them to the
release by hand. Each holds `clap.onnx`, ONNX Runtime's native library for the platform, `models.txt`, `NOTICE.txt` and ONNX
Runtime's licence files.

`models.txt` lists the models set (`ModelFolder.ModelsSet` in `src/Models/ModelFolder.cs`), the ONNX Runtime version
(`Microsoft.ML.OnnxRuntime.Managed` in `Directory.Packages.props`) and each model's SHA-256. When either changes, make new
zips: older ones are refused with exit 7.
