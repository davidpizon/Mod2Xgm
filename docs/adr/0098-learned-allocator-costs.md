# ADR 0098: Learned costs for the allocator's choices

- **Status:** Proposed
- **Date:** 2026-11-09

## Context

The goal for XGM and XGM2 is the closest-sounding song within the size budget. When the recordings don't fit, the optimizer
chooses how to give some of them up: FM, a PSG tone or noise, a shrunk recording, a pre-mix, or a cut. David's stated aim
(2026-11-06, [multi-pass.md](../multi-pass.md)) is to try many ways of cutting the song down and keep **the combination of
changes that is least perceptible**.

Each of those choices is priced today by a fixed rule, the same in every song:

| Choice | Today's rule | Where |
|---|---|---|
| Which notes give way first | Importance: length up to 0.25 s × volume, up to 9× for a sample that hits, 1.5× on a downbeat | [ADR 0087](0087-greedy-allocator.md) |
| Which shrink move, on which sample | Least important sample first; merge, then share pitch, then share loudness, in that order | [ADR 0091](0091-shrinking-recordings.md) |
| Look-alike merge | Correlation ≥ 0.98, same loop kind, lengths within 10 % | ADR 0091 |
| Pitch sharing | Within 10 cents; not tuned by ear (it fired once on the corpus) | ADR 0091 |
| XGM2 half speed | Off for every sample: crunchy in round 11, though its cost ranges from about +3 dB on average to +10 dB on bright samples | ADR 0091, [ADR 0071](0071-pcm-candidates.md) |
| Pre-mixed voice steals | At most 4 notes, turned down at most 6 dB | [ADR 0094](0094-pre-mixed-voice-steals.md) |
| A recording moved to FM | Lowest `(1 − p) · importance / cost` | [ADR 0096](0096-fm-confidence.md) |

The ears don't hear a choice on its own; they hear it in the mix. A pad under busy drums can lose much more than a lead
that plays alone, and half speed costs a bass line little and a hi-hat a lot. The rules can't see that, and each rule took
listening rounds to set.

Rendering and scoring the whole song for every option would see it. But FM fits alone take 42–188 s per song
(ADR 0087), and there are far too many combinations of options to render each one.

## Decision

Proposed by Claude, for David to accept, change or reject. The items marked **open** are David's to settle.

- **A model sets the prices; a solver makes the plan.** The model predicts what each choice costs the ears. The
  allocator, `RecordingShrinker` and `PreMixer` still build the plan, and they alone check it against the target's
  limits ([ADR 0085](0085-target-limits.md)), the `--require-*` lists and the stable-voice rule
  ([ADR 0092](0092-stable-voices.md)). A model never decides whether a plan fits.
- **What it predicts.** A choice is one track (a sample on a channel), over one section or the whole song, played with one
  option instead of its first choice. The model predicts how much worse the whole mix scores with that choice:
  **Δs**, in the units of [ADR 0097](0097-learned-listening-score.md)'s listening score. The options covered:
  - FM, a PSG tone or PSG noise;
  - each recording saving: merge with another sample, share pitch by *k* cents, share loudness, half speed, trim, a
    shorter loop;
  - a pre-mix, and a cut.
