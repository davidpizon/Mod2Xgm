# ADR 0100: A fixed FM-confidence threshold

- **Status:** Proposed; supersedes the threshold of [ADR 0096](0096-fm-confidence.md)
- **Date:** 2026-11-10

## Context

[ADR 0096](0096-fm-confidence.md) gives each sample a confidence `p`: the chance that David picks its FM version, or
can't tell it from the recording, in the whole song. It planned two ways to choose the threshold θ above which a sample
plays on FM:

- an option, `--fm-confidence <0–100>`, so whoever runs the converter sets θ (T9.4f);
- a default θ chosen with David after the listening rounds (T9.4i).

David (2026-11-10) removed both requests: neither the person running the converter nor David picks the threshold.

## Decision

- **No `--fm-confidence` option (David).** The CLI contract gains nothing for the threshold. `--max-pcm-bytes` stays the
  hard wall, and `--require-fm`, `--require-dac` and `--require-psg` stay hard constraints, as in ADR 0096.
- **No threshold chosen by ear (David).** θ is a constant in the converter, set by the calibration rather than by a pick.
- **The value (proposed).** θ = 50 %: a sample plays on FM when David is at least as likely to pick FM, or not tell the
  two apart, as to pick the recording. This is what `p` means, so it needs no tuning of its own; the listening rounds of
  T9.4i tune the model behind `p` instead.
- **Until T9.4i.** θ stays 100 %, so every plan is as it was, until the fitted model passes the hold-out round. Then θ
  becomes 50 %.
- **The ranked table stays.** It still prints whenever the optimizer runs (T9.4f), so the reason a sample went to FM is
  visible.

## Options considered

- **A constant set by the calibration (chosen).** One less option to explain and test, and one less question for David.
  The table shows the result.
- **`--fm-confidence` on the command line (ADR 0096).** Lets a user trade sound for space by hand, but `--max-pcm-bytes`
  already does that where it matters, and a free θ invites settings the rounds never checked.
- **A default chosen with David after the rounds (ADR 0096).** Removed by David.

## Consequences

- T9.4f drops `--fm-confidence`; it keeps the ranked table. [implementation-plan.md](../implementation-plan.md) and
  [multi-pass.md](../multi-pass.md) say so.
- T9.4i fits the model and checks it on the hold-out round; it no longer chooses a default with David.
- ADR 0096's model, features, "least needed" order and calibration rounds are unchanged.
