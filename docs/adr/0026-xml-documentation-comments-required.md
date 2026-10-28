# ADR 0026: XML documentation comments required

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

David wants every type and public method documented, in `src/` and `tests/`.

## Decision

- David's rule: every class (record, struct, interface, enum) and every public method in `src/` and `tests/` has an
  XML doc comment, including internal types and test methods. Recorded in AGENTS.md (Code conventions).
- Not compiler-enforced yet: `CS1591` only covers publicly visible members, and the app's types are internal.

## Consequences

AGENTS.md § Code conventions.
