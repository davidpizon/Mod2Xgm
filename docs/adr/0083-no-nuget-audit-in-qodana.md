# ADR 0083: No NuGet audit inside the Qodana scan

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Qodana builds the solution with the SDK inside its linter image, not the one CI installs. The
`jetbrains/qodana-cdnet:2026.2` image of 2026-10-29 has SDK 10.0.401, which restores
`Microsoft.DotNet.ILCompiler` 10.0.12 because the app sets `PublishAot`. Advisory
[GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9) (fixed in 10.0.14) came out on
2026-11-03. Under warnings-as-errors `NU1903` then stopped the restore, so the analysis exited with code 4 and
wrote no SARIF report. The Qodana job on pull requests and `main`, and the nightly audit's Qodana scan, all
failed, while CI, on a current SDK, passed.

Any later .NET advisory can cause the same failure until JetBrains rebuilds the image.

## Decision

(David)

- Set `NuGetAudit=false` for the Qodana scan only, in `code_quality.yml` and `nightly-audit.yml`, passed into
  the linter container with the action's `env-vars-to-linter` input.
- Change nothing in `Directory.Build.props` or the project files. Every other build keeps auditing packages, with
  `NU19xx` as an error.

## Options considered

- **Turn off the audit in the scan (chosen).** The scan publishes nothing and never runs ILCompiler, so its restore
  adds no dependency check that CI lacks, and an image that lags can no longer stop it.
- **Use the `2026.3-eap` image.** Its build date suggests a fixed SDK, but it is a pre-release linter, its findings
  may shift, and the failure returns the next time an image lags.
- **Pin `Microsoft.DotNet.ILCompiler` 10.0.14 explicitly.** Clears `NU1903`, but ILCompiler's targets warn "Delete
  explicit 'Microsoft.DotNet.ILCompiler' package reference", which fails AOT publish. It would also need a suppression
  and a manually maintained version.
- **Wait for JetBrains to rebuild the image.** Changes nothing here, but leaves the scans red for an unknown time.

## Consequences

- Package vulnerabilities are still caught by CI's restore and by `dotnet list package --vulnerable` in the
  nightly audit, which files `audit:vulnerability` issues. Those run on the SDK that `setup-dotnet` installs.
- The scan may analyze against an older SDK than CI uses. This can affect its findings, but not what ships.
- The action ignores `env-vars-to-linter` in native mode, where the linter inherits the environment anyway, so this
  keeps working if the scan mode changes.
