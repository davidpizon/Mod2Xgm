namespace Mod2Xgm.Model;

/// <summary>A block of rows, played when an order names it.</summary>
/// <param name="Rows">The rows, in playing order (MOD: always 64).</param>
internal sealed record Pattern(IReadOnlyList<Row> Rows);

/// <summary>One row: a cell per channel, in channel order.</summary>
/// <param name="Cells">One cell per <see cref="Song.Channels"/> entry.</param>
internal sealed record Row(IReadOnlyList<Cell> Cells);

/// <summary>What one channel does on one row. Every part is optional; an empty cell does nothing.</summary>
/// <param name="Note">The note started on this row, or <c>null</c> for none.</param>
/// <param name="Instrument">Instrument index (0-based) selected on this row, or <c>null</c> to keep the channel's current one.</param>
/// <param name="Volume">Volume set on this row, 0–1 (MOD <c>Cxx</c>: xx / 64), or <c>null</c> to leave it.</param>
/// <param name="Effects">Effects on this row, in the order the player applies them; one source command may become several (MOD <c>5xy</c> = tone portamento + volume slide).</param>
internal sealed record Cell(NotePitch? Note, int? Instrument, double? Volume, IReadOnlyList<Effect> Effects)
{
    /// <summary>A cell that does nothing.</summary>
    public static Cell Empty { get; } = new(null, null, null, []);
}

/// <summary>
/// The pitch of a note, kept two ways (David, 2026-10-27): musically, for writers that place notes (DMF, the FM
/// mapper), and as an exact playback rate, for the renderers that must match the source to the sample (plan T7.1).
/// The reader computes both from the same source value (MOD: the Amiga period and clock, plan T3.2).
/// </summary>
/// <param name="Semitone">Nearest semitone, counted from C-0 (C-1 = 12, as trackers name octaves).</param>
/// <param name="Cents">Distance from <paramref name="Semitone"/>, −50 to +50 cents, unrounded.</param>
/// <param name="PlaybackRateHz">Samples per second at which the instrument's sample is played for this note.</param>
internal readonly record struct NotePitch(int Semitone, double Cents, double PlaybackRateHz);
