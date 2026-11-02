# ADR 0073: Locked pairs and CH3 special-mode candidates: every onset together, two 2-operator voices judged against the mix (T8.5b)

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

Plan task T8.5b is the second half of T8.5 ([ADR 0072](0072-pre-mix-candidates.md)): the CH3 special-mode candidates of
[multi-pass.md](../multi-pass.md#candidates). In CH3 special mode each of channel 3's four operators takes its own
frequency, so algorithm 4 (OP1→OP2, OP3→OP4) plays two 2-operator voices on one channel. That frees an FM channel for a
**locked pair**: two instruments that always start together at a fixed pitch interval (layered bass and kick, octave
doubling, chord stabs). The mode is allowed for DMF (system `0x42`) and XGM2, and for XGM v1 only after a probe ROM
(plan T11.1), because `$27` also holds the timers ([ADR 0010](0010-multi-pass-optimizer-sgdk-compliance-pure-csharp.md),
[ADR 0011](0011-xgm-v1-spec-saved.md)).

The plan row recorded two of David's choices: a pair is *the same start tick and semitone gap every time*, and the voices
come *from new algorithm-4 fits and from cut-down T8.2 fits, the closer kept*. multi-pass.md spoke of a "co-onset rate",
and nothing said how strict the gap is, whether one sample doubled on two channels counts, how a pair is scored or
where the candidates show up.

## Decision

(David, 2026-11-02, unless marked)

- **Locked means every onset, both sides.** A pair is two **tracks** (a sample on a channel): every note of either starts
  on the same tick as a note of the other. Neither ever plays alone. A track is the notes `SongNote.Find` gives for one
  sample on one channel.
- **Pairs are by channel, so one sample doubled on two channels is a pair** (an octave doubling, a chord stab from one
  sample). The first track is the one on the lower channel.
- **The gap is measured from the playback rates, and may spread by 10 cents** (finetune, not a different note). The gap
  is reported in semitones, with its spread in cents.
- **At least two onsets.** A pair heard once proves nothing.
- **Samples that can't go on FM never pair**: silent ones, `--require-dac` and `--require-psg` (implementation, as in
  ADR 0072). Ignored samples aren't analyzed.
- **Each voice is fitted to its own sample**, the T8.2 way, per pitch region, on either half (OP1→OP2 has the feedback,
  OP3→OP4 has none), from two sources:
  - **New algorithm-4 fit:** every carrier multiple of the exhaustive search (1 and the strongest harmonics) with every
    modulator multiple (1, 2, 3, 4, 5, 7) that shares no factor with it, judged by the quick score, envelopes from the
    region's closest library patch; the two best go to the level fit.
  - **Cut-down T8.2 fit:** each carrier of the region's T8.2 fit without the LFO, with each operator that feeds it
    directly, or alone, moved onto the half; the closest by the search score goes to the level fit (David). OP1's feedback
    stays only when OP1 lands on OP1 again (implementation).
  - The **level fit** is T8.2's local search, limited to the voice's two operators (`FmLocalSearch`, `operators`); the
    feedback moves only with OP1 (implementation).
- **The pair is judged together against the MOD mix** (David): both voices played at once in CH3 special mode against the
  MOD renderer's mix of the two notes, mel bands only (a mix has no one pitch), as a pre-mix is. Every combination is
  tried: either track on OP1→OP2, each voice from either source (8 renders); the closest is kept, and the closest pair of
  new fits and of cut-down fits are reported beside it.
- **Candidates only.** As for T8.2–T8.5a, the converter's output doesn't change; M9's allocator weighs them. The
  **converter logs the locked pairs** at `Debug` (`--verbose`) on every conversion (David), and the harness makes the
  candidates with `--ch3`, writing `locked-pairs.csv` and `ch3-fit.csv`.
- **Offered for DMF and XGM2, never XGM v1** (`Ch3Fitter.Targets`, `Ch3Fitter.Allows`) until T11.1 says otherwise.

How it works (implementation):

- **Regions.** A pair's onsets are grouped by the pitch region each note falls in (or the nearest), one candidate per
  pair of regions with sound. Each voice plays at its region target's pitch, moved by the note's rate, at the group's
  first onset.
- **The window** runs from the onset until the first of the two notes ends, 0.1–1 s; the FM keys are held through it.
- **Balance.** Each voice is fitted at its own level (the scores match levels), so before judging, the voice that is too
  loud against the other, compared with the two notes alone in the MOD render, is turned down by its carrier's TL.
- **No LFO.** The two voices share channel 3's vibrato and tremolo depths, so neither uses the LFO; the cut-down starts
  from T8.2's fit without it.
