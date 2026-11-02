# ADR 0072: Pre-mix candidates: every group of 2–4 overlapping notes, tails pay their own bytes (T8.5a)

- **Status:** Accepted; supersedes the decision "Tails share bytes where the format allows" of [ADR 0052](0052-pre-mixed-pcm.md)
- **Date:** 2026-11-01

## Context

Plan task T8.5 built two kinds of combined candidates: the pre-mixed PCM of [ADR 0052](0052-pre-mixed-pcm.md) and the CH3
special-mode pairs of [multi-pass.md](../multi-pass.md#candidates). They share no code, so David split the task: T8.5a
(this ADR) makes the pre-mixed candidates, and T8.5b makes the locked pairs and CH3.

ADR 0052 set what a pre-mix is: notes that overlap, mixed into one sample from the note events, deduplicated by bytes,
with a tail for a note that outlasts the overlap, and a seam measured with the PCM driver models. It left these open:
how many notes go into one pre-mix, whether windows have a length cap, what to do with an audible seam, how to count the
voice steals pre-mixing removes before the allocator (M9) exists, and whether a tail can share its sample's bytes. That
last one was marked **verify** for XGM2 and assumed for XGM v1.

## Decision

(David, 2026-11-01, unless marked)

- **Candidates only.** As for T8.2–T8.4, the converter's output doesn't change: `PreMixer` makes the candidates for DMF,
  XGM and XGM2, the harness writes them with `--pre-mix`, and the allocator (M9) uses them. The conversion notes and the
  output sample table's pre-mix rows (ADR 0052) come with M9, when a pre-mix is actually used.
- **Every group of 2–4 overlapping notes.** If a kick, a bass and a snare overlap, `kick+bass`, `kick+snare`,
  `bass+snare` and `kick+bass+snare` are all offered, where those notes overlap. At most 4 notes, the most PCM voices any
  target has (XGM). A group is one window only when its notes are linked by overlaps and two or more of them sound in one
  unbroken stretch: a kick and a snare that each overlap a long bass but not each other give `kick+bass` and
  `bass+snare`, never one window with a gap (implementation).
- **The window** runs from the first note's start to the end of that stretch; at most one note outlasts it.
- **No length cap.** A long overlap is offered like a short one; the allocator weighs its bytes.
- **Both endings are always offered.** A note that outlasts the window goes on as a **tail**, and a one-shot one is also
  offered **baked** to its end (no tail, no seam, more bytes). A looped note is never baked (ADR 0052). Both are kept
  whatever the seam sounds like: the seam is measured and reported, not used to drop a candidate.
- **Tails pay their own bytes, in every format.** SGDK's driver would play a slot that points into another sample's
  data, but no `.xgm` or `.xgm2` reaches a ROM as written: rescomp always runs it through xgm2tool or xgmtool, and both
  rebuild the sample block slot by slot, so a shared tail turns into a copy (see [formats/xgm.md](../formats/xgm.md)).
  A tail is the rest of the outlasting note's sample, from where the note is at the switch, at its rate and volume
  then, so it carries on at the level the mix left it at. Tails with the same bytes share one slot (`tail_id`).
- **Voice steals are counted as notes over the limit.** For each target's voices (DMF 1, XGM 4, XGM2 3), a note that
  starts while every voice is busy counts as stolen; voices are given out in the order notes start. Then again with
  every candidate used where it fits: each note goes into one pre-mix at most, larger groups first, and a pre-mix holds
  one voice from its window's start to its last note's end. The difference is the steals removed. Each note-start check
  is greedy, so this is the count of an allocator without foresight (implementation).

How it works (implementation):

- **Finding windows.** From the notes of `SongNote.Find`, of samples with sound that aren't kept out (`--require-fm`,
  `--require-psg`; ignored samples aren't analyzed). Two notes overlap when they sound on a common tick.
- **Dedupe.** Windows whose notes have the same samples, start ticks, start frames and pitch and volume on every tick
  (with the tick lengths) make the same bytes, so each such group is rendered once. A group is offered when the song
  plays it at least twice, or once where it saves a voice steal (ADR 0052). Offered groups with the same bytes are then
  merged into one candidate, their occurrences added.
- **Mixing.** The MOD renderer plays the notes in mono, other notes on their channels silenced, scaled so one note at
  full volume is full scale. A mix over full scale is turned down to it (`clip_db`, 0 when it wasn't). It is resampled
  to the target's rate (DMF: the DAC rate code of the highest note) and quantized once, by the driver model.
- **The switch.** On DMF the tail starts on the window's last tick, as DefleMask plays the song's ticks. On XGM and XGM2
  it starts on the next 1/60 s frame, when the driver reads the music's commands. A pre-mixed sample's 256-byte padding
  holds what follows of the outlasting note, so a tail that starts a little late doesn't leave a gap.
- **Scores.** The pre-mix through the target's PCM driver model (`PcmNoteRenderer`, the tail started on the same channel
  at the switch) against the MOD renderer's mix, by the FM fits' ruler (`NoteScorer`, mel bands only, as a mix has no one
  pitch): over the window, at least 0.1 s and at most the analysis's second; and the **seam**, ±0.1 s around the switch,
  for the tail and its baked twin alike.

## Corpus result

CORPUS_RESULT

## Options considered

- **Every group of 2–4 notes** (chosen). **Pairs only**: misses three drums on one hit. **Every group, no limit**: a
  32-channel MOD gives thousands of groups for one hit, and no target has more than 4 voices to save. **Only notes that
  all overlap each other**: misses a kick and a snare both over a long bass.
- **No length cap** (chosen). **2 s or 1 s caps**: smaller and faster, but would hide long held overlaps from the
  allocator, which is the one that knows the byte budget.
- **Always offer both endings** (chosen). **Drop the tail when its seam is over 1 dB worse than baked**, or **when the
  wave jumps by more than a set step**: either throws away a candidate the allocator might still prefer for its bytes.
- **Tails pay their own bytes** (chosen). **Count both a file and a ROM cost**: the file cost is never what a game
  pays. **Share anyway in our files**: rescomp undoes it, so the saving would be on paper only.
- **Steals as notes over the limit** (chosen). **Ticks over the limit**: shows how long voices are crowded, not how many
  notes lose their voice. **Both**: one number is enough until the allocator gives the real one.

## Consequences

- New: `PreMixer` (`PreMixEnd`, `PreMixPart`, `PreMixTail`, `PreMixCandidate`, `PreMixSongFit`) and `PreMixReport` in
  `src/Optimizer/`; `PcmNoteRenderer` plays a head then a tail on one channel; `ModRenderer.MonoChannelLevel`.
- The harness gets `--pre-mix`, `pre-mix.csv` and `voice-steals.csv` ([harness.md](../harness.md#output)).
- ADR 0052's tail-sharing decision is superseded; [formats/xgm.md](../formats/xgm.md) says why. Its other decisions stand.
- [implementation-plan.md](../implementation-plan.md) splits T8.5 into T8.5a (done here) and T8.5b (locked pairs and CH3,
  with David's choices recorded in its row).
- M9's allocator weighs these candidates (score, bytes, slots, voices freed × occurrences), adds their conversion notes,
  and gives the real steal count.
