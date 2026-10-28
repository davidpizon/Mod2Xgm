# ADR 0028: Console report sections 1–2 (T2.5)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T2.5: the console report's metadata and sample table.

## Decision

- **Tempo line** reads the `Fxx` commands on the first row the song plays; defaults (speed 6, 125 BPM) fill in what
  that row doesn't set (David's choice over always printing the defaults or leaving the line out until M3).
- **Ignored samples** show `ignored` in the table now, and sample numbers the module doesn't have (16–31 in a
  15-sample file) are rejected with exit 1 after the report is printed (David). The check also covers
  `--require-fm` / `--require-pcm`, since cli.md gives them the same range rules.
- **No digit grouping** anywhere in the report (`182344`, not `182 344`) (David); cli.md's examples were updated.
- The doc's example table wasn't evenly aligned; the code uses one fixed column layout and the doc now shows its
  real output. Each line is logged as a property (`{ReportLine:l}`), so names with `{`/`}` print as stored.
- 15-sample files: the restart line notes that Ultimate Soundtracker reads that byte as the tempo; M3 decides.
- Golden files live in `tests/golden/`; with no NuGet access in the agent session they were generated from the
  report code itself and are checked by CI.

## Consequences

Report format in [cli.md](../cli.md#console-report).
