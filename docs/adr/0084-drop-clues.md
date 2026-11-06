# ADR 0084: Drop clues per song track, set by listening

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

When a song wants more voices than the Genesis has, something has to go. Plan T8.7 asks how cheap each song track is to drop
or thin out: one that only echoes or doubles another, a very quiet one, one buried under louder ones, and mirrored tracks
that can merge into one voice. The allocator (M9) reads the result. The scores agree only weakly with ears
([ADR 0049](0049-simple-mapper-baseline.md)), so David judged the cutoffs by A/B clips.

## Decision

(David, 2026-11-06, unless marked as implementation.)

- **A track is a sample on a channel**, as for locked pairs. A snare and a hi-hat on one channel get separate clues.
- **Each clue stays separate with its own number.** Nothing is combined into one grade, and nothing is dropped here.
- **Whole song, keeping the notes.** Clues cover the whole song, and each keeps the list of notes it applies to, so M9 can
  use the ones in a section.
- **A copy needs the same sample at the same pitch**, within 10 cents. Another sample or an octave is a part of its own.
- **A copy covers at least 3 of the track's 4 notes**, and at least 8 notes in all. The clue is on the track that could go,
  with its exact share. A pair where each covers the other gives one clue each way.
- **Double:** the same notes at the same tick on the same side, or in the middle. **Mirror:** the same notes on opposite
  sides, by the mean pan of the shared notes (implementation).
- **Echo:** the same notes a steady number of ticks later, within 1 second, and quieter. Quieter means at least 1 dB on
  average (implementation). A track echoing itself on its own channel is not looked for (implementation).
- **Quiet:** the track's mean level while it sounds is at least 22 dB below the song's loudest track.
- **Buried:** a moment of a note is buried when any other track is at least 12 dB louder, whatever its pitch. A note is
  buried when 75 % of its energy is (implementation), and a track when 75 % of its notes are.
- **Clues are hints, not proof.** They say what to try first. M9 has to judge the result, because listening shows that no
  number here predicts what can be heard.
- `--verbose` logs the clues, and the harness's `--drop-clues` writes `drop-clues.csv` and `track-levels.csv`.

## Listening

Each pair played the MOD as written against the same MOD with one track pointed at an empty sample slot, through libopenmpt,
for the 15 seconds where the track is most present. The clips came from a throwaway script, so they aren't reproducible from
the repository.

| Pair | Track | Below loudest | Buried at | Removed against the clip's mix | David |
|---|---|---|---|---|---|
| 1 | sharpojects_cantpleaseeveryone sample 7, ch 2 | 22.6 dB | | −32.4 dB | not heard |
| 2 | k0w-rsnd sample 9, ch 2 | 19.5 dB | | −13.6 dB | not heard |
| 3 | sharpojects_cantpleaseeveryone sample 30, ch 3 | 17.2 dB | | −17.9 dB | not heard |
| 4 | drozerix_-_stardust_jam sample 9, ch 4 | 21.4 dB | 12 dB | −24.9 dB | **heard** |
| 5 | drozerix_-_the_hawks_claw sample 9, ch 2 | 15.7 dB | 9 dB | −19.9 dB | **heard** |
| 6 | jam_-_xlr8 sample 3, ch 2 | 9.5 dB | 6 dB | −20.0 dB | **heard** |
| 7 | drozerix_-_the_hawks_claw sample 6, ch 2 (echo of ch 1) | | | −10.9 dB | **heard** |

What that means:

- **Quiet:** 22 dB is the one cutoff that fits every answer: pair 1 was unheard at 22.6 and pair 4 heard at 21.4. The margin
  is thin, and pairs 2 and 3 show that some louder tracks could go too.
- **Buried:** every track tried was heard, so no cutoff is proven safe. At 15 dB no track qualifies, so 12 dB is the
  strictest cutoff tried that marks anything. It stays as a hint: pair 4 passes it and was heard.