- **Features.**
  - **The sample:** `SampleAnalysis`'s traits (tonal, noisy, loop, pitch deviation, chord), its share of energy above
    3.3 kHz (half speed's top frequency), and its share of energy in the attack.
  - **The option on its own:** its distances against the sample alone (log-mel, ADR 0096's attack, envelope and
    brightness distances, and T9.4h's noise-to-mask ratio).
  - **The context:** how exposed the track is (its share of the song's importance, and how long it plays with little
    else sounding); how much of its energy the other tracks cover in the same bands at the same time, measured on
    per-track stems from the MOD renderer as ADR 0053's stem measure does; its pan side; the format.
- **Training data is made by the harness, not by ear.** Starting from a song's plan, change one choice at a time, render
  the whole song and record Δs with ADR 0097's score. Some pairs of choices are changed together, to measure how far
  their costs add up. This runs offline with the existing caches. Blind rounds check the result: does David prefer
  the choice the model priced cheaper?
- **The model.** Gradient-boosted trees or a small neural network, trained in `tools/Mod2Xgm.Harness`. Its weights are
  constants in `src/Optimizer`, run in pure C#, deterministic and AOT-safe. Where the method allows, the model is held to
  rise with exposure, so a more exposed track is never priced cheaper.
- **Where the prices go.**
  - **Importance** becomes the price of losing a note's first choice.
  - **`RecordingShrinker`** takes, on any sample, the move with the lowest Δs per byte or slot saved, rather than a fixed
    order on the least important sample.
  - **The merge, pitch-sharing and pre-mix limits** become prices rather than thresholds.
  - **Half speed** becomes available per sample when its price is low, for example a dull bass. This would reverse
    ADR 0091's "off" only for samples the model prices low, and only after a blind round. **Open:** whether David wants
    half speed back on that condition.
  - **ADR 0096**'s "least needed" rule takes the price in place of `(1 − p) · importance`.
  - **T9.5**'s refine loop searches whole plans by their summed prices, then renders and scores only the best few in
    full.
- **Songs to train on.** Today's 19-song corpus ([ADR 0046](0046-mod-corpus.md)) becomes the hold-out and is never trained
  on. Training songs are more Public Domain or CC0 MODs, chosen and licensed as ADR 0046 did. They are used only by the
  harness, so the converter stays local-only. **Open:** how many; Claude suggests 100 or more, across the trackers and
  styles the corpus has.
- **Order and gate.** This waits for ADR 0097's score to pass its own gate. Trained on log-mel, the prices would learn
  log-mel's mistakes, such as the hawks_claw version that measured better and lost 3 and 2 to 5 (ADR 0087). Before that, the
  tooling may be tried with log-mel as a dry run that changes no plan. The prices replace the rules only after the priced
  optimizer beats the rule-based one on hold-out songs in blind rounds, behind a rule switch until then, as
  `GenesisPlan.TunedByEar` was. **Open:** how many songs and rounds that takes.

## Options considered

- **Learned prices with the existing solver (proposed).** It sees masking and exposure, which the rules can't. It keeps
  the hard limits in code that can be tested, and the plan stays deterministic. Every change still gets its conversion
  note, which can now also say what it was predicted to cost.
- **Keep the hand rules, tuned by ear one at a time.** This works and is how ADRs 0087–0094 were made. But each rule is
  one setting for every song, it can't see the mix, and each one costs listening rounds.
- **Render and score every option in the song.** Exact, but far too slow for many combinations. It stays the final
  check on the best few plans.
- **A learned planner, or reinforcement learning, that makes the plan itself.** It needs far more answers or a ruler
  already trusted, it can't guarantee the limits, and it would make plans harder to explain and to repeat with `--seed`.
- **A hand-written masking model alone.** It is one of the features. Alone it doesn't know how David weighs a masked
  change against an exposed one, or an attack against a tail.

## Consequences

- Proposed plan tasks, after ADR 0097's T9.4m and before T9.5:
  - **T9.4n:** per-track stems, and the masking and exposure features from them;
  - **T9.4o:** harness training data: one choice changed at a time, plus pairs to check how well costs add up;
  - **T9.4p:** training songs and their licences; model fit with leave-one-song-out checks; Δs predicted on the hold-out
    against the measured Δs;
  - **T9.4q:** importance and `RecordingShrinker` driven by the prices behind a rule switch, checked in blind rounds;
    half speed per sample if David agrees.
- T9.5 uses the prices for its search; its acceptance ("improves corpus score vs greedy") is judged with ADR 0097's score.
- [sample-capacity.md](../sample-capacity.md) (consolidation and importance) and [multi-pass.md](../multi-pass.md)
  (allocation) change once the prices are adopted. [harness.md](../harness.md) gains the training-data run.
- Out of scope, each for its own ADR: a new candidate kind that plays a short recorded attack followed by an FM or PSG
  sustain; and a learned move order for speed.
