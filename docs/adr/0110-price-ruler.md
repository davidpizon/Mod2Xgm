# ADR 0110: What a price measures: log-mel with ear penalties, then the file's size on a tie

- **Status:** Accepted (David, 2026-11-14); supersedes in part [ADR 0098](0098-learned-allocator-costs.md);
  [ADR 0111](0111-neither-and-listening-models.md) leaves `neither` answers out of the tie line
- **Date:** 2026-11-14

## Context

[ADR 0098](0098-learned-allocator-costs.md) planned learned prices for the allocator's choices (plan T9.4n–T9.4q): a model
predicts **Δs**, how much worse the whole mix scores when one track plays with one option instead of its first choice.
Δs was to be in the units of [ADR 0097](0097-learned-listening-score.md)'s learned listening score, and the prices were to
wait for that score to pass its gate, so that they wouldn't learn log-mel's mistakes.

The learned score didn't pass: 43 % on the hold-out against log-mel's 67 %, so log-mel stays the main number
([ADR 0107](0107-learned-score-not-adopted.md)). ADR 0107 kept T9.4n–T9.4q "as planned", but their ruler was gone. Before
T9.4n starts, David asked for this ADR (2026-11-14, plan T9.4n0): what does a price measure?

What the rounds taught (ADR 0107, [ADR 0106](0106-cant-tell-smaller-wins.md), [ADR 0109](0109-pan-swap-cost.md)):
missing instruments always lose; an instrument jumping between the centre and one speaker is "very unsettling"; harsh
clipping distracts from everything else; a sound switching between recording, FM and PSG mid-song is jarring
([ADR 0092](0092-stable-voices.md)). Log-mel sees little of these. And when David can't tell two versions apart, the
smaller file wins (ADR 0106).

## Decision

David, 2026-11-14:

- **A price has two parts: how much worse the song sounds, and the size of the song.** They are not added with an
  exchange rate. **Sound comes first; size only breaks ties.**
- **How much worse it sounds: log-mel plus ear penalties.** Δs is the change in the whole song's log-mel distance to the
  MOD render, plus a penalty for each kind of mistake log-mel can't see. The first four:
  - **a missing instrument:** a sound of the MOD that can no longer be heard;
  - **a side jump:** an instrument moving between the centre and a speaker, or between speakers, where its MOD channel
    doesn't ([ADR 0108](0108-pan-swap-count.md)'s count);
  - **harsh clipping:** the mix clipped where the MOD's isn't;
  - **a voice switch:** a sound changing between recording, FM and PSG mid-song (ADR 0092).

  Each penalty's size is set by blind rounds, one penalty at a time, as the rules in ADRs 0087–0094 and 0109 were. Until
  the priced optimizer has passed ADR 0098's gate, the hard rules for side jumps (ADR 0109: none) and voice switches
  (ADR 0092) stay in force; a penalty only prices them.
- **Size: the whole output file's bytes**, the `.xgm` or `.xgm2` as written, as ADR 0106 counts "smaller", not only the
  PCM bytes.
- **The tie line.** Two choices whose Δs differ by less than a "can't hear it" line count as equal, and then the smaller
  file wins. The line is measured on David's answers: the log-mel-plus-penalty gaps of the pairs he answered "can't tell"
  against those of his a/b picks, choosing the line that separates them best. A blind round then checks it.
- **A song that already fits.** It stays as it is ([multi-pass.md](../multi-pass.md#idea)), except for shrinks whose Δs is
  below the tie line: those can't be heard, so the smaller file wins. Anything audible is still only for songs that don't
  fit, and there the least Δs that fits wins.
- **XGM and XGM2 only.** DMF has no size limit and is for editing in DefleMask, so a DMF keeps every recording as it is
  and size never breaks a tie there.
- **The hold-out.** The 19-song corpus has been heard in many rounds and the 28 songs of rounds 22–24 too. New hold-out
  songs are picked in T9.4p (David).
- **Unchanged from ADR 0098:** a model predicts the prices and the solver alone checks the limits; the training data
  comes from the harness changing one choice at a time; the prices replace the rules only after winning on at least
  5 hold-out songs over 2 blind rounds, behind a rule switch until then.

## Options considered

- **Log-mel plus ear penalties, size on a tie (chosen).** Log-mel is the best ruler on the hold-out, and the penalties add
  what the rounds showed it misses. Each penalty is small to tune on its own.
- **Log-mel alone, with the blind-round gate catching its mistakes.** Simpler, but the prices would learn log-mel's
  mistakes and the gate would only find them late, after the whole model is built.
- **Fit the prices straight from David's either/or answers.** No ruler in between, but it needs hundreds of pairs.
- **Sound and size added with an exchange rate** (bytes per dB, set by ear or by a CLI option). It would shrink songs
  audibly to save bytes even when they fit, which David doesn't want; a CLI option would also change the CLI contract.
- **Drop the learned prices** and keep tuning hand rules by ear, as with T9.4m. Possible, but the rules can't see the mix.

## Consequences

- ADR 0098's Status line says it is superseded in part by this ADR: the units of Δs, the gate on ADR 0097's score, and the
  hold-out.
- Plan: T9.4n0 (this ADR) done. T9.4o's training data records log-mel, each penalty and the file size per choice, and
  measures the tie line on the answers file. Each penalty needs its own detector in `src/Scoring` and a blind round to set
  its size, before T9.4p fits on them. T9.4p picks new hold-out songs.
- [multi-pass.md](../multi-pass.md) says a song that fits may still take shrinks below the tie line, on XGM and XGM2,
  once the prices are adopted. Until then nothing changes: this ADR changes no plan.
- T9.5's acceptance ("improves corpus score vs greedy") is judged with log-mel plus the penalties, the file size breaking
  ties.
