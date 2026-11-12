# onnx-export

Exports the pretrained listening models the harness tries as extra features (plan T9.4k4,
[ADR 0104](../../docs/adr/0104-pretrained-embeddings.md)) to ONNX files the harness runs through ONNX Runtime. Run it
once per PC. The models are downloaded from their publishers and stay **outside the repository**.

| Model | Publisher's weights | Licence of the weights | Rate | Chunk | Output |
|---|---|---|---|---|---|
| `panns` | PANNs CNN14 (`Cnn14_mAP=0.431.pth`, Zenodo) via `panns-inference` | CC BY 4.0 (code MIT) | 32 kHz | 2 s | 2048-value embedding |
| `vggish` | VGGish via `harritaylor/torchvggish` | Apache-2.0 | 16 kHz | 0.975 s | 128-value embedding (before PCA) |
| `clap` | LAION-CLAP `630k-audioset-best.pt` via `laion-clap` | CC0-1.0 (code; the checkpoint per its Hugging Face repo `lukewys/laion_clap`) | 48 kHz | 10 s | 512-value embedding |
| `cdpam` | CDPAM via `cdpam` | MIT | 22.05 kHz | 2 s | distance between two clips |

## Run

Python 3.11 in its own virtual environment (the CLAP and CDPAM packages pin libraries newer Pythons lack). With
[uv](https://docs.astral.sh/uv/):

```bash
uv venv --python 3.11 tools/onnx-export/.venv
```

```bash
uv pip install --python tools/onnx-export/.venv -r tools/onnx-export/requirements.txt
```

```bash
tools/onnx-export/.venv/Scripts/python tools/onnx-export/export_models.py
```

(`.venv/bin/python` on Linux and macOS.) It writes `<model>.onnx` and `<model>.expected.txt` for each model into
`%LOCALAPPDATA%\Mod2Xgm\models` (Windows), `~/Library/Application Support/Mod2Xgm/models` (macOS) or
`~/.local/share/Mod2Xgm/models` (Linux), the harness's default `--models` folder; `--out` picks another. Name models to
export only those (`export_models.py panns cdpam`). A model that fails is reported and the others still export.

The script checks each ONNX file against PyTorch on a test signal and stops that model when they differ by more than
0.1 %. `<model>.expected.txt` keeps ONNX's answer, which the harness's tests compare with C#'s (they skip when the
folder is missing, as in CI).

CLAP's and CDPAM's checkpoints are older than PyTorch 2.6's `weights_only` loader: CLAP's holds NumPy scalars, and
CDPAM's is in the legacy (pre-zip) format that loader can't read. Rather than turning the check off, the script loads
both through an unpickler that accepts only the names those files use (`LISTED_GLOBALS`: tensors, storages, an ordered
dictionary and NumPy scalars, listed with `pickletools`, which runs nothing) and refuses any other. PANNs' package fetches
its files with `wget`, which Windows lacks, so the script downloads them to `~/panns_data` itself.

Downloads: about 1 GB of packages and 1 GB of weights. The `.venv` folder is git-ignored.