- **Echo:** the clue finds a real copy, but losing it can still be heard. An echo is cheaper to lose, not free to lose.
- **Removed level doesn't predict it.** Pair 2 was the loudest removal among the first six and wasn't heard; pair 4 was among
  the quietest and was.

### Soft notes right after a hit

David noticed that a soft sound is harder to miss right after a sharp, loud one. As a measure of a whole track it runs the
wrong way: with notes starting within 0.12 s after a sharp hit at least 6 dB louder, the heard pairs 4 and 5 score 90 % and
95 %, against 4 %, 22 % and 48 % for pairs 1–3. Removing only those notes from pairs 4 and 5, leaving 10 % and 5 %, was still
heard. So it isn't a clue here. It may still matter for single notes, which this test can't settle.

A share of the track in frequency bands above the rest of the mix didn't separate the pairs either. Seven pairs are too few to
fit a formula.

### Baking notes together instead of dropping them

Those same tracks are why David suggested a meta sample: bake a track with the hit it follows into one larger sample, so
they need one channel. That is pre-mixed PCM ([ADR 0052](0052-pre-mixed-pcm.md), [ADR 0072](0072-pre-mix-candidates.md)),
already among the candidates and already covering them: in `drozerix_-_stardust_jam`, sample 9 with sample 5 is offered as
3,328 bytes on XGM2, for 91 times the two start together. Nothing new is built; M9 chooses between dropping a track and baking it.

## Corpus result

Full corpus (19 songs, 434 tracks), `--drop-clues`, 2026-11-06. A track can carry more than one clue.

| Clue | Clues | Songs | On tracks of 8 notes or more |
|---|---|---|---|
| Doubles | 0 | 0 | 0 |
| Mirrors | 3 | 1 | 3 |
| Echoes | 8 | 4 | 8 |
| Quiet | 7 | 5 | 3 |
| Buried | 13 | 6 | 8 |

| Song | Mirrors | Echoes | Quiet | Buried |
|---|---|---|---|---|
| 8bit_castle | | | | 2 |
| cancan | | 1 | | |
| drozerix_-_ai_renaissance | | 1 | | 2 |
| drozerix_-_stardust_jam | | 3 | 2 | 3 |
| drozerix_-_the_hawks_claw | | 3 | 1 | |
| hit | 3 | | 1 | |
| jam_-_latenightcoffee | | | | 4 |
| musix-omg-plz-help | | | 1 | 1 |
| sharpojects_cantpleaseeveryone | | | 2 | 1 |

Ten songs have no clue. The mirrors are `hit`'s doubling: sample 9 each way, and sample 3 on channel 2 (98 % covered; channel 1,
at 59 %, isn't marked). No corpus song doubles a track on the same side.

## Options considered

- **Buried only within the same pitch range:** my recommendation, on the reasoning that a high sound isn't hidden by a low
  one. David chose any louder sound. The band check above doesn't clearly favor either.
- **A share of one in two or nine in ten:** one in two could lose half a track's notes; nine in ten leaves M9 less to choose
  from. M9 can be stricter, since the exact share is kept.
- **One grade per track:** fixes the weights before M9 exists and hides why a track was marked.
- **Clues per section:** more work now; keeping the note lists leaves it open.
- **Removing the buried clue, as nothing proved it safe:** David chose to keep it as a hint, knowing he heard every track it
  tried. As a hint of what to try first it costs little. If M9 finds it misleads, it can go.

## Consequences

- `DropClues`, `TwinClue`, `TrackLevel` and `DropClueReport` in `src/Analysis`; the harness gains `--drop-clues`.
- The aim for M9 (David, 2026-11-06): try many ways of cutting the song down to fit, and choose the **combination of changes
  that is least perceptible**, with A/B listening against the original MOD wherever possible
  ([multi-pass.md](../multi-pass.md#passes)). M9 can't take a clue as proof that a change is safe.
- `--require-fm`, `--require-dac` and `--require-psg` don't filter clues: required samples may carry them, and M9's hard
  constraints keep them (T9.3).
