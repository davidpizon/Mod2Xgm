# ADR 0075: FM pitch guard: every fitted voice repeats at the note, no ½ multiple and no shared factor

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

The T8.5b A/B clips ([ADR 0073](0073-ch3-locked-pairs.md)) of k0w-rsnd played the wrong notes. Once the short samples got
a pitch (PR #78), the four-operator fits were right, but the CH3 voices were not: one was fitted as OP3 ×1 → OP4 ×½,
which sounds an octave below the note. The cause is general. An FM voice is made of its operators' frequencies,
multiple × note, and repeats at their greatest common divisor. A ½ multiple on any sounding operator, or a factor all
of them share (2 and 2, 3 and 6), moves the note however the levels are set.

[ADR 0069](0069-fm-patch-fitting.md)'s exhaustive search already prunes shared factors, because they "only move the
pitch", but three other places could still make them:

- the genetic search's mutations (any multiple ½–15);
- the local search's multiple moves;
- the CH3 cut-downs, where two operators cut from four can share a factor the four didn't (algorithm 4's `1 1 2 2`
  leaves `2 2`).

The scores don't catch it. The harmonic score only reads multiples of the note, which a voice an octave up also has, and
misses a sub-octave between them. The mel score is only part of the picture.

## Decision

(David asked for the guard, 2026-11-02; the rule is the implementation's.)

- **A fitted voice keeps the note's pitch** (`FmPitch.Keeps`). Its sounding operators share no factor and none is ½.
  An operator sounds when its TL is below 127 and it reaches the output through operators that do too: a modulator
  behind a silent carrier (TL 127) adds nothing, so it doesn't count and normalizing leaves it alone. In half steps (½ = 1, 1 = 2, …), their greatest common divisor is exactly 2.
- **The genetic search** divides a child's multiples by what they share (`FmPitch.Normalized`): 2 2 2 2 becomes
  1 1 1 1, and ½ 1 becomes 1 2. That keeps the tone and puts it on the note. A child that would need a multiple above 15
  is replaced by its first parent. Random structures are normalized too.
- **The local search** never moves from a patch that keeps the pitch to one that doesn't. A start that already breaks
  the rule can still move, toward the rule.
- **The CH3 cut-downs** are normalized the same way, so a carrier left alone always plays at ×1.
- Library patches and the exhaustive search already keep the rule; the guard doesn't change them.

## Options considered

- **Normalize where structures are made, and block moves away from the note** (chosen). It is cheap, needs no renders,
  and keeps the tone the search found.
- **Reject at the evaluator** (an infinite score): one choke point. But the final judged fits would need a special
  value, and a start that broke the rule would have no way out.
- **Score the pitch instead** (a periodicity or sub-harmonic term): catches subtler cases, such as a voice with a weak
  fundamental heard an octave up. But it changes every score and its weights need a corpus run. It is left for the
  scoring work if listening shows it's needed.

## Consequences

- New: `Optimizer/FmPitch`. Changed: `FmGeneticSearch`, `FmLocalSearch.Refine` and `Ch3Fitter.CutDowns`.
- T8.2 fits, pre-mixes and CH3 candidates can change where they used ½ or a shared factor. Fits that sounded off-note
  now play at the note. Their scores may be a little worse where the shifted voice matched the spectrum better.
- Not covered: a voice that keeps the rule but whose fundamental is weak enough to be heard an octave up (2-operator
  ×3 → ×2). The harmonic score weighs that.
