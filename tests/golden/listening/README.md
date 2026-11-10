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
music. On hit, David heard the half-speed bass as "crunchy", as in round 11. Even on dull samples, half speed is easy to
hear.

## A clip changes

When a change to the converter makes `--remake` report a row whose clips differ, the row no longer describes what the
code makes. Leave it in the file, since David's answer is still true of the clips he heard, and note the change in the PR.
The fit (T9.4k) takes only rows that still remake, so a row's weight comes back if its clips are restored. **Verify**
in T9.4k whether stale rows should be dropped from the file instead.
