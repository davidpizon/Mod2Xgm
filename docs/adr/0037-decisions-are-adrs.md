# ADR 0037: Decisions are recorded as ADRs

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Decisions were kept in one file, `docs/decisions.md`, newest first. At 36 entries it had grown long, entries couldn't
be linked to one by one, and nothing marked which ones later decisions replaced.

## Decision

- **Every decision is an Architecture Decision Record (ADR):** one file per decision in `docs/adr/`, numbered
  `NNNN-title.md` in the order they were made, from [template.md](template.md): Status, Date, Context, Decision,
  Options considered, Consequences (David).
- **All 36 existing entries were converted** (David, over "ADRs from now on" and "just this one"): each dated section
  of `decisions.md` became one ADR, oldest first, its text kept as written; a short Context and Consequences were
  added, and Status says which later ADR replaces it in part.
- `docs/decisions.md` stays as a short pointer to the index ([README.md](README.md)), so links to it keep working.
  The **open questions** move to the index.
- An ADR isn't edited once accepted, except its Status line: a changed decision gets a new ADR that says which one it
  supersedes.

## Options considered

- Convert everything to ADRs (chosen).
- ADRs only for new decisions, keeping the old log as it was.
- One ADR for the Furnace note only.

## Consequences

AGENTS.md and the docs point to `docs/adr/`. Converted ADRs keep their options inline ("David, over …") instead of an
"Options considered" section.
