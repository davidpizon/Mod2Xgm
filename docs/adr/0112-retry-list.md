# ADR 0112: The retry list: kinds of fix, CLAP's screen, and the round it asks

- **Status:** Accepted (David, 2026-11-14)
- **Date:** 2026-11-14

## Context

[ADR 0111](0111-neither-and-listening-models.md) sends every `neither` answer back for a retry: the harness builds
different kinds of fixes for that place, not small changes of the rejected versions, and puts the two most promising to
the next round until one passes. Plan T9.4s2 builds that list. Seven rows were `neither` after T9.4s1, at six places
(tgm-lazycows at 1:40 twice):

| Place | What was wrong in both versions |
|---|---|
| musix-burp at 3:20 | The main melody clips harshly |
| tgm-lazycows at 1:40 | The percussion shifts between the centre and the left; then `--mapping simple`'s FM melody |
| jam_-_highway_encounter at 1:00 | The voices cut over to the right when the melody stops |
| k0w-rsnd at 1:10, musix-the-messanger at 4:20, hit at 2:50 | `--mapping simple`'s FM melody: "completely unacceptable" |

Since those rounds, the converter's default has changed: no pan swaps stand (round 25, [ADR 0109](0109-pan-swap-cost.md)).
A converted musix-burp shows where its clipping comes from: several recordings go past full scale on their own when
they are made into 8-bit copies (sample 8 peaks at 1.30, 493 frames clipped), not the drivers' mix.

## Decision

David, 2026-11-14.

- **Start from today's default** (`--mapping optimizer`), not from the rejected recipes. If today's default makes a clip
  David rejected there (the same bytes), the place is still as bad and the round asks two retries against each other. If
  it doesn't, the round asks today's default against the best retry, so David can say whether the change since already
  fixed it.
- **Three kinds of fix**, each tried at every place (the list doesn't guess which problem a place has):
  1. **Keep a recording:** each of the three sounds that play most there (seconds × MOD volume × the sample's RMS level)
     with `--require-dac`. If that finds no room (exit 6), the sound that plays least there is left out
     (`--ignore-samples`).
  2. **Pre-mixes and layers off:** `rule:pre-mix=off`, `rule:layers=off`, and both.
  3. **Turn down to stop clipping**, both ways (David): the loudest sound there 3 and 6 dB quieter as a recording
     (`rule:quieter=<sample>:<dB>`), and the whole song 3 and 6 dB quieter (`rule:song-quieter=<dB>`: FM, PSG, every
     recording and pre-mix).
- **Not tried: another FM patch** (the second or third closest fit). David left it out: round 25 already says FM
  shouldn't stand in for a melody.
- **CLAP's screen.** Each clip is measured against the original's with CLAP (the cosine distance of
  [ADR 0104](0104-pretrained-embeddings.md)). A try as far from the MOD as the closer of the clips David rejected there,
  or further, is screened out; a try whose clip is today's default's, byte for byte, changes nothing and is dropped. The
  closest left go to the round. **When none passes, the closest go anyway** (David), marked doubted in `retries.tsv`, not
  in the survey.
- **The round.** One question per place, the versions' order by a hash of the place, answered `a`, `b`, `same` or
  `neither`. A place leaves the list once its last answer isn't `neither`; a `neither` keeps it on.
- **The level rules are harness-only.** `LevelRules` reaches the optimizer through `ConversionOptions`, as the pan and
  pre-mix rules do; no command-line option sets it, and a DMF ignores it. With no rule the plan's levels are today's,
  so every byte-exact test stays exact.

## Options considered

- **Today's default, checked in the round where it changed (chosen).** Retries fix what the converter does now. A place
  the default already fixed still gets one question, so it isn't dropped on a measurement alone.
- **The rejected recipes as they were.** Faithful to what David heard, but fixes code that has since changed: the simple
  mapper's FM melody isn't what the optimizer plays.
- **Drop a place when a measurement says it's fixed.** Cheaper, but the measures agree only weakly with David's ears
  ([ADR 0049](0049-simple-mapper-baseline.md)).
- **Pick the fix kind from the words** ("clips", "jumps", "FM"). It reaches only the songs whose words name the
  problem; trying every kind everywhere and letting CLAP screen is general, as ADR 0111 asks.
- **Skip a place where CLAP screens out every try.** Says no fix exists without asking David; CLAP was weak at small
  differences (ADR 0104).
- **Each retry against the rejected version.** Two questions per place, and the rejected version is already known to be
  unacceptable.

## Consequences

- `mod2xgm-harness --retry answers.tsv` writes `listening/retries.tsv` (every try, its CLAP distance, whether it changed
  anything, passed and went to the round) and `listening/retry-round.tsv` (the next round's rows), then the round goes on
  as any other: `--remake`, the survey, the answers file ([harness.md](../harness.md#retries)). It needs CLAP's ONNX file
  in the model folder.
- Recipes gain `rule:quieter` and `rule:song-quieter`. A pre-mix keeps the levels its notes were mixed at under
  `rule:quieter`; `rule:song-quieter` turns it down with everything else.
- That recordings clip on their own when made into 8-bit copies is a converter fault the retries may confirm by ear;
  fixing it by default is a separate change, once a round shows the quieter versions pass.
- The retries' answers are labels for the "acceptable?" check (T9.4s4).
- **Round 26** (2026-11-15) asked the six places: Burp's retry (`--require-dac 27`) won, Lazy Cows' default won (the
  retry dropped melody notes), the other four were "can't tell". None stayed `neither`
  ([round 26](../../tests/golden/listening/README.md#round-26-second-tries)).
