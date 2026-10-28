# ADR 0041: `--require-pcm` becomes `--require-dac`; add `--require-psg`

- **Status:** Accepted; supersedes [ADR 0021](0021-require-fm-require-pcm.md)
- **Date:** 2026-10-28

## Context

[ADR 0021](0021-require-fm-require-pcm.md) added `--require-fm` and `--require-pcm`. The Genesis has three kinds of
voice (FM, the DAC and the PSG), and the other options talk about the DAC (`--no-dac`, [ADR 0040](0040-simple-mapper.md)),
so "PCM" no longer names the voice a sample is pinned to. There was also no way to pin a sample to the PSG.

## Decision

(David)

- `--require-pcm` is renamed `--require-dac`. Behavior is unchanged: the sample is always digitized PCM on the DAC.
  No alias is kept: the tool is unreleased.
- New `--require-psg <list>`: the sample is always played by the PSG, never FM or the DAC. The simple mapper uses the
  PSG noise channel; the tone channels stay with the optimizer.
- A sample may be in at most one of the three `--require-*` lists, and in none of them if it is in
  `--ignore-samples`. Every conflict is reported (exit 1). `--no-dac` conflicts only with `--require-dac`.
- Overlap rule, as for the DAC in ADR 0021: a required-PSG note that loses the noise channel to a louder note on the
  same row is dropped with a conversion note, never moved to FM.

## Options considered

- Rename and add the option (chosen).
- Keep `--require-pcm` as an alias: rejected, nothing has been released.
- Map `--require-psg` to the tone channels too: rejected for the simple mapper; a square wave can't play a recording
  and the choice of tone is the optimizer's job (plan M9).

## Consequences

`docs/cli.md`, `docs/sample-capacity.md`, `docs/multi-pass.md`, `README.md` and `AGENTS.md` use the new names. In code
`RequirePcm` became `RequireDac` and `RequirePsg` was added to `ConversionOptions` and `MappingOptions`. The exit-6
rule ("required PCM doesn't fit") now refers to `--require-dac`.
