# ADR 0111: "Neither" answers, retries, and listening models the converter loads from an optional folder

- **Status:** Accepted (David, 2026-11-14); supersedes in part [ADR 0104](0104-pretrained-embeddings.md) (the converter
  may now load the models) and adds to [ADR 0106](0106-cant-tell-smaller-wins.md) and [ADR 0110](0110-price-ruler.md); superseded in part by
  [ADR 0138](0138-model-folder.md) (a folder from another version, the models download)
- **Date:** 2026-11-14

## Context

Sometimes both versions of a blind A/B pair are unacceptable. The answers file (`tests/golden/listening/answers.tsv`)
has only `a`, `b` and `same`, so David marks such a pair `same` and says so in his words, for example "both FM voice
choices for the melody are completely unacceptable". But `same` means "either is fine, and the smaller file wins"
(ADR 0106). Those rows count as wins for the smaller file, and ADR 0110's "can't hear it" line, measured on the `same`
rows, would come out far too wide. David wants unacceptable versions to "go back and try again", so the converter learns
to adapt to different situations rather than repeating one rule everywhere.

The pretrained models of ADR 0104 (CLAP came closest) were weak at ranking small differences, and the learned score built
on the C# features lost its hold-out ([ADR 0107](0107-learned-score-not-adopted.md)). "Unacceptable" is a different
question: is this still the same instrument at all? That is what models trained on large sound collections are good at.
ADR 0104 kept them in the harness only: the converter never referenced ONNX Runtime. David asked what more they could do
in the converter, and for a model that works on any song, not only on samples the harness has already seen.

## Decision

David, 2026-11-14.

### Answers and retries

- **A fourth answer, `neither`:** both versions are unacceptable. It scores as a large loss for both, so size never
  decides it, and it is left out when ADR 0110's tie line is measured. `same` keeps ADR 0106's meaning.
- **Old rows.** Each `same` row whose words say both were unacceptable is relabelled `neither`, with the words kept,
  once David confirms that row.
- **Retry.** A `neither` puts the song and its section on a retry list. The harness builds different kinds of fixes for
  that place, not small changes of the rejected ones: keep the recording and give up a quieter sound, a pre-mix, another
  FM patch, a PSG layer. The two most promising go into the next round, until one passes or David says none can.
- **What the retries teach.** Each `neither` is a label for the "acceptable?" check below and data for ADR 0110's
  missing-instrument and voice penalties.

### Listening models in the converter

- **An optional model folder.** The converter loads pretrained listening models from a folder: by default .NET's
  `LocalApplicationData/Mod2Xgm/models`, as ADR 0104's harness already does. **It uses them whenever that folder has
  them** (David); no option is needed to turn them on.
- **ONNX Runtime lives in the model folder**, not next to the executable. The converter stays one Native AOT file and
  loads ONNX Runtime's native library from the folder only when the folder is there. A folder that is incomplete or made
  for another version of the converter is an error that says what is wrong, never a silent fallback.
- **A separate download** (David). Each release gets a models archive per platform: the ONNX files, ONNX Runtime and their
  licences. The tool never touches the network, so the user downloads and unzips it by hand. `tools/onnx-export` stays to
  remake the ONNX files. The first archive holds CLAP only (CC0); PANNs (CC BY 4.0) needs its attribution if it joins.
- **Always said.** Every run that uses the models prints which folder and which models, so two different results from
  the same MOD can be explained. With `--progress silent` nothing is printed, as for everything else.
- **An off switch, `--no-models`,** gives today's result whatever the folder holds. Tests and CI run with it, so the
  byte-exact tests stay exact. This is a new CLI option ([cli.md](../cli.md) gains it with the code).
- **Repeatable on one PC:** one thread, the CPU only, distances rounded before they decide anything, and the candidate
  cache ([ADR 0074](0074-candidate-cache.md)) keyed by the models' version. Two PCs may still differ in the last digits,
  and the ADR doesn't promise the same file across PCs when the models are used.
- **General, not looked up.** The models run on the song being converted. Labels computed ahead of time by the harness,
  which would only work on samples it has seen, were turned down (David).

### What the models do, in this order

1. **The "acceptable?" check and retry.** After planning, each section of each instrument is compared with the MOD: is it
   still the same instrument? Where it isn't, the converter tries a different kind of fix and keeps the one that passes.
   It learns where "no" begins from the `neither` answers.
2. **Instrument labels.** CLAP compares each sample with text such as "voice", "snare drum", "piano", "orchestra hit",
   and the allocator's rules can adapt to the label, for example never giving a voice to FM ("not even close to the same
   voice", said of `--mapping simple` in an earlier round).
3. **FM patch choice.** Fitted FM patches are chosen between by CLAP distance as well as log-mel: the shelved T9.4m idea,
   with CLAP as the ruler instead of the learned score.

### The gate

Each use is proven in the harness first, on new hold-out songs (picked in T9.4p) and in blind rounds, before the
converter acts on it. Until a use passes, the converter may compute it and print it, but it changes no plan.

## Options considered

- **An optional folder, used whenever it is there, with ONNX Runtime inside it (chosen).** Users without the models keep
  the single file and today's results; users with them get everything in one place.
- **Only with an option such as `--models <folder>`.** Every result would be today's unless asked for, but most users
  would never find it. David chose the folder being enough.
- **ONNX Runtime next to the executable.** Simpler to load, but every release ships tens of MB that most users don't need,
  and the tool is no longer one file.
- **Users run `tools/onnx-export` themselves.** No large release files, but it needs Python and PyTorch.
- **Labels computed ahead of time by the harness.** No runtime in the converter, but it only knows samples the harness
  has seen (David: not acceptable).
- **A small model distilled into pure C#** (shelved T9.4l2). One file and repeatable everywhere, but it only learns what
  it was trained on, and it is a much larger piece of work. It stays possible later if the folder proves its worth.
- **Keep the models in the harness only** (ADR 0104). The converter could never adapt with them.

## Consequences

- ADR 0104's Status line says it is superseded in part: the converter may now load ONNX Runtime and the models from the
  model folder. ADR 0106 and ADR 0110 gain `neither`.
- AGENTS.md's approved list: ONNX Runtime may be loaded by the converter from the model folder, never bundled in the
  executable. Its Native AOT and trimming support on win-x64, linux-x64 and osx-arm64 is **Verify**: checked in the
  first code task, with the AOT publish and smoke run in CI.
- Plan tasks (T9.4n1 is this ADR):
  - **T9.4s1:** `neither` in the answers file, its README and the fit; old rows relabelled with David.
  - **T9.4s2:** the harness's retry list, building different kinds of fixes, with CLAP screening out tries that look as
    far from the MOD as the rejected ones.
  - **T9.4s3:** the converter's model folder: loading ONNX Runtime from it, the version check, the printed line,
    `--no-models`, the cache key, the models archive in the release workflow.
  - **T9.4s4–T9.4s6:** the "acceptable?" check and retry, instrument labels, FM patch choice; each through the gate.
- T9.4m stays shelved as written; its idea returns as T9.4s6 with CLAP.
- Releases grow a second download per platform. `release.yml` changes in T9.4s3.
