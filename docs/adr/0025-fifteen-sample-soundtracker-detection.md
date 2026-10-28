# ADR 0025: 15-sample Soundtracker detection (T2.3)

- **Status:** Accepted; supersedes the 15-sample rejection in [ADR 0020](0020-mod-reader.md)
- **Date:** 2026-10-27

## Context

Plan task T2.3: untagged MOD files may be 15-sample Soundtracker modules, which have no signature to check.

## Decision

- Untagged files are read as 15-sample Soundtracker modules with **relaxed** checks (David's choice): reject only
  what can't be read (header under 600 bytes, song length outside 1–128, a played order ≥ 128, patterns cut off),
  and report **every sign of junk** as a conversion note. Rejections give the bytes found at offset 1080 and the
  reason the 15-sample reading failed.
- Standing rule from David: always log assumptions and potential bad conditions. The reader notes the 15-sample
  assumption, the ambiguous byte 471, ambiguous loop starts, and header/pattern anomalies in both layouts.
- Loop starts stored in bytes: detected only when the words reading can't fit; a conversion note every time.
- Other 31-sample tags (`N.T.`, `M&K!`, `FEST`, `LARD`, `NSMS`) stay rejected for now, with the tag and tracker named.
- Ultimate vs. later Soundtracker is decided in M3, not in the reader; the reader keeps the bytes as stored.

## Consequences

Ultimate vs. later Soundtracker is decided by `--player` ([ADR 0032](0032-player-option.md)).
