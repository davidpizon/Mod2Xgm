# ADR 0039: DMF writer and its song model (T5.2)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T5.2: write `.dmf` files. The test-only reader ([ADR 0035](0035-test-only-dmf-reader.md)) already has a
model of a DMF file (`DmfFile` and the records under it) that lives in `tests/`. The mapper (T5.3) will have to
produce the same thing, and no mapper exists yet.

## Decision

- **The DMF model moves to `src/Dmf/DmfFile.cs`** (David, 2026-10-28, over a second copy in `src/`). The shipped app,
  the writer, the test reader and the test builder share one set of types; the reader and builder stay in `tests/`.
- **`DmfWriter.Write(DmfFile, ConversionNotes)` returns the zlib-compressed bytes**; `Serialize` returns them before
  compression. It always writes **file version 24**, with no pattern names and no sample cut points, so a `DmfFile`
  carrying other values (version, pattern names, cut points, trailing bytes) is refused rather than half-written.
- **The writer checks every value against the format's limits first** and throws `ArgumentException` naming the
  field: a song that breaks one is a bug in whoever built it (a mapper bug is exit 5, not a conversion note). The
  limits are the ones the strict test reader enforces.
- **Text is the one thing it changes**: names and authors go out as printable ASCII, other characters become `?`, and
  text over 255 characters is cut, each with a conversion note ([docs/formats/dmf.md](../formats/dmf.md#container)).
- **The custom Hz digits come from `DmfTiming.CustomHz`**; with no rate they repeat the PAL/NTSC rate (50/60), since
  the field is written even when custom Hz is off.
- **Tests:** one song spelled out byte by byte from the spec; every probe read with the reader and written back must
  equal `DmfBuilder`'s bytes (decompressed, since zlib output may differ between platforms); a round trip of a song
  using every section; and one test per refusal.
- **Scope: writer only** (David, over also writing a silent `.dmf` from the CLI). The CLI writes no file until the
  mapper (T5.3) gives it a song.

## Options considered

- Move the model to `src/` (chosen): one set of types, nothing to keep in sync.
- Copy the model into `src/`: the two copies would drift, and the round trip would compare a type with itself anyway.
- Let the writer fix bad values (clamp, drop): hides mapper bugs, and a clamped value is a silent lossy change.
- Write the version the model carries: more code paths, none of which DefleMask 1.0 needs.

## Consequences

- T5.3 builds a `DmfFile`; T5.5 fills its effect columns; T5.4 sets its timing. All go through this writer.
- Writer output opens in DefleMask only once T5.7 checks it; the probe files (T4.3) already showed that bytes equal to
  the builder's do.
- `docs/formats/dmf.md` and the plan are updated in the same PR.
