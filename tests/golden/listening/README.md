# Listening answers

`answers.tsv` holds every blind A/B question David has answered whose clips today's code can make again byte for byte
([ADR 0097](../../../docs/adr/0097-learned-listening-score.md), plan T9.4j). The learned listening score (T9.4k) is
fitted on it, and every new round adds its rows here. The clips themselves aren't committed: each row's recipe makes them
again, and `mod2xgm-harness --remake tests/golden/listening/answers.tsv` checks that it still does
([harness.md](../../../docs/harness.md#listening-answers)).

## Columns

Tab-separated, one question per line, after a header line with these names. Numbers are written with a `.` and no
thousands separator.

| Column | What it holds |
|---|---|
| `date` | The day of the round, `yyyy-MM-dd` |
| `round` | The round, `r20` and on (rounds 1–19 are in their ADRs, see below) |
| `question` | The question within the round, `q1`, `q2`, … |
| `song` | The MOD's file name, in `tests/data/corpus` unless the harness is given other inputs |
| `start_s` | Where the clip starts in the song, in seconds |
| `seconds` | How long the clip is |
| `format` | The `--format` both versions are made for |
| `recipe_a`, `recipe_b` | How each version is made: converter options, plus harness rules such as `rule:half-speed=3` ([harness.md](../../../docs/harness.md#listening-answers)). A and B are the versions as the survey showed them, X and Y |
| `commit` | The commit that made the clips |
| `sha256_a`, `sha256_b` | The SHA-256 of each clip file David heard, as `--remake` cuts it |
| `pick` | `a`, `b`, or `same` for "can't tell"; empty for a question not answered yet |
| `grade_a`, `grade_b` | A 1–5 grade of each version when the round asked for one; empty otherwise |
| `words` | What David said about it, on one line |

Every question is heard against the original MOD (libopenmpt), played first.

## Rounds 1–19

None of the earlier rounds is in the file (David, 2026-11-10: only clips today's code remakes exactly; 1–5 grades
aren't turned into pairs). Rounds 1–7 of the layering survey and the first listening survey were graded 1–5. The
either/or rounds (layering 8–9, voice switching 10–15, mixed voices 16–19) were made by code that has changed since:
the version David picked in round 19 (8-bit Castle, 0:57–1:17, `--mapping optimizer`, XGM2), made again at this
branch's start, matches only 10.7 % of the clip's samples, and the older rounds came before more changes still. Their
answers stay in ADRs [0088](../../../docs/adr/0088-layering.md), [0089](../../../docs/adr/0089-fm-layer.md),
[0091](../../../docs/adr/0091-shrinking-recordings.md), [0092](../../../docs/adr/0092-stable-voices.md) and
[0094](../../../docs/adr/0094-pre-mixed-voice-steals.md) as history.

## Round 20: half speed on dull samples

The first round logged here (David, 2026-11-10). Each question is one sample with little sound above 3.3 kHz, the
highest XGM2 half speed (6.65 kHz) keeps, in one song: the converter's plan, against the same plan with that sample
stored at half speed. It asks whether ADR 0098's per-sample half speed is cheap to the ear on such samples.

**Result:** full speed won all five, with no "can't tell", though the two versions differ by only 21–34 dB below the
music. On hit, David heard the half-speed bass as "crunchy", as in round 11. Afterwards he added that in Cancan, with a lot of
melody over the bass, the melody hid the crunch very well (it was also the closest pair, 34 dB below the music): half speed may still be a useful way to save space where the
rest of the mix covers it. So the round says half speed is audible on an exposed bass, not that it is always too costly;
how much the other tracks cover a sample is ADR 0098's masking feature (T9.4n).

## Round 21: pairs the picker chose

The first round the pair picker chose (`mod2xgm-harness --pick`, plan T9.4k3,
[ADR 0102](../../../docs/adr/0102-pair-picker.md)), David, 2026-11-11. Out of 998 candidates, ten pairs where the seven
measures' votes were most split, one per song, none from round 20: each the default `--mapping optimizer` against one
change (half the PCM memory, half speed on a dull sample, `--no-dac`, or `--mapping simple`).

**Result:** the five half-speed and memory-budget pairs on big songs (hawks_claw, neon, 8-bit Castle, Rubbish,
Stardust Jam) were "can't tell": the changes the measures argued about were too small to hear. The other five were clear:

- 10kdub at half its memory: the default won; a drum hit turned to static.
- `--no-dac` lost both times. AI Renaissance kept its FM but lost the drums; on Can't Please Everyone David heard no voice at all,
  only the bass: the voice samples (13–16) went to FM patches, which can't sound like a voice.
- `--mapping simple` lost both times (rsnd, Cute October): "not even close to the same voice".

**The first fit** (`--fit`, 2026-11-11) on all 15 answers, every clip remade byte for byte: one weight allowed, and forward
selection kept the envelope modulation distance (`modulation_db`, weight 6.24, λ = 0.01). Left out one song at a time, it
guessed all 10 of David's a/b picks (log-mel alone: 6 of 10), with a log-loss of 0.396 against a coin's 0.693, and it
gave the five "can't tell" pairs 0.50–0.55. Fifteen answers, one per song, are still few: the adoption decision waits
for its hold-out round (T9.4l).

## Round 22: the first hold-out round

The first round on the hold-out songs ([ADR 0105](../../../docs/adr/0105-hold-out-songs.md), plan T9.4l), David,
2026-11-12. The picker chose ten pairs, one per song, from `tests/data/holdout/`: four at half the PCM memory, three
with a sample at half speed, two `--no-dac` and one `--mapping simple`. Every clip was pitch-checked against the original
before the survey (no semitone shift; tuning within 19 cents).

**Result:** six "can't tell": all four memory-budget pairs and two of the three half-speed pairs (Shine, Spider). The
other four were clear:

- `--no-dac` lost all three: on The Messageman it kept only the bass (the default lost the long chords); on Silicon
  Dancer the default kept the percussion and the pulsing beat; on May Is 4 Her the default won though its whoosh at 0:02
  came out as "a second of static" that would have been better dropped.
- Pleasant with a sample at half speed won.
- `--mapping simple` lost on Air Ride: its voices were close, but the percussion and bass were mostly missing.

**Scored with the fitted weights, no refit:** the learned score guessed 1 of the 5 a/b picks, log-mel 4 of 5. Ten
pairs are a third of the bar's 30, so this is no verdict yet, but it is the first sign the one-feature fit of round 21
doesn't carry over to songs it hasn't heard.

## Hold-out rows

Rows whose song is in `tests/data/holdout/` are the hold-out ([ADR 0105](../../../docs/adr/0105-hold-out-songs.md)):
logged here like any round, but never fitted on. `--fit` and `--embeddings` leave them out, and `--holdout` scores the
learned score on them against David's bar ([harness.md](../../../docs/harness.md#the-hold-out)).

## A clip changes

When a change to the converter makes `--remake` report a row whose clips differ, the row no longer describes what the
code makes. Leave it in the file, since David's answer is still true of the clips he heard, and note the change in the PR.
The fit (`--fit`, [harness.md](../../../docs/harness.md#the-fit)) takes only rows that still remake, lists the others
in `fit.txt`, and takes a row again if its clips are restored. Stale rows stay in the file (David, 2026-11-10,
[ADR 0101](../../../docs/adr/0101-listening-score-fit.md)).
