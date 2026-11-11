# ADR 0104: Pretrained listening models in the harness, through ONNX Runtime

- **Status:** Proposed
- **Date:** 2026-11-11

## Context

[ADR 0097](0097-learned-listening-score.md) lets distances from pretrained audio models be tried as extra features of the
learned listening score, in `tools/Mod2Xgm.Harness` only. Such a model goes into the converter only if it beats the
pure-C# features on the hold-out (plan T9.4l), and only after its own ADR shows it meets the library rules. Plan T9.4k4
tries them "through ONNX Runtime (David); the models kept outside the repository; an ADR for the library". Today's fit
([ADR 0101](0101-listening-score-fit.md)) has 15 answers from 15 songs, enough for one weight: envelope modulation
(10 of 10 a/b picks on songs left out, log-mel 6).

David answered the questions below on 2026-11-11. The details that follow from them are Claude's, recorded for David to review.

## Decision

**Which models (David): all four.**

| Model | What it is | Weights' licence | Rate | Chunk | Distance |
|---|---|---|---|---|---|
| `panns` | PANNs CNN14 (Kong et al. 2020), trained on AudioSet | CC BY 4.0 (code MIT) | 32 kHz | 2 s | cosine, 2048-value embedding |
| `vggish` | VGGish (Hershey et al. 2017), via torchvggish | Apache-2.0 | 16 kHz | 0.975 s | cosine, 128-value embedding before PCA |
| `clap` | LAION-CLAP (Wu et al. 2023), HTSAT audio tower | code CC0-1.0; **Verify** the checkpoint's | 48 kHz | 10 s | cosine, 512-value embedding |
| `cdpam` | CDPAM (Manocha et al. 2021), a learned perceptual distance | MIT | 22.05 kHz | 2 s | its own distance |

**How the models get here (David).** `tools/onnx-export/export_models.py` downloads each publisher's weights and writes
`<model>.onnx` to a folder outside the repository: .NET's `LocalApplicationData/Mod2Xgm/models` by default. Each ONNX
file takes plain mono audio at the model's rate, one chunk at a time, with the front end (STFT, mel, log) inside the
graph, so the harness has no feature code of its own per model. The script checks each file against PyTorch on a test
signal and writes the result to `<model>.expected.txt` for the C# tests.

**The library.** `Microsoft.ML.OnnxRuntime` 1.31.1 (MIT), referenced only by `tools/Mod2Xgm.Harness`. Its NuGet package
carries the native runtime for win-x64, linux-x64 and osx-arm64. The converter never references it.

**Chunk by chunk (David).** Both clips (the version's and the original's, each 20 s) are mixed to mono, resampled to the
model's rate, cut into whole chunks, and each chunk is compared with the same chunk of the original; the distances are
averaged. The clips are aligned to the sample, and the differences David hears are often short (one missing note), which
averaging the embeddings first could wash out or even cancel. A clip shorter than one chunk is padded with silence.

**How they are compared (David): a separate report.** `mod2xgm-harness --embeddings answers.tsv [--models folder]`
remakes the rows as `--fit` does, measures every answered row whose clips remake byte for byte with each model found, and
writes `listening/embeddings.tsv` (each clip's distances) and `listening/embeddings.txt`. For each set below it reports
the leave-one-song-out log-loss (with the best λ of the fit's four) and how often it guessed the a/b picks on songs left
out:

- log-mel alone;
- today's learned score (the features `ListeningWeights` weighs);
- each model alone;
- each model with today's features.

A set with more features than the answers allow weights (one per 10) is marked "over the cap": it is for comparison,
not a fit. `src/Scoring/ListeningWeights.cs` is never touched, since the converter can't run these models. A missing
model is listed and skipped.

**Tests (David).** Everything but the models runs in CI: chunking, the distances, the comparison and the run, with Moq
stand-ins for the models. The test that runs each real model through ONNX Runtime and checks its output against
`<model>.expected.txt` skips itself when the model folder is missing, as on the CI runners.

**If a model wins (David).** A new plan task, T9.4l2, after the hold-out decision: distil the winning model into a small
model written in pure C#, so the converter stays one Native AOT file with no ONNX Runtime and no large model file.

## Options considered

- **A separate report (chosen, David).** The answer to "is a pretrained model worth bringing into the converter?" without
  changing what the converter uses.
- **The models joining `--fit`'s forward selection.** The fit could then pick a feature the converter can't compute, and
  the weights written would differ from the fit reported.
- **Averaging each clip's embeddings, then one distance.** Steadier, and how the models summarize a recording, but a short
  difference can vanish before it is measured.
- **The user downloads ready-made ONNX files.** Fewer moving parts here, but there are no official ONNX releases of these
  checkpoints, and the front ends would differ from file to file.
- **Running the models in Python from the harness.** Launching other programs is allowed in the harness, but it would
  tie every run to a Python environment, and a model chosen later would still need ONNX Runtime or C# to reach the
  converter.
- **Shipping ONNX Runtime and a model in the converter now.** Breaks the single-file AOT binary and adds hundreds of MB;
  ADR 0097 asks for a hold-out win first.

## Consequences

- New `tools/onnx-export/` (script, requirements, README) and `tools/Mod2Xgm.Harness/Embeddings.cs`; `--embeddings` and
  `--models` in the harness ([harness.md](../harness.md#pretrained-models)); `ListeningFit.Trial` checks one fixed set.
- `Microsoft.ML.OnnxRuntime` in `Directory.Packages.props`, THIRD-PARTY-NOTICES.md and AGENTS.md's approved list,
  harness only.
- The comparison is run by hand on David's PC once the models are exported; its numbers go in the PR that adds them.
- Plan T9.4l2 (distillation) runs only if a model beats the C# features on the hold-out (T9.4l).
