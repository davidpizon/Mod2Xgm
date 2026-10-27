# AGENTS.md

Guidance for AI coding agents (and humans) working in this repository.

## What this project is

**Mod2Xgm** is a command-line converter that turns Amiga **MOD** music files (tracker modules with
digitized/PCM sample instruments) into music for the **Sega Genesis / Mega Drive**:

- **`.dmf`** — DefleMask module (Genesis system), editable in DefleMask. Default output.
- **`.xgm`** / **`.xgm2`** — SGDK XGM / XGM2 driver data, playable directly on hardware via SGDK.
  Written by a **native writer** in this app (no dependency on `xgmtool` / `xgm2tool`).

**Goal: the most accurate *sounding* rendition of the MOD.** When choices conflict, sound accuracy beats
output size, DefleMask editability and implementation simplicity. "Accurate" is measured with the accuracy
harness in [`docs/testing.md`](docs/testing.md), not assumed.

The target hardware is the **YM2612** FM chip (6 channels; channel 6 can be switched to an 8‑bit DAC
for PCM) plus the **SN76489** PSG (3 square + 1 noise).

```
mod2xgm song.mod                  # -> song.dmf next to the input
mod2xgm song.mod --format xgm     # -> song.xgm
mod2xgm song.mod --format xgm2    # -> song.xgm2
```

See [`docs/cli.md`](docs/cli.md) for the full command-line contract.

## Status

Early scaffold. `src/Program.cs` only sets up Serilog. The design lives in
[`docs/`](docs/README.md); read it before writing code. Items marked **(proposed)** or **open** in the docs
are not settled yet. **Work from [`docs/implementation-plan.md`](docs/implementation-plan.md)**: one task
(`T#.#`) per PR, in milestone order, meeting its acceptance criteria. Do not silently pick an answer; raise it or record it in
[`docs/decisions.md`](docs/decisions.md).

