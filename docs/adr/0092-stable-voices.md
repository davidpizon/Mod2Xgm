# ADR 0092: Stable voices: the song's order, and a sound keeps its voice

- **Status:** Accepted; supersedes the track order and the switch rule (`SwitchCostDb`) of [ADR 0087](0087-greedy-allocator.md)
- **Date:** 2026-11-08

## Context

Plan task T9.4c. In round 12 ([ADR 0091](0091-shrinking-recordings.md)), David heard neon_techno's left pad jump from the
left speaker to another place partway through the song, where the original stays put: "quite noticeable and jarring".

The cause was the greedy allocator ([ADR 0087](0087-greedy-allocator.md)). It chose again in every section (one order):
- tracks went by their notes' importance *in that section*;
- a track kept its sound only when no other sound was 3 dB closer (`SwitchCostDb`).

So a track could move between recording, FM and PSG from one order to the next. Each move changes the sound's colour, and
for a recording also its speaker.

David's answers:
- any switch is jarring, even one that stays on the same side;
- his ears decide the rule;
- the work goes in its own task and PR.

## Decision

- **Rule: `VoiceSwitching.SongRank`** (`src/Optimizer/VoiceSwitching.cs`, `OptimizerMapper.Switching`), David's pick in rounds
  13–15:
  - Sections are still chosen one at a time, in order.
  - Within each, tracks go in **one order for the whole song**: their notes' mean importance over the song, not over the
    section. So a late but important sound chooses before sounds that matter less overall.
  - A track **keeps the sound it had** while that sound has room for it. It moves only for lack of room, never because
    another sound would be a little closer.
  - Within a section, a track still tries one sound for all its notes first, then goes note by note (unchanged).
- The other rules stay in `VoiceSwitching` for tests and comparison: `PerSection` (the old rule), `KeepPlaying` and
  `WholeSong`. `OptimizerMapper` takes one as an optional constructor argument; the converter always uses `SongRank`.

### Rounds

Rounds 13–15 were blind, on XGM2 renders played as WAV, each version against the original. They used 30-second clips placed
where the rules differ.

| Round | Songs | Versions | David's picks |
|---|---|---|---|
| 13 | neon (×2), hit, omg-plz-help | per section, keep playing, whole song | neon: none (all "unsatisfactory"); hit: **keep playing**; omg: **whole song** |
| 14 | hit, omg | whole-song-first hybrid, keep playing, whole song | hit: **keep playing**; omg: **whole song**, very close to the hybrid |
| 15 | hit, omg | **song rank**, keep playing, whole song | hit: **song rank**, "by far the best"; omg: **song rank**, "a clear winner" |

Why the earlier rules each lost one song:
- **Keep playing (sounds already playing choose first)** shut out omg's late lead: it got only PSG noise and sounded off
  pitch (+32 cents by chroma).
- **Whole song (one choice per track for the song)** put a sound that couldn't be a recording everywhere on FM or noise for
  the whole song. hit's bass went to FM and its snare to noise.
- Song rank keeps hit's bass and snare as recordings while there is room, and gives omg's lead a recording.

How often a track changes kind over the song (counted from the plans):

| Song | Per section (old) | Keep playing | Whole song | Song rank |
|---|---|---|---|---|
| neon_techno | 47 | 6 | 0 | 2 |
| hit | 9 | 12 | 3 | 12 |
| omg-plz-help | 14 | 4 | 5 | 8 |

Fewer switches did not by itself win: in hit, song rank switches as often as keep playing (mostly a 12-note tom late in the
song) and still sounded best.

## Options considered

- **Song rank** (chosen): won both songs in round 15.
- **Per section** (the old rule, ADR 0087): the most switches; never picked.
- **Keep playing:** won hit in rounds 13 and 14; lost omg, where a late, important sound got only PSG noise.
- **Whole song:** won omg in rounds 13 and 14; lost hit, where a sound with no room on one voice for the whole song plays a
  worse sound throughout.
- **Whole song first, then per section for the rest** (round 14's hybrid): almost the same as whole song (identical on neon),
  and picked in neither song. Removed.
- **A steady DAC pan for the whole song:** offered, not built; the pan of mixed recordings is [ADR 0091](0091-shrinking-recordings.md)'s.

## Consequences

- `OptimizerMapper.Allocate` orders tracks by the song-wide importance and `Ranked` prefers the sound a track had. The doc
  comments say so. ADR 0087's other rules are unchanged: importance, the percussive boost, the first try at one sound per
  section, and the column assignment.
- Tests: `Map_WithMoreImportantNewcomer_KeepsOrGivesUpTheVoiceAsTheRuleSays` checks each rule's choice for a pad and a
  more important newcomer.
- **neon_techno is still unsatisfactory** under every rule. It needs 4 PCM voices at once (XGM2 has 3) and about 977 KB of
  recordings (budget 128 KB), so too few voices is the problem, not switching. That is for T9.4b (pre-mixed drums,
  [ADR 0052](0052-pre-mixed-pcm.md)).
- In round 15, David heard note ends that sound clipped in hit under keep playing. Not looked into here.
