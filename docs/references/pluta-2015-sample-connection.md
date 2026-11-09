# Pluta & Delekta (2015): smoothly connecting sound samples in sampling synthesis

| | |
|---|---|
| **Citation** | Marek Pluta, Rafał Jacek Delekta (Academy of Music in Kraków). "Technique to Smoothly Connect Sound Samples in Sampling Synthesis." 2015. The venue is not printed on the paper. The file name (`OSA2015…`) suggests the Open Seminar on Acoustics 2015 *(unverified)*. |
| **Links** | [ResearchGate 303286994](https://www.researchgate.net/publication/303286994_Technique_to_smoothly_connect_sound_samples_in_sampling_synthesis) |
| **Copyright** | The authors (no licence shown). Summarized in our own words; not copied. |
| **Access status** | Full text read, 12 pages, from a PDF David downloaded from ResearchGate. Table 2 (listening scores) came out of the PDF with its columns jumbled, so per-variant scores are not given here; the conclusions below are the authors' own. |
| **Used by** | PCM seams: pre-mixed PCM tails ([ADR 0052](../adr/0052-pre-mixed-pcm.md), [ADR 0072](../adr/0072-pre-mix-candidates.md)), drum pre-mixing (plan T9.4b), sample merging ([../sample-capacity.md](../sample-capacity.md)) |

## TL;DR for agents

To join two recordings of the same pitch in the middle of a sustained note:
1. **shift one by the lag that maximizes their cross-correlation** over the overlap, so they are in phase;
2. **match their RMS levels**;
3. **crossfade**.

A plain crossfade without the phase shift gives an audible dip in level, because the two signals partly cancel.
In a small test with two listeners, joins of roughly 28–57 ms, early or mid-note, were accepted. Very short
fades (1–3 ms) and joins near the note's end were rated worst. For us, this is the recipe for any seam between
*two different* recordings. A seam inside one recording (a pre-mix head going on into its own tail) is meant to
need only sample alignment, though resampling and frame rounding mean the continuation is close, not exact.

## Problem

Sample libraries record single notes, so note-to-note transitions are missing. In wind instruments those
transitions are audible and depend on context. The authors' system (SSW, for orchestral wind parts) records
short melodic figures instead and joins them on the **sustain** of a note the two figures share. That join has
to be inaudible.

## Method

- **Sample set:** per instrument, 5000–6000 recordings at 88.2 kHz (A/B and X-Y stereo), covering:
  - tetrachords, both directions;
  - intervals from a minor second to an octave, both directions;
  - single notes in many articulations;
  - full multi-sampling, so nothing is resampled.
- **Score analysis:**
  - phrases start at the melody's start, at a slur's start or after its end, on a repeated note, and after a rest;
  - each phrase is covered by overlapping figures, with only the phrase's first figure played from its attack;
  - every way of covering the phrase is collected, and the one with the **fewest joins** is picked, with ties
    going to the one that uses most of each figure. A cover always exists, because any melody can be built
    from intervals.
- **Note regions:**
  - autocorrelation f0, guided by the known pitch sequence;
  - the window and the minimum peak distance start strict and are relaxed on failure;
  - octave errors are allowed for, and gaps between equal pitches are filled;
  - the result is each note's start and end within the recording, which can be corrected by hand.
- **Join (their Fig. 4):** at the shared note's sustain, take the overlap area of both recordings, then:
  1. find the lag with the largest cross-correlation, and shift one recording by it. The shift is small enough
     not to be heard as a timing change.
  2. scale one recording to the other's RMS over the area;
  3. crossfade.

## Results

- **Test:** flute; two excerpts with one join each:
  - two slow thirds joined into a C major triad;
  - two fast tetrachords joined into a C major scale.
- **Variants:** fade lengths from 1.25 ms to 283 ms, with the join at the start, middle or end of the sustain.
- **Raters:** two orchestra conductors, scoring 1 (worst) to 4 (inaudible).
- **Findings (the authors' reading):**
  - very short fades are rated low by at least one listener, wherever they are placed;
  - joins at the **end** of the sustain are rated lower;
  - for the scale, an average-length and the longest fade rated best;
  - for the triad, about **57 ms at the start** or about **28 ms in the middle** rated best.
- Fades short enough to leave room for changing note lengths seem acceptable, if they are not at the note's end.

## Limitations

- Two listeners, one instrument, one join per excerpt: a preliminary test, as the authors say.
- Joins are only between recordings of the same instrument and the same pitch.
- No objective measure of the seam.
- How short a fade can be while staying inaudible is left for later work, as is time-stretching the sustain by
  inserting or removing segments.

## What applies to Mod2Xgm

| Paper idea | Mod2Xgm decision |
|---|---|
| Cross-correlation shift, then RMS match, then crossfade | **The recipe for seams between different recordings**, if we ever make one: a drum pre-mix (T9.4b) that hands over to another sample mid-sound, or a merged look-alike swapped in mid-note. To adopt it, each such seam gets a short aligned crossfade, measured by the seam score (ADR 0072's `seam_mel_db`). |
| A plain crossfade dips in level through phase cancellation | A reason never to *mix* two near-identical copies at a seam without aligning them first. Also a caution for layering (one sound on two voices): unaligned copies of one sample comb-filter. |
| Seams inside one recording | Probably not needed for the pre-mix head → tail switch ([ADR 0052](../adr/0052-pre-mixed-pcm.md)). The tail continues the same source sample, so phase is *meant* to line up, but it isn't guaranteed: head and tail are resampled separately, and `PreMixer.FrameAt` rounds the source position to a frame. Measure the seam for all three effects (resampling, frame alignment and the driver's own switch timing, which the PCM driver models give) before deciding no crossfade is needed. |
| Fades of about 28–57 ms are accepted; 1–3 ms are not; joins near a note's end are worse | Starting points for any crossfade length we choose. At the DAC's rates (about 13.3 kHz for XGM2, 14 kHz for XGM) that is about 370–800 frames, so a fade costs bytes in a 128 KB budget. Prefer seams early in a sustain, not just before a release. |
| MOD loops | **Not changed.** A MOD's loop points are part of the song as written; the converter keeps them as they are and doesn't crossfade them. |
| f0 guided by the known pitch | The analysis pass already knows each note's pitch from the MOD period. The same "expected pitch first, relax on failure" idea fits measuring a sample's cycle in the analysis pass. |
| Fewest joins wins | Agrees with T9.4c: each extra seam or voice switch is a chance for an audible change, so plans with fewer of them are preferred. |