Required behavior on every run (see [`docs/cli.md`](docs/cli.md#console-report) and
[`docs/sample-capacity.md`](docs/sample-capacity.md)):

1. **Whenever a MOD file is converted**, read and print the **MOD metadata** and the **full sample table**
   (all sample/instrument names, including empty slots, which often hold credits) right after reading —
   before mapping, for every output format, with no option to turn it off — then the output
   instrument/sample table.
2. Log a **capacity warning** if the samples exceed what XGM/XGM2 (or DMF) can hold.
3. By default **consolidate** to fit — dedupe, trim, merge, and even swap a track's sound mid-song — logging
   each change as a conversion note. `--no-consolidate` fails (exit 6) instead.
4. Honor `--ignore-samples <list>` to leave chosen samples out.
5. With `--format xgm2`, write `.xgm2` and print exactly `NOTE: SGDK expects XGM2 files to use the .XGM file extension.` (see [`docs/cli.md`](docs/cli.md)).

Mapping: a **simple mapper** for the first DMF release, then the **multi-pass optimizer**
([`docs/multi-pass.md`](docs/multi-pass.md)) that fits FM/PSG/PCM candidates per instrument, allocates them
within the target's limits, and renders/refines in-app. Both implement `IChannelMapper`. In-app rendering
(MOD renderer, YM2612 port of ymfm, SN76489, PCM driver model) and scoring are **pure C#** — no native
libraries for these.

## Repository layout

```
/AGENTS.md                  this file
/Mod2Xgm.slnx               solution (XML .slnx format) — src/ and tests/ projects
/Directory.Build.props      settings shared by every project (net10.0, nullable, warnings-as-errors, analyzers)
/Directory.Packages.props   central package versions — PackageReference items carry no Version
/.gitattributes             line-ending normalization (LF in the repo)
/docs/                      design and format documentation (start at docs/README.md)
/src/Mod2Xgm.csproj         the console app (Native AOT)
/src/Program.cs             entry point
/tests/                     test projects, one folder each (see tests/README.md)
/tests/Mod2Xgm.Tests/       xUnit v3 tests for the app
/.vs/                       Visual Studio state — never edit or commit (git-ignored)
```

Planned source layout (proposed — see [`docs/architecture.md`](docs/architecture.md)):

```
src/Cli/            argument parsing, exit codes
src/Mod/            MOD reader -> ModModule
src/Model/          format-neutral song model (IR)
src/Mapping/        IR -> Genesis channel plan (strategy interface) + SampleBudgeter (capacity/consolidation)
src/Audio/          resampling, bit-depth conversion, loop handling
src/Render/         in-app MOD renderer + YM2612 (ymfm port) / SN76489 / PCM driver models (pure C#)
src/Scoring/        spectral + onset metrics, shared with tools/Mod2Xgm.Harness
src/Optimizer/      multi-pass candidates, allocation, refinement
src/Dmf/            DMF writer
src/Xgm/            XGM and XGM2 writers
tests/Mod2Xgm.Tests/
```

## Build, run, test

Requires the **.NET 10 SDK**. All commands run from the repo root.

```bash
dotnet build                                                # whole solution (Mod2Xgm.slnx)
dotnet test                                                 # all test projects under tests/
dotnet run --project src/Mod2Xgm.csproj -- path/to/song.mod
dotnet publish src/Mod2Xgm.csproj -c Release -r win-x64     # Native AOT single exe
```

After a change, at minimum: `dotnet build` and `dotnet test` must pass. If you touched anything
reflection- or serialization-adjacent, also run the AOT `publish`.

## Every warning is an error

`Directory.Build.props` sets `TreatWarningsAsErrors`, `MSBuildTreatWarningsAsErrors`, `AnalysisLevel=latest`
and `EnforceCodeStyleInBuild` for **every** project; the app additionally sets `IsAotCompatible` (trim/AOT
analyzers at build time, IL2xxx / IL3xxx). Any
compiler, analyzer, trim/AOT, NuGet or MSBuild warning fails the build.

- Fix the cause. Do **not** add `<NoWarn>`, `#pragma warning disable`, `[SuppressMessage]`,
  `[UnconditionalSuppressMessage]`, or lower a rule's severity in `.editorconfig` without the user's
  explicit approval — and then only for a single, narrowly scoped site with a comment explaining why.
- Never turn `TreatWarningsAsErrors` off, locally or in CI.
- New projects inherit these automatically from `Directory.Build.props`; don't override them in a project file.
- Package versions go in `Directory.Packages.props` only.

## Git workflow

- **Never push to `main`.** Every change goes on a branch and into a **pull request**; David reviews and merges.
- Branch names: `feat/…`, `fix/…`, `docs/…`, `chore/…`, `test/…`. For plan work, include the task id:
  `feat/t2-2-mod-reader`.
- One plan task (`T#.#`) per PR; put the task id in the PR title ("T2.2: MOD reader").
- Keep `main` buildable: a PR merges only with green CI (once CI exists, T0.4).
- Line endings are normalized by `.gitattributes`; don't commit line-ending-only changes.

## SGDK compliance

**SGDK compliance is the standard.** Every output must be playable through SGDK:

- `.xgm` (XGM) and `.xgm2` (XGM2) must be accepted by SGDK's rescomp and play with `XGM_startPlay` / `XGM2_play`
  exactly as specified in SGDK's `xgm.txt` / `xgm2.txt`.
- `.dmf` must open in DefleMask **and** survive DefleMask's VGM export → SGDK rescomp (`XGM`/`XGM2`).
- Never use a hardware feature the target SGDK driver doesn't support (e.g. CH3 special mode is allowed for
  XGM2 and DMF; for XGM v1 only after a probe ROM confirms it, because register `$27` also controls the YM timers).
  Both specs are saved in `docs/formats/` (`xgm.txt` v1.01, plus a summary of `xgm2.txt` in `xgm.md`).
- Respect driver conventions; `--reserve-sfx-channels` leaves PCM channels for game sound effects.

## Hard constraints

1. **Native AOT and trimming must keep working** (`PublishAot=true`). No reflection-based
   serialization, no `dynamic`, no `Assembly.Load`, no runtime code generation. If you add JSON, use
   `System.Text.Json` source generators.
2. **External libraries are welcome; external applications are not.** The converter may use NuGet
   packages and native libraries (C/C++ via P/Invoke) in-process. It must **never launch another program**
   (no `Process.Start`, no shelling out) for conversion. External applications (openmpt123, DefleMask,
   SGDK, BlastEm, Genesis Plus GX, …) are allowed **only in tests and the accuracy harness** (`tests/`, `tools/`).
   Rules for adding a library:
   - It must work under **Native AOT on win-x64, linux-x64 and osx-arm64**. Native libraries must be
     available (or buildable in CI) for all three, preferably statically linked into the AOT binary
     (`DirectPInvoke` + `NativeLibrary` items) so the tool stays a single file.
   - License: permissive (MIT, BSD, zlib, Apache-2.0) is fine. LGPL only as a separately loaded native
     library, not statically linked. GPL needs David's approval. Record the license in `docs/decisions.md`.
   - Pin the version in `Directory.Packages.props` and log the addition in `docs/decisions.md`.
   Approved so far: **Serilog**, **Serilog.Sinks.Console**, **System.CommandLine**; test-only **xUnit v3**.
   The BCL still covers the basics: `ZLibStream` (DMF is zlib-compressed), `BinaryPrimitives` (endianness), `Span<T>`.
