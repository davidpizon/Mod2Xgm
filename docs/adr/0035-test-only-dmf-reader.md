# ADR 0035: Test-only DMF reader (T4.2)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T4.2: tests need to read DMF files back, before the writer exists.

## Decision

- **Tested now on hand-made files** (David, over waiting for the writer or using a DefleMask-saved file): a
  `DmfBuilder` test helper builds DMF bytes in code, and one test spells a file out byte by byte from the spec so the
  reader and the builder can't share a mistake. The round trip of real writer output becomes T5.2's test.
- **Reads file versions 24–27** (David, over 24 only), so the songs David saves in DefleMask for the probe files
  (T4.3) can be opened in tests: 25 adds pattern names, 27 sample cut points; 26 changes nothing for Genesis.
- **Strict:** every value DMF or DefleMask doesn't allow throws `InvalidDataException` with the field and byte
  offset, since the reader's job is catching writer bugs. Bytes after the last sample are counted, not rejected.
- Lives in `tests/` only; the shipped exe never reads DMF.

## Consequences

The writer's round trip is T5.2's test.
