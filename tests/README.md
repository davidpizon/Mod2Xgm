# Tests

Test projects live here, one folder per project:

```
tests/
  Mod2Xgm.Tests/      xUnit v3 unit, golden-file and CLI tests for src/Mod2Xgm.csproj
```

- Every test project is added to the root `Mod2Xgm.slnx` and inherits `Directory.Build.props`
  (warnings are errors) and `Directory.Packages.props` (central package versions).
- Name new projects `Mod2Xgm.<Area>.Tests` (e.g. `Mod2Xgm.Harness.Tests`).
- Test data goes under `tests/data/`; never commit copyrighted MOD files (see `docs/testing.md`).

Run everything from the repository root:

```
dotnet test
```