3. **Local-only.** The tool never touches the network.
4. **Never modify the input file.** Output goes next to the input unless `--output` is given. Do not
   overwrite an existing output without `--force` (proposed).
5. **Endianness is explicit.** MOD is **big-endian**; DMF and XGM are **little-endian**. Always read and write
   through `BinaryPrimitives` with the endianness spelled out — never `BitConverter`.
6. `InvariantGlobalization=true`: format numbers with `CultureInfo.InvariantCulture` and do no
   culture-sensitive string work.

## Code conventions

- C# latest, file-scoped namespaces, `Nullable` enabled — no `!` suppressions without a comment why.
- Keep parsing, the format-neutral model, mapping and writing in separate layers. Writers never see MOD
  types; the reader never knows about Genesis.
- Readers validate and throw a single domain exception type (e.g. `ModFormatException`) with the byte
  offset and a human-readable reason. The CLI turns it into an exit code; no stack traces for user errors.
- Lossy conversion is expected. Every lossy decision (dropped effect, clipped note range, resampled or
  truncated sample, channel collision) must produce a **conversion note**, never be silent. (The word
  "warning" is reserved for build warnings, which are errors.) Conversion notes are collected in a
  `ConversionNotes` list and **logged to the console at Serilog `Warning` level**.

## Logging (Serilog)

- Packages: `Serilog`, `Serilog.Sinks.Console`. Configure **in code** in `Program.cs` — no
  `Serilog.Settings.Configuration` / `appsettings.json` (reflection-based, not AOT-safe).
- Use the static `Log` (or an `ILogger` passed in) — no `Console.WriteLine` for diagnostics.
- Levels: `Debug` = detail shown with `--verbose`; `Information` = normal progress/summary;
  `Warning` = conversion notes; `Error` = failures that set a non-zero exit code; `Fatal` = unexpected crashes.
- Message templates with **named scalar properties** (`Log.Warning("Sample {Sample} truncated to {Length} bytes", name, len)`).
  No `{@obj}` destructuring of complex types — it relies on reflection and breaks under trimming/AOT.
- Levels `Warning` and above go to **stderr**; below to stdout.
- Always `Log.CloseAndFlush()` before exit.
- Magic numbers that come from a file spec get a named constant and a comment pointing at the doc
  section (e.g. `// docs/formats/mod.md#sample-header`).
- Prefer `ReadOnlySpan<byte>` over streams for parsing; the inputs are small (usually < 1 MB).

## Testing expectations

See [`docs/testing.md`](docs/testing.md). In short: unit tests for every reader/writer field, round-trip
tests where the format allows it, golden-file tests for whole conversions, and never commit copyrighted
MOD files — generate test modules in code or use files with a clear free license.

**Coverage requirement (enforced by you, the agent — not by GitHub):** every pull request must keep line
coverage of the `Mod2Xgm` assembly at **≥ 80 %** and must not lower it by **more than 10 points** versus `main`.

1. Before opening or updating a PR, measure coverage locally (command in [`docs/testing.md`](docs/testing.md#code-coverage))
   on your branch, and on `main` when your change could lower it.
2. If it's below 80 % or dropped more than 10 points, add tests — or make the code testable — before opening the PR.
3. State the number in the PR description, e.g. `Line coverage: 93.4 % (main: 95.0 %)`.
4. Check the `Line coverage` notice from the CI `Code coverage` job matches; if it fails the requirement, fix it in
   the same PR.
5. Never exclude code from coverage (attributes, runsettings filters, `[ExcludeFromCodeCoverage]`) to meet the number.

## Documentation rules

- If you change behavior, CLI flags, or a format detail, update the matching file in `docs/` **in the same
  change**.
- Record design decisions (especially channel mapping) in [`docs/decisions.md`](docs/decisions.md) with
  date, options, and the reason.
- Format docs carry a **"Verify"** marker on anything not yet checked against the primary spec. Remove
  the marker only after checking against the spec or a real file opened in DefleMask / played via SGDK.

## When unsure

Ask instead of guessing on: channel mapping strategy, anything that changes the CLI contract, a
library with a non-permissive license or no AOT support on all three platforms, or dropping support for a MOD variant listed in `docs/formats/mod.md`.