- **Reuse.** Fits, voices and evaluators are made once per song and shared by every pair; given `--fm-fit`'s fits, the
  harness cuts those down instead of fitting again, and `--fm-max-renders` limits each step as for T8.2.
- **Rendering.** `FmNoteRenderer.RenderCh3` sets `$27` bit 6, loads the patch on channel 3, writes OP1–OP3's frequencies
  to `$A9`–`$AA`/`$A8` (high byte first) and OP4's to the channel's, and keys each half on its own
  ([genesis-audio.md](../genesis-audio.md)).

## Corpus result

Full corpus (19 songs), `--ch3 --fm-max-renders 300 --no-furnace --no-deflemask -j 8`, 2026-11-02:

- **Two locked pairs, both in `jam_-_latenightcoffee`**, and each is two tracks that play only twice (gaps −3.98 and
  +6.98 semitones). No other song has a pair that meets the rule. So on this corpus the rule finds coincidences, not
  layered parts.
- **The CH3 candidates:** 8.27 dB and 10.06 dB log-mel against the MOD mix. In both, the closest combination mixes the
  sources (one new algorithm-4 voice, one cut-down voice). Pairs from one source only: cut-down 8.40 and 10.54 dB, new
  algorithm-4 fits 12.25 and 10.69 dB. 13,075 renders and 237 s for the song, most of it the T8.2 fits the run made for
  the cut-downs.
- **Near misses** (a one-off diagnostic, not kept): counting, per pair of tracks on different channels, the onsets they
  share at their most common gap (±10 cents) against all their onsets:

  | Share of onsets together | Pairs | Songs | With ≥ 8 onsets together |
  |---|---|---|---|
  | 100 % (locked) | 2 | 1 | 0 |
  | ≥ 75 % | 4 | 3 | 1 |
  | ≥ 50 % | 10 | 6 | 4 |

  The clearest: in `hit`, sample 9 doubled on channels 1 and 2 starts together 518 times (81 % of their onsets; 92 % of
  channel 1's). In `k0w-rsnd`, every one of sample 1's 320 notes on channel 1 starts with sample 2 on channel 3, which
  also plays 182 notes alone. The rule as decided misses both. Whether to loosen it (a share, one side always paired, or
  locked per song section) is open for David; M9's allocator works per section, which would suit a per-section lock.

## Options considered

- **Every onset, both sides** (chosen). **Every onset of one side:** a kick that always comes with the bass, plus extra
  bass notes; the lone notes would still need a channel of their own. **A co-onset rate threshold:** catches pairs that
  mostly play together, but then a CH3 candidate can't stand for the whole track.
- **Pair by channel, same sample allowed** (chosen). **Different samples only:** misses octave doublings and chords from
  one sample, which are among the commonest locked pairs.
- **Rate ratio within 10 cents** (chosen). **Rounded to whole semitones:** treats a ±50-cent detune, which sounds as a
  chorus, like a clean doubling.
- **At least twice** (chosen). **Any number:** a pair heard once is trivially locked. **At least 4:** drops short songs'
  real pairs.
- **Judge the pair against the MOD mix** (chosen). **Each voice against its own sample:** cheaper and comparable with
  single-channel fits, but blind to how the two voices sound together. **Both:** more numbers than the allocator needs now.
- **Fit each half against its sample, then judge the mix** (chosen). **Fit jointly against the mix:** no harmonics to
  guide the search, and each step renders both voices.
- **Cut down with a refit** (chosen). **Without a refit:** cheaper and shows what cutting loses, but a voice cut from four
  operators is rarely balanced as two.
- **Try either track on the feedback half** (chosen). **The lower note on OP1→OP2:** half the work, but a fixed rule.
- **Candidates and Debug logs** (chosen). **Harness only:** a user couldn't see the pairs without the harness.

## Consequences

- New: `Analysis/LockedPairs` (`LockedPair`, `LockedOnset`, `LockedPairReport`), `Optimizer/Ch3Fitter` (`Ch3Voice`,
  `Ch3Candidate`, `Ch3SongFit`), `Optimizer/Ch3FitReport`, and `Optimizer/NoteMix` (the MOD mix of some notes, moved out
  of `PreMixer`). `FmNoteRenderer.RenderCh3`; `FmLocalSearch.Refine` takes the operators it may move.
- The conversion logs locked pairs with `--verbose`; its output is unchanged.
- The harness gets `--ch3`, `locked-pairs.csv` and `ch3-fit.csv` ([harness.md](../harness.md#output)).
- M9's allocator weighs a CH3 candidate (its mix score, one FM channel for two tracks) against two single-channel fits,
  and decides how notes end (the release isn't fitted, as in T8.2). The DMF writer needs system `0x42` and its extra
  channels, and the XGM2 writer `$FA`/`$FB` and `$3x` b3, when one is used.
