# ADR 0074: Candidate cache: every kind kept per build, FM fits per region's sound, the rest per song file (T8.6)

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

Plan task T8.6 is the *fitted-patch cache (sample hash → patches), `--no-cache`*, accepted when *a rerun is fast and
identical*. [ADR 0010](0010-multi-pass-optimizer-sgdk-compliance-pure-csharp.md) and [multi-pass.md](../multi-pass.md)
promised it, and [ADR 0069](0069-fm-patch-fitting.md) found it necessary: without a render limit an FM fit takes
minutes per pitch region, and the corpus's 327 regions take 40 CPU-hours.

What was open:

- **Who uses it.** The conversion doesn't make candidates yet (plan M9); only the harness does (`--fm-fit`,
  `--psg-fit`, `--pcm-fit`, `--pre-mix`, `--ch3`). `--no-cache` was parsed and logged, and did nothing.
- **What it keeps.** The plan said FM patches, but T8.3–T8.5 added PSG, PCM, pre-mix and CH3 candidates since.
- **The key.** A fit depends on more than the sample's bytes: the pitch region the song plays it at, the fit options,
  the patch library and the chip model's code. PSG room, PCM, pre-mix and CH3 candidates depend on the whole song.
- **Staleness, size, location, and what a report shows for a result it didn't make.**

## Decision

(David, 2026-11-02)

- **Every kind of candidate is cached:** FM fits, PSG, PCM, pre-mix and CH3 candidates.
- **The harness uses it now; the conversion is ready for M9.** The harness reads and writes the cache unless
  `--no-cache`. The CLI gets `--cache-dir` beside `--no-cache`, both bound and logged; the conversion makes no candidates
  yet, so neither changes its output. Both commands reject `--no-cache` with `--cache-dir`.
- **Every key holds the build:** the compiled code's fingerprint, the Mod2Xgm assembly's module version ID. A
  deterministic build changes it exactly when the compiled code changes, uncommitted edits included, so a cached
  candidate is always one this code would make. It works under Native AOT (checked with the published binary).
- **FM fits are keyed per pitch region by its sound:** the region's note, fundamental and spectrum (every mel and
  harmonic frame, by its bits) and the fit options. The same sample played at the same pitch in another song, or as
  another sample number, is fitted once; its fitted patches are renamed for the sample that reads them (`Fit 5.1`).
- **The song-wide kinds are keyed per song file:** `SongAnalysis.Source`, set by the conversion from the MOD file's
  bytes, the player that reads it, the Amiga's clock and tick rate, and the samples left out; then the sample, target,
  options and samples kept out, as each kind takes them. CH3's key also holds the FM fits it cuts down, all but their
  times. An analysis without a source is never cached.
- **No size limit, but one folder per build, and a new build deletes the other builds' folders** (only folders named
  `build-` and 32 hex digits). Switching branches back and forth therefore refits.
- **Where:** the user's cache folder (`%LOCALAPPDATA%\Mod2Xgm\Cache`, `~/Library/Caches/Mod2Xgm`,
  `$XDG_CACHE_HOME/mod2xgm` or `~/.cache/mod2xgm`), or `--cache-dir` on both commands.
- **A result read back is the one made:** the same bytes, with the renders and seconds it took then, marked `Cached`.
  The harness's candidate CSVs end with a `cached` column (`yes`/`no`), and the FM log line says `(cached)`, with a
  summary line per song and kind.

How it is built (Claude, within the decisions above):

- One small binary file per key, `build-…/kind/<SHA-256>.bin`: a magic number, a format version, the kind and the key,
  then every field little-endian, doubles by their bits (`CandidateCodec`; no reflection, so AOT-safe). An object two
  fields share (an FM region's best fit and its search's fit, a CH3 candidate's pair) is written once and stays one object.
- An entry is written to a temporary file and renamed into place, so songs fitted at once and harness runs side by
  side never read half an entry. An entry that can't be read is made again and replaced (logged at `Debug`); a folder
  that can't be written is reported once at `Warning`, and the run goes on without the cache.

## Options considered

- **The kinds:** FM only (the plan's words) or FM and CH3 lost to every kind: the song-wide kinds are faster, but the
  pre-mixer and CH3 still take minutes on the corpus.
- **The build:** a hand-bumped fitter version (hits across builds, but a forgotten bump means stale fits), or the
  version and commit (`0.1.0+<commit>`, misses uncommitted edits) lost to the compiled code's fingerprint.
- **The song-wide key:** hashing the analysis itself (hits across files that measure the same, but much more key code
  to keep right), or keying FM per song file too (a shared sample fitted twice), lost to the song file plus options.
- **Old builds:** keeping every build's folder lost to deleting them, so the disk stays small.
- **A hit's report:** zero renders and seconds lost to the stored ones plus a `cached` column, so a rerun's CSVs equal
  the first run's but for that column.
- **The folder:** an environment variable, or a cache inside the harness's output folder, lost to the user's cache
  folder plus `--cache-dir`.

## Corpus

CORPUS_RESULT

## Consequences

- A rerun of the harness with the same build is fast, and its candidate CSVs equal the first run's but for `cached`.
- Plan M9's optimizer makes its candidates through `CandidateCache` (or `FmFitter.Fit` with the cache) and honours
  `--no-cache` and `--cache-dir`, which the conversion already binds.
- Almost any change to `src/` starts an empty cache, even one that only moves lines (the debug information is part of
  the build); long unlimited corpus fits are worth running on a build that won't change. Changes to tests, tools and
  docs keep it.
- A new field in a candidate record must be added to `CandidateCodec` too; the round-trip tests compare every kind
  byte for byte.
