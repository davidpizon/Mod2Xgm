# ADR 0109: A price for pan swaps the MOD doesn't make; the threshold set by ear

- **Status:** Accepted
- **Date:** 2026-11-14

## Context

[ADR 0108](0108-pan-swap-count.md) counted the pan swaps the MOD doesn't make: an instrument heard on one side, then the
centre or the other side, while its MOD channel keeps one side. David finds them "very unsettling" (rounds 23–24). The
optimizer makes about 14 000 on XGM and XGM2 across the 47 songs. Nine in ten come from the one DAC pan every recording
shares (`DacPanClash.Centre`: a clash of sides plays from both speakers). The rest come from centred pre-mixes, the
centred FM bass and the PSG. The simple mapper's 3 000 all come from the PSG: a drum that loses the DAC to a louder one
falls to the noise channel, which has no pan.

Plan T9.4r asks for a cost the allocator weighs against the other ways out, not a fixed rule. Either/or rounds then step
down a ladder (never → only short, quiet swaps → today) to find the threshold, if there is one (David).

## Decision

David's answers, 2026-11-13 and 2026-11-14:

- **All of T9.4r in one PR:** the price, the rounds and the threshold.
- **The fixes the allocator may take** (`PanFixes`), each priced in dB × note importance, as the allocator prices its
  other choices ([ADR 0087](0087-greedy-allocator.md)):
  - **Steady middle**, planned ahead: the instrument's recordings play from both speakers (`PlanCell.Centred`), so a
    clash never moves it. David: "this condition should've been pre-planned for. The DAC probably would've been in the
    centre". Its price is `OffSideDb` (3 dB until the rounds set one) for each note held on the wrong side. With
    `SteadyMiddleSpan.Song` it stays there all song; with `Order` only in the orders where it would jump. The rounds pick.
  - **Move to FM:** every recording of the instrument, or of one it clashes with, plays its FM patch, which keeps its
    own pan. Its price is how much further FM is from the MOD. The whole instrument moves, so it keeps one sound
    ([ADR 0092](0092-stable-voices.md)).
  - **Skip the pre-mix:** a pre-mix of notes from both sides, which plays centred, isn't made when keeping the note's own
    sound costs less than holding the instruments in it in the middle.
- **The centred FM bass is left out:** it was chosen by ear ([ADR 0087](0087-greedy-allocator.md)).
- **Instruments steadily off side** may grow in number: `--pan-swaps` counts them, and the rounds judge them.
- **The ladder** (`PanRules.Allow`): `All` (today), `ShortQuiet` (a swap stands when its stretch off side,
  `PanSwap.OffSeconds`, its MOD volume and its distance are all within limits the rounds pick) and `None`. With `None`
  the allocator still lets a swap stand when no fix has room, and says so in a conversion note.
- **The simple mapper keeps one sound:** a drum on one side that loses the DAC either plays FM on its own channel
  (`DacLoser.Fm`, keeps its side) or plays the PSG noise all song (`DacLoser.NoiseAllSong`, steady in the middle). Today's
  rule, `DacLoser.Noise`, stays the default until a round picks.
- **Rounds:** XGM2 renders, as in rounds 12–24; Lazy Cows and Highway Encounter (where David heard it), the songs with
  the most swaps (hit, k0w-rsnd, musix-the-messanger, kc-dancinonamiga) and hold-out songs not used to tune; about 10
  either/or pairs per round, each a swap against the cheapest fix.

How the price works (`OptimizerMapper.PlanPans`): after the allocation and the pre-mix rescue, the allocator works out,
tick by tick, the side each note will be heard on, as the Genesis player plays it, and counts the swaps with
`PanSwaps.Count`, the counter of ADR 0108. While a swap the rules don't let stand is left, the instrument whose notes
matter most takes the cheapest fix that has room. Each fix holds more notes in the middle or takes more off the DAC, so it
ends. A DMF has no shared DAC pan and is left as it is.

The rules reach the converter only through the harness (`rule:pan…`, `rule:dac-loser`, [harness.md](../harness.md#listening-answers));
`PanRules.Default` is `PanRules.Steady`, as round 25 decided (see Results).

## Results

**Round 25** (David, 2026-11-14, headphones; [listening README](../../tests/golden/listening/README.md#round-25-pan-swaps-the-mod-doesnt-make)):

- `rule:pan=never` beat today in all six optimizer pairs (Lazy Cows, Highway Encounter, hit, rsnd, The Messanger,
  Dancin' on Amiga). David hears today's swaps as the sound "cutting" left and right. The ladder stops at its top:
  with nothing let stand, `short` left the same swaps as `never` on every round song, so there was no lower step to test.
- On Dancin' on Amiga the winner had moved the synth-chord samples to FM and "completely lacked the FM voice melody";
  on a second listen David added that "the bassline needs to be MUCH quieter". The bass, held in the middle at full
  level, measured 4 dB louder on the left than the MOD's low band, and as loud on the right.
- The simple mapper's four pairs were "can't tell": "both FM voice choices for the melody are completely unacceptable".

**What changed after it:**

- **The default is `PanRules.Steady`:** no swap stands, and the only fix is the steady middle. Moving a recording to FM
  is out (FM in its place was "completely unacceptable"), and so is skipping a pre-mix, whose note then plays FM too
  (round 23 heard FM standing in for a pre-mix as "just noise"). The steady middle spans the song: the per-order span
  wasn't heard, and only the whole-song hold never jumps. With no room for a hold, a swap stands and a conversion note
  says so.
- **A recording held in the middle plays one 3 dB step quieter** on each speaker (the stored copy one step down), so from
  both speakers it is as loud as from one, as the centred FM bass is (`PlanVoicing.CentredGain`). With every note of the
  sample held, it needs as many copies as before.
- **`DacLoser` stays `Noise`:** neither other choice is better to the ear.
- Every listening row made before this default carries `rule:pan=today`, which is how its clips were made, so it still
  remakes.

## Options considered

- **A price weighed against the other ways out (chosen, David).** The allocator picks the least change per song.
- **A fixed rule: one side per instrument, or every recording centred.** David turned both down: centring everything
  moves instruments that never clash.
- **Settling the clash on the louder side (`DacPanClash.Louder`).** Lost to the centre in round 12 ([ADR 0091](0091-shrinking-recordings.md)),
  and it still swaps the quieter instrument.
- **Pricing the centred FM bass too.** Left out: chosen by ear.

## Consequences

- `PanRules` and `PlanPans` in the optimizer, `PlanCell.Centred`, `PanCause.SteadyMiddle` and `PanSwap.OffSeconds`,
  `DacLoser` in the simple mapper; the Genesis player and the DMF translator play a held recording from both speakers.
- `--pan-swaps` also counts the versions with the rules, so each step of the ladder shows how many swaps it leaves and
  how many instruments it holds off side.
- **The PSG tone under an FM note now stops with it** where the MOD note falls silent. [ADR 0095](0095-voices-stop-with-the-mod-note.md)
  cut the FM voice and its FM layer but left the tone layer ([ADR 0088](0088-layering.md)) humming on from both speakers,
  which `--pan-swaps` counted as the instrument jumping to the middle: 639 of k0w-rsnd's swaps on XGM once its robot
  sound moved to FM. It changes every clip with a tone layer under a one-shot FM note, so older listening rows may no
  longer remake byte for byte ([README](../../tests/golden/listening/README.md#a-clip-changes)).
- The XGM2 writer records the player ([ADR 0099](0099-xgm2-writer-records-the-player.md)), so its output follows.
