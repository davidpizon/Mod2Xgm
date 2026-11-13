# ADR 0107: The learned listening score is not adopted; log-mel stays the main number

- **Status:** Accepted (David, 2026-11-13)
- **Date:** 2026-11-13

## Context

[ADR 0097](0097-learned-listening-score.md) lets the learned listening score replace log-mel as the main number only after
it beats log-mel at predicting David's picks on songs it never learned from. [ADR 0105](0105-hold-out-songs.md) set the bar:
at least 30 answered hold-out pairs, at least 70 % agreement with David's picks, and at least 10 points above log-mel on the
same pairs. [ADR 0106](0106-cant-tell-smaller-wins.md) made a "can't tell" count as a pick of the smaller output file, in the
fit and on the hold-out.

Plan T9.4l ran three rounds on the 28 hold-out songs (`tests/data/holdout/`), 30 pairs, logged in
`tests/golden/listening/answers.tsv`:

- **Round 22:** 10 pairs the pair picker chose ([ADR 0102](0102-pair-picker.md)): half the memory, half speed on dull
  samples, `--no-dac`, `--mapping simple`.
- **Round 23:** layers off and pre-mixes off, 5 of each, kinds the score had never seen. The picker's 10 least-sure pairs
  barely differed (in 9 of them every measure moved by less than 0.01 dB), so David chose half and half: the picker's 5
  least-sure pairs, plus 5 whose difference was clearest across all seven measures, one per song.
- **Round 24:** every kind, picked the same half-and-half way. Only 8 hold-out songs were still unasked, so two of the
  clear pairs reuse a song with a different kind of change.

Before scoring, the score was fitted again with ADR 0106's rule on the 15 corpus answers. It kept the same one weight
(envelope modulation, 6.24 → 6.68), and none of the hold-out rows went into the fit.

## Decision

**The learned score is not adopted; log-mel stays the main number** (`harness --holdout`, 2026-11-13):

| On the 30 hold-out pairs | Learned score | Log-mel |
|---|---|---|
| Every pair (17 "can't tell" counted for the smaller file) | 43.3 % (13 of 30) | 66.7 % (20 of 30) |
| David's own a/b picks only | 61.5 % (8 of 13) | 84.6 % (11 of 13) |

The bar asks for 70 % and a 10-point lead; the learned score is 23 points behind. It fails the bar counted either way.

**What follows (David):** shelve the learned score and keep log-mel. Using the learned score in the converter (plan T9.4m)
is shelved too, and so is distilling a pretrained model (T9.4l2): T9.4l2 was only for a model that beat the C# features on
the hold-out, and the pretrained models aren't scored there. The answers file and the harness's `--fit`, `--pick`,
`--holdout` and `--embeddings` stay. The answers keep coming from every round, and the score can be tried again much
later, with many more answers and a new hold-out.

## Options considered

- **Shelve it and keep log-mel (chosen, David).** The bar exists for this case, and log-mel is the better guess of what
  David hears on both counts.
- **Score the pretrained models (CLAP, PANNs, VGGish, CDPAM) on the same 30 pairs**, and distil one that passes (T9.4l2).
  Each model's one weight was fitted on 15 answers, as the learned score's was, and none beat it on those. Not now.
- **Refit with all 45 answers and make a new hold-out.** Needs new songs, as all 28 hold-out songs have now been heard, and
  three more rounds. Not now.

## Consequences

- `ListeningWeights.cs` keeps its fitted weight, so `scores.csv`, `features.tsv` and `report.md` still show the learned
  score next to log-mel. It is a measure for reading, and nothing the converter decides uses it.
- Plan: T9.4l done; T9.4l2 and T9.4m shelved. The learned allocator prices (T9.4n–T9.4q, [ADR 0098](0098-learned-allocator-costs.md))
  are a separate model of David's answers and stay as planned. Their hold-out needs new songs, since these 28 have been heard.
- What the rounds taught the next ones (David's comments): pairs that barely differ mostly come back "can't tell"; a defect
  both versions share (harsh clipping in musix-burp, instruments jumping between speakers) distracts from the change being
  compared; missing instruments always lose. An instrument moving between the centre and one speaker mid-phrase is "very
  unsettling", which became plan T9.4r.
- 17 of the 30 answers were "can't tell". Many pairs differed too little to hear, and that limits what any score could show
  here.
