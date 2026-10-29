# ADR 0046: The MOD corpus (T6.1)

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

Plan T6.1 asks for 10–20 freely licensed MODs with their licenses in `tests/data/LICENSES.md`. T5.7 (the corpus opens
and plays in DefleMask and survives the SGDK path) needs them first, so T6.1 is done before it, as its own PR. The
repository is public, and AGENTS.md forbids committing copyrighted MODs. `docs/testing.md` asked for fixtures under
100 KB each; real songs are bigger.

## Decision

All by David, 2026-10-29:

- **Only Public Domain or CC0** songs go in the repository. CC BY, BY-SA and BY-NC were offered and declined.
- **Source: The Mod Archive**, using the license each artist set there. Claude searched its Public Domain and CC0
  listings (57 MODs) and left out covers of other people's tunes, songs whose notes say the samples came from someone
  else's song, a song whose text says "all rights reserved", a 32-channel file and files over 1.5 MB. David approved
  the remaining list of **18** songs (2.6 MB) in one go.
- **Mixed styles and effects**: chiptune, techno, dance, drum & bass breaks, slow trance, a melodic ballad, tiny and
  large files.
- **A 15-sample song made in code** (`CorpusSongs.Soundtracker15`, `soundtracker15.mod`), since none of the listed
  songs is a 15-sample Soundtracker file. It uses Ultimate Soundtracker's `1xy` arpeggio and `2xy` pitch bend. A real
  one can replace it if a Public Domain or CC0 file turns up.
- **Samples from elsewhere are noted, not excluded**: a few songs use Amiga "ST" sample-disk sounds or a sample
  library whose own rights aren't stated. LICENSES.md says so.
- Files are kept **unchanged** and **checked by SHA-256** (`CorpusTests`); the corpus is exempt from the 100 KB fixture
  limit.

## Options considered

- Public Domain / CC0 from The Mod Archive, one approved list (chosen).
- Also CC BY / BY-SA: more choice, but each needs attribution kept with the file; declined.
- David's own MODs, not committed: CI and other machines couldn't use them.
- Only songs made in code: no copyright doubt, but not real music, which is what T5.7 and the harness must judge.

## Consequences

- `tests/data/corpus/` holds 19 MODs; `tests/data/LICENSES.md` lists each with artist, license, source and checksum.
- T5.7, T6.3 (harness), T6.4 (baseline) and every later "on the corpus" acceptance use these files.
- The repository grows by about 2.6 MB.
- Adding or replacing a corpus file means a new LICENSES.md row; the tests fail otherwise.
