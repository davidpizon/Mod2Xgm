# ADR 0113: Squeeze rounds: which ways of fitting a smaller budget keep the most sound

- **Status:** Accepted (David, 2026-11-15)
- **Date:** 2026-11-15

## Context

After round 26 every place David had rejected passed, and every melody was "exceedingly close". But a song that fits
keeps every recording: a recording's distance is 0 dB whatever the ruler reads ([ADR 0087](0087-greedy-allocator.md)),
and size only breaks ties below the "can't hear it" line ([ADR 0110](0110-price-ruler.md)). Without `--max-pcm-bytes`
only the format's 16 MB binds, so no round has ever heard how a song sounds when it must give up recordings. David
(2026-11-15): "I'm worried we are being wasteful on size", while keeping accuracy first, and the tool should be able to
make accurate, or at least acceptable, FM instruments when pushed to.

The converter has several ways to fit a budget, and today it always uses them in one order: shrink the recordings
(merge look-alikes, share pitches, share loudness, half speed; [ADR 0091](0091-shrinking-recordings.md)), then give the
least important notes FM or the PSG. Different songs may do better with different ways, or in a different order.

Rounds 11 and 12 already squeezed to half of today's bytes, one way against another: half speed lost to moving notes
to FM/PSG in every song ("very crunchy and compressed"), and the gentle shrinks tied with moving notes (ADR 0091). The
squeeze rounds go further: 25 % and 0 %, who keeps a recording, a harder FM search, and an order per kind of song, on
songs that store far more today (musix-burp 2.4 MB, musix-the-messanger 1.8 MB).

## Decision

David, 2026-11-15.

- **Squeeze rounds.** Each pair is one song at one budget, squeezed **two different ways**, not a budget against a
  bigger one (a bigger file would always win). They teach which ways keep the most sound under which circumstances.
- **Budgets:** a share of each song's own XGM2 PCM bytes today: **50 %, 25 % and 0 %** (0 % is `--no-dac`: everything
  on FM and the PSG, drums included). The harness turns the share into `--max-pcm-bytes` per song and writes the bytes
  in each recipe, so a row remakes without knowing today's size.
- **Songs:** the six listening songs (corpus and hold-out) with the most PCM bytes today, as XGM2 (ADR 0110: XGM and
  XGM2 only).
- **The ways tried against each other:**
  1. **Shrink vs swap:** today's shrinking first, vs no shrinking (keep some recordings whole, the rest on FM/PSG).
  2. **Half speed for all:** every recording at half speed, so nearly every sound stays a recording, vs today's way.
  3. **Who keeps a recording:** today's importance order, vs drums first, vs melody first.
  4. **FM: today's patch vs a harder search with CLAP's pick:** several fits from different starting points (seeds,
     structures), the closest by CLAP chosen among them (plan T9.4s6's idea), checked first in the harness as ADR 0111
     asks. One such pair per song at 25 %, and at 0 %, where FM is all there is.
- **More ways, from round 28 (David, 2026-11-15):** each joins the rounds as it is built, so round 27 isn't remade:
  5. **Dials the converter has (plan T9.4t5):** pitch sharing at 25 and 50 cents (today 10), one copy per 12 dB of
     loudness (today 6 dB: shared copies already span two 3 dB steps), merging look-alikes at 90 % alike (today 98 %),
     and half speed only for dull samples (little above 3.3 kHz). Round 28 tries them at 25 %.
  6. **A recorded attack with an FM tail:** a long sound's first part stays a recording, and an FM note of its fitted
     patch carries the rest.
  7. **A made loop, and a cut tail:** a long one-shot with a steady part gets a seamless loop there, so only that much
     is stored; or its tail is cut 30 dB below the peak (today 48 dB, but that only counts bytes: the whole sample is
     still stored, so cutting it is new work, done with the loops).
  8. **Noise for hi-hats and cymbals:** bright noisy hits give up their recordings to the PSG's noise first.
  9. **Hidden notes skipped:** a note masked by a louder one at the same moment stores no copy of its own.
- **Learning the order.** Each song is described by measured traits: the share of its importance in percussive
  sounds, its FM confidence ([ADR 0096](0096-fm-confidence.md)), how many of its samples look alike, how much of its
  sound is above 6.65 kHz (what half speed loses), and the budget. Every answer is logged with them, and a small rule
  (which way first, for which traits) is fitted from the answers, checked leave-one-song-out.
- **When to stop (David): when it's 95 % sure.** Rounds keep coming until, for each kind of song the rule separates, the
  way it puts first beats each other way with 95 % confidence on David's picks (a one-sided binomial test, "can't tell"
  counted for the smaller file as in [ADR 0106](0106-cant-tell-smaller-wins.md)), or the answers there are mostly
  "can't tell", which means the order doesn't matter there. Each round reports how far from that each kind is. Later
  rounds ask only the pairs still unsure, and songs that weren't in the earlier rounds join, so the rule is checked on
  songs it wasn't fitted on.
- **A price for size comes from the answers, not a guess (David).** Once the squeeze rounds say how much each way loses,
  that sets how much a recording is worth against its bytes, which replaces "size only breaks ties" in a later ADR, behind
  a rule switch until blind rounds pass it. Until then ADR 0110 stands for the converter's default.

## Options considered

- **Same budget, two ways (chosen).** Each answer says which way works better with the bytes there are, which is what
  the converter has to choose.
- **Each budget against the one above.** David: the bigger file would always be heard as better, so it says nothing
  about how to squeeze.
- **Fixed budgets (256K, 128K, 64K).** Small songs wouldn't be squeezed at all, and big ones would lose everything.
- **Pick a price per KB now.** Quicker, but a guess; the rounds measure it instead.
- **Only a lower "can't hear it" line.** Changes little: a song that fits would still keep every recording.
- **A fixed number of rounds.** David asked for rounds until the rule is 95 % sure.

## Consequences

- The harness gains a budget-share rule and a rule per way of squeezing, each passed to the optimizer through
  `ConversionOptions` as the pan, pre-mix and level rules are; none is a command-line option of the converter yet.
- The traits and the fitted rule live in the harness until the rounds have settled them; then the converter uses the
  rule to choose its order (a later plan task), and the price for size follows.
- The FM pair is the first use of CLAP in patch choice (T9.4s6); the converter gets it only after T9.4s3.
- The rounds' answers go in the answers file like any other, as labels for the "acceptable?" check (T9.4s4) too.
- **Round 27** (2026-11-15, 50 %): no way won everywhere. Half speed for all won 3 of 6 pairs, tonal sounds first 2 of
  5, drums first 1 of 2, no shrinking 1 of 4 (2 can't tell); David: percussion "is extremely important"
  ([round 27](../../tests/golden/listening/README.md#round-27-squeezed-to-half)). The order depends on the song, so the
  rounds go on.
