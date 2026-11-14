namespace Mod2Xgm.Render;

/// <summary>Why a note is heard on a side other than its MOD channel's (docs/adr/0108-pan-swap-count.md).</summary>
internal enum PanCause
{
    /// <summary>Nothing moves it: it plays on its own side.</summary>
    None,

    /// <summary>A recording follows the one DAC pan every recording shares on XGM and XGM2 (<c>DacPanClash</c>).</summary>
    SharedDac,

    /// <summary>A pre-mix of notes from both sides plays from both speakers (docs/adr/0094-pre-mixed-voice-steals.md).</summary>
    PreMix,

    /// <summary>An FM bass note plays centred (<see cref="Mapping.PlanVoicing.CentredBass"/>).</summary>
    CentredBass,

    /// <summary>The PSG has no pan: it always plays from both speakers.</summary>
    Psg,
}

/// <summary>Where one sounding note was heard on one tick of a render, next to where its MOD channel puts it.</summary>
/// <param name="Tick">Index into the playback's ticks.</param>
/// <param name="Seconds">When the tick starts, in seconds of the render.</param>
/// <param name="DurationSeconds">How long the tick lasts.</param>
/// <param name="Sample">The song sample the note plays (0-based).</param>
/// <param name="Source">The song channel the note comes from (0-based).</param>
/// <param name="Original">The side the MOD plays it on: −1 left, 0 both, +1 right.</param>
/// <param name="Heard">The side the Genesis plays it on, the same way.</param>
/// <param name="Cause">Why <paramref name="Heard"/> isn't its own side, or <see cref="PanCause.None"/>.</param>
/// <param name="Volume">The MOD volume on the tick, 0–1.</param>
internal readonly record struct PanTick(int Tick, double Seconds, double DurationSeconds, int Sample, int Source, int Original, int Heard, PanCause Cause, double Volume);

/// <summary>
/// Collects, on every tick of a Genesis render, the side each sounding note plays on and its MOD channel's side
/// (plan T9.4r, docs/adr/0108-pan-swap-count.md). Only the harness asks for it; the converter's own renders leave it out.
/// </summary>
internal sealed class PanLog
{
    private readonly List<PanTick> _ticks = [];

    /// <summary>Every note on every tick it sounded, in the order the render played them.</summary>
    public IReadOnlyList<PanTick> Ticks => _ticks;

    /// <summary>Adds one note's tick.</summary>
    public void Add(PanTick tick) => _ticks.Add(tick);
}
