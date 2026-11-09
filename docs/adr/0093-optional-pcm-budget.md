# ADR 0093: The PCM budget is optional; by default XGM and XGM2 hold what the format can

- **Status:** Accepted; supersedes the default budget of [ADR 0006](0006-pcm-budget-and-mid-song-swapping.md)
- **Date:** 2026-11-08

## Context

[ADR 0006](0006-pcm-budget-and-mid-song-swapping.md) gave every XGM and XGM2 song a default PCM budget of 128 KB, 3.125 % of
a 4 MB cartridge. `TargetLimits` ([ADR 0085](0085-target-limits.md)) treats it as a hard limit: the shrinker
([ADR 0091](0091-shrinking-recordings.md)) squeezes recordings to fit it, and notes that still don't fit move to FM/PSG or
are cut.

Most of the corpus asks for more: on XGM2, 17 of 19 songs (ADR 0091). neon_techno asks for about 977 KB. So with the
default, nearly every song lost sound to a number no Genesis song actually has to meet.

While planning T9.4b, David pointed out that a Genesis song has no fixed sample budget. 128 KB is a suggested target, and
a user who wants stricter down-sampling can ask for it.

## Decision

(David, 2026-11-08)

- **No default budget.** Without `--max-pcm-bytes`, XGM and XGM2 hold only what the format can address:
  16,776,960 bytes (65,535 blocks of 256, [ADR 0085](0085-target-limits.md)). Nothing is shrunk, moved or cut just to save
  bytes.
- **`--max-pcm-bytes` sets a budget when the user wants one,** with the same behaviour as before: capacity note, then
  consolidation, or exit 6 with `--no-consolidate`. `--max-pcm-bytes 128K` gives back the old default.
- A budget over the format's ceiling is still cut to it, with a conversion note. No budget at all adds no note.
- DMF is unchanged: it has no byte limit.
- `PreMixer`'s candidate cap ([ADR 0078](0078-pre-mix-budget-cap.md)) keeps 128 KB as its default
  (`PreMixer.DefaultMaxBytes`), so the harness's `--pre-mix` run stays as fast as that ADR made it. It no longer comes from
  the CLI default.
- Its own PR, before T9.4b (David). T9.4b's "make room by shrinking" question then matters only when a budget is given.

## Options considered

- **No default budget; `--max-pcm-bytes` opt-in** (chosen).
- **Keep 128 KB, but only as a note:** the song may go over it, and a note says so. Lost because the number has no meaning
  for the hardware, so the note would be noise on most songs.
- **Keep 128 KB as a hard default** (ADR 0006): most songs lose sound to it.

## Consequences

- `ConversionOptions.MaxPcmBytes` is `long?` (`null` = none); `TargetLimits.For` takes `long?`.
  `ConversionDefaults.MaxPcmBytes` is gone.
- [cli.md](../cli.md), [sample-capacity.md](../sample-capacity.md), [multi-pass.md](../multi-pass.md),
  [harness.md](../harness.md) and [ADR 0085](0085-target-limits.md)'s table say so. ADR 0006's Status line points here.
- A plain `--format xgm2` conversion now keeps every recording that fits the voices and slots, so its output can be much
  bigger than before. A game that shares the cartridge sets `--max-pcm-bytes`.
- The T9.4a survey rounds were run with a budget, and their choices still apply when one is set.
