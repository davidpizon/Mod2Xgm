namespace Mod2Xgm.Optimizer;

/// <summary>
/// Which tracks choose their sound first, and so keep a recording when the budget can't hold them all: a squeeze round's
/// way of fitting a smaller budget (plan T9.4t1, docs/adr/0113-squeeze-rounds.md). No option sets it: the harness does,
/// with <c>rule:keep</c> (docs/harness.md#listening-answers).
/// </summary>
internal enum KeepFirst
{
    /// <summary>The converter's own order: the tracks whose notes matter most first (docs/sample-capacity.md#importance).</summary>
    Importance,

    /// <summary>Percussive samples' tracks first (a hit is where FM is weakest), then by importance.</summary>
    Drums,

    /// <summary>Tonal samples' tracks first (melody, bass, chords), then by importance.</summary>
    Tonal,
}
