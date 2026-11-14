using Mod2Xgm.Render;

namespace Mod2Xgm.Optimizer;

/// <summary>Which pan swaps the MOD doesn't make the allocator lets stand: a step of plan T9.4r's ladder (docs/adr/0109-pan-swap-cost.md).</summary>
internal enum PanSwapAllowance
{
    /// <summary>Every swap stands, as before T9.4r.</summary>
    All,

    /// <summary>Only swaps that stay off side briefly, quietly and not far (<see cref="PanRules.Free"/>) stand.</summary>
    ShortQuiet,

    /// <summary>No swap stands while some fix is left.</summary>
    None,
}

/// <summary>How long a recording held in the middle stays there (docs/adr/0109-pan-swap-cost.md).</summary>
internal enum SteadyMiddleSpan
{
    /// <summary>Every note of the instrument, all song: it never goes back to its side, so it never jumps.</summary>
    Song,

    /// <summary>Only its notes in the orders (patterns) where it would jump; it goes back to its side between them.</summary>
    Order,
}

/// <summary>What the allocator may do in place of a pan swap (David, 2026-11-13, docs/adr/0109-pan-swap-cost.md).</summary>
[Flags]
internal enum PanFixes
{
    /// <summary>Nothing: every swap stands.</summary>
    None = 0,

    /// <summary>Play the instrument's recordings from both speakers, planned ahead, so a clash of sides never moves it.</summary>
    SteadyMiddle = 1,

    /// <summary>Play every note of one of the clashing instruments with its FM patch, which keeps its own pan.</summary>
    MoveToFm = 2,

    /// <summary>Don't make a pre-mix of notes from different sides, which plays centred; the note keeps the sound it had.</summary>
    SkipPreMix = 4,

    /// <summary>All of them.</summary>
    All = SteadyMiddle | MoveToFm | SkipPreMix,
}

/// <summary>
/// How the allocator weighs a pan swap the MOD doesn't make (plan T9.4r, docs/adr/0109-pan-swap-cost.md): a swap the rules
/// don't let stand is priced against the fixes in <see cref="Fixes"/>, and the cheapest fix is taken. A fix's price is its
/// notes' importance times dB: how much further its sound is from the MOD, and <see cref="OffSideDb"/> for each note held
/// on the wrong side. The centred FM bass (docs/adr/0087-greedy-allocator.md) is left out: it was chosen by ear.
/// </summary>
/// <param name="Allow">Which swaps stand.</param>
/// <param name="FreeSeconds">With <see cref="PanSwapAllowance.ShortQuiet"/>: the longest an instrument may stay off its side (<see cref="PanSwap.OffSeconds"/>).</param>
/// <param name="FreeVolume">With <see cref="PanSwapAllowance.ShortQuiet"/>: the loudest it may be meanwhile, as a MOD volume 0–1.</param>
/// <param name="FreeDistance">With <see cref="PanSwapAllowance.ShortQuiet"/>: the furthest it may move, 1 (a side and both) or 2 (side to side).</param>
/// <param name="Span">How long a recording held in the middle stays there.</param>
/// <param name="Fixes">What the allocator may do in place of a swap.</param>
/// <param name="OffSideDb">The price, in dB per unit of importance, of a note held steadily on the wrong side.</param>
internal sealed record PanRules(PanSwapAllowance Allow, double FreeSeconds, double FreeVolume, int FreeDistance, SteadyMiddleSpan Span, PanFixes Fixes, double OffSideDb)
{
    /// <summary>The price of a note held in the middle until the rounds set one: as much as a track's change of sound (docs/adr/0087-greedy-allocator.md).</summary>
    public const double DefaultOffSideDb = 3;

    /// <summary>Every swap stands: the allocator as before T9.4r.</summary>
    public static PanRules Today { get; } = new(PanSwapAllowance.All, 0, 0, 0, SteadyMiddleSpan.Song, PanFixes.All, DefaultOffSideDb);

    /// <summary>No swap stands while a fix is left: the top of the ladder.</summary>
    public static PanRules Never { get; } = Today with { Allow = PanSwapAllowance.None };

    /// <summary>
    /// No swap stands, and the only fix is holding recordings in the middle: round 25 (David, 2026-11-14) picked no swaps
    /// in all six pairs, and found FM in place of a recording "completely unacceptable" (docs/adr/0109-pan-swap-cost.md).
    /// </summary>
    public static PanRules Steady { get; } = Never with { Fixes = PanFixes.SteadyMiddle };

    /// <summary>The converter's rules (docs/adr/0109-pan-swap-cost.md).</summary>
    public static PanRules Default => Steady;

    /// <summary>True when the allocator ever looks at a swap.</summary>
    public bool Active => Allow != PanSwapAllowance.All && Fixes != PanFixes.None;

    /// <summary>True when <paramref name="swap"/> may stand.</summary>
    public bool Free(PanSwap swap)
    {
        ArgumentNullException.ThrowIfNull(swap);
        return Allow switch
        {
            PanSwapAllowance.All => true,
            PanSwapAllowance.None => false,
            _ => swap.OffSeconds <= FreeSeconds && swap.Volume <= FreeVolume && swap.Distance <= FreeDistance,
        };
    }
}
