# ADR 0040: Simple mapper, `--no-dac` and the Genesis plan (T5.3)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T5.3: the first mapper. It decides which Genesis channel plays each MOD note and with which sound, before
the optimizer (M9) exists and before the scoring tools (M6) can say what "adequately represented by FM" means.

## Decision

All by David, 2026-10-28, unless noted.

- **Scope: a plan only.** `SimpleMapper : IChannelMapper` turns a `Song` into a `GenesisPlan` (`src/Mapping`). Building
  the DMF file from the plan (T5.4 timing, T5.5 effects, and the layout step) comes next. The CLI doesn't call the
  mapper yet.
- **The plan is per order, not per song pattern.** What a note sounds like depends on the instrument and volume its
  channel carried in, which differs each time an order list repeats a pattern, and the DMF writer wants one pattern
  per matrix row anyway. Song-wide effects (jumps, breaks, loops, speed, tempo, stop) go to a per-row `Flow` list;
  other effects stay with the channel's sound, untranslated (T5.5).
- **Routing is per instrument, by a stand-in rule** until the scoring tools exist: a sample that **loops** is a sustained
  tone and goes to **FM**; a sample that **plays once** is a recording the synthesizer can't copy and goes to the
  **DAC**. "Any sample FM and PSG can't represent adequately is a DAC candidate" is the principle; the rule is its
  stand-in.
- **The DAC takes notes from any MOD channel**, not a whole channel. It plays one sample at a time and can't change
  pitch per note, so each DAC sample plays at the rate of the pitch the song uses it at most often.
- **Same-row clashes: the louder note keeps the shared channel** (the lower MOD channel on a tie). The loser moves to
  the PSG noise channel if its sample is noise, else to FM; a `--require-dac` loser is dropped
  ([ADR 0021](0021-require-fm-require-pcm.md), as renamed by [ADR 0041](0041-require-dac-require-psg.md)). Mixing the clashing samples into one new sample is a **different
  candidate** for the optimizer, to be compared by rendering (David: the app generates several candidate songs and a
  later step picks the one that sounds closest; plan T8.5 and M9). It is not done here.
- **`--no-dac` is a new switch** (a CLI contract change): nothing uses the DAC. One-shots that are noise go to the PSG
  noise channel, the rest to an FM "Pluck". Together with `--require-dac` it is a usage error (exit 1).
- **Five built-in FM patches** (Sine, Organ, Lead, Brass, Pluck), picked by a sample's brightness. They are
  placeholders, judged by ear in T5.7 and replaced by fitted patches in M8. `SampleTraits` supplies brightness and a
  noise test (autocorrelation) for this and nothing else.
- **Conversion notes:** one per played instrument whose routing is lossy (said when first played), and counts for notes
  left out or moved. A requirement on an empty or never-played sample gets a note and has no effect.
- **DAC level:** quiet notes sound worse on the YM2612 DAC ([genesis-audio.md](../genesis-audio.md)); the plan carries
  each DAC note's volume so the sample step can keep samples loud.

## Options considered

- A whole MOD channel on the DAC (the plan's first wording): simplest, but real MODs mix drums and melody on one channel.
- Everything to the DAC first: melodies would take the DAC from the drums.
- Louder-wins versus mixing: louder-wins now, mixing as a separate candidate later (above).
- Dropping a DAC clash loser: lossier than moving it; only required-PCM notes are dropped.

## Consequences

- T5.4/T5.5 and the layout step read `GenesisPlan`; T5.6 adds `--ignore-samples` and report section 3.
- `docs/cli.md` documents `--no-dac`; `docs/channel-mapping.md` and `docs/architecture.md` describe the plan.
- Capacity limits (144 DAC notes, 128 KB) are not checked here (T9.2).
