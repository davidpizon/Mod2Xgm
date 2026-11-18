# ADR 0125: A chord or chorused sample plays its bottom note on FM

- **Status:** Proposed
- **Date:** 2026-11-18

## Context

David converted FunkyChristmas to XGM2 (2026-11-18, plan T5.3i) and reported three problems:

- The melody instruments sounded out of tune.
- The background instruments sounded out of tune.
- The orchestral sound that builds at the start sounded like a buzz.

Its strings, pads and orchestra hits (samples 1, 2, 14, 15 and 19) are recorded chords or chorused tones. The simple mapper
played each of them on one FM voice at a pitch it had measured wrong, in one of two ways:

- **No cycle.** A chorused chord never repeats, so the cycle search ([`SamplePitch`](../../src/Mapping/SamplePitch.cs),
  YIN) found nothing. The fallback then assumed the sample was tuned to C and played FM notes two octaves above the MOD
  note's name. In samples 2, 14, 15 and 19, the FM voice's main note was one of the original's notes in only 0.1–4.7 %
  of frames.
- **A cycle below every note.** Sample 1's loop repeats exactly at the chord's common cycle. A just major triad is
  harmonics 4, 5 and 6 of a cycle two octaves below its root. A library patch sounds its own fundamental, so it played
  that cycle: two octaves below the chord, a buzz. Its four loudest harmonics hold too little (87 %) for a harmonic
  patch ([ADR 0118](0118-harmonic-patches.md)).

## Decision

- **No cycle: play the bottom note.** When a tone loop has no cycle within the longest searched (1024 frames), its pitch
  is its bottom note. The bottom note is the lowest partial within 20 dB of the loudest in the loop's spectrum:
  - the spectrum is taken over the loop repeated to 65,536 frames, Hann-windowed;
  - each partial is counted within ±25 cents of it, which covers chorus spread and an equal-tempered chord's distance
    from a just one (up to 16 cents);
  - the note is placed at the power-weighted centre of that band, so a chorused partial counts once, at its middle.

  The pitch is marked as a chord, and a conversion note says so.
- **A cycle below every note: lift the library patch.** The harmonic measurement also gives the lowest harmonic within
  20 dB of the loudest (`HarmonicPatch.Lowest`). When a library patch plays the sample, it is lifted to that harmonic by
  1200 · log2(lowest) cents (`PlanCell.Cents`): two octaves for harmonic 4. A harmonic patch is not lifted, as it
  sounds the harmonics at the cycle itself. A conversion note says which harmonic is played.
- **Measure loops that aren't whole cycles.** A loop that isn't a whole number of cycles (a chorused tone) used to get no
  harmonic measurement. Now each harmonic's power is the power within ±25 cents of it in the loop's spectrum. It gets no
  measurement only when its loudest band holds less than 1 % of it, because then the cycle doesn't describe it.
- **One voice plays the chord's bottom note.** Playing the whole chord, on several FM voices or on PCM, is a
  channel-mapping decision. Playing a chord or swell sample as a recording stays a choice made on the command line
  (`--require-dac`), not a default (David, 2026-11-18, after FunkyChristmas's opening swell sounded right as a recording).
  Playing it on several FM voices is left for A/B rounds.
- **Only the simple mapper changes.** The optimizer (`--mapping optimizer`) is unchanged. On FunkyChristmas it still
  leaves out sample 2 and plays pads 14 and 15 on PSG noise.

## Results on FunkyChristmas

Per-sample renders, compared with a 12-note chroma per 186 ms frame against the original:

- Samples 2, 14, 15 and 19: the FM voice's main note is one of the original's notes in 96.6–100 % of frames, up from
  0.1–4.7 %.
- Samples 6, 8 and 23 (tones with a cycle) are unchanged, at 92–99 %.
- Sample 1 moved from G2 to G4, the chord's bottom note.

## Options considered

- **Chosen: the bottom note.** It is the note one voice standing for a chord should play, and it is found by the same
  spectrum for chords and for chorused tones.
- **Keep the C-tuning fallback.** This was the bug.
- **Root by common cycle:** search for the cycle whose harmonics hold the partials. Within 35 cents it read chords as
  septimal (6:7:9) and picked wrong roots: C2 for sample 2 and G1 for sample 14.
- **The loudest partial.** It is often the chord's third or fifth, so the voice drifts off the chord's bass line.
- **The whole chord on several FM voices, or on PCM.** Closer, but it needs channels the song may not have free. That is
  a mapping-strategy decision, to be asked first.

## Consequences

- A chord's FM voice plays one note, so chord pads sound thinner than the original but in tune.
- Loops that aren't whole cycles now get a harmonic measurement and its conversion note, for example "its four loudest
  harmonics hold only 81.4 %". The reporter goldens gained that note for their strings sample, with no change in pitch.
- The optimizer's handling of chord samples is a follow-up.
