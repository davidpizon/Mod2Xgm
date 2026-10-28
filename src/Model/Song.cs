namespace Mod2Xgm.Model;

/// <summary>
/// The format-neutral song (IR, docs/architecture.md#layers): what any output needs, with no trace of the input
/// format. Readers build it (MOD: <c>ModToSong</c>, plan M3); mappers and writers read only this.
/// <para>Numbering is 0-based throughout (orders, patterns, rows, channels, instruments, samples). Reports that show
/// a tracker's own numbers convert at the edge.</para>
/// </summary>
internal sealed class Song
{
    /// <summary>Creates a song and checks that every reference points inside it.</summary>
    /// <exception cref="ArgumentException">An order, loop point, row, instrument or sample reference is out of range.</exception>
    public Song(
        string title,
        IReadOnlyList<Channel> channels,
        IReadOnlyList<int> orders,
        int? loopOrder,
        IReadOnlyList<Pattern> patterns,
        IReadOnlyList<Instrument> instruments,
        IReadOnlyList<Sample> samples,
        Timing timing)
    {
        Title = title;
        Channels = channels;
        Orders = orders;
        LoopOrder = loopOrder;
        Patterns = patterns;
        Instruments = instruments;
        Samples = samples;
        Timing = timing;
        Validate();
    }

    /// <summary>Song title as stored by the source (may be empty).</summary>
    public string Title { get; }

    /// <summary>The song's channels, in order; every row has one cell per channel.</summary>
    public IReadOnlyList<Channel> Channels { get; }

    /// <summary>Pattern indices in playing order.</summary>
    public IReadOnlyList<int> Orders { get; }

    /// <summary>
    /// The order the song continues from after its end, or <c>null</c> when it simply ends. Set by the reader or the
    /// order traversal (plan T3.4: restart position, a <see cref="PositionJump"/> backwards).
    /// </summary>
    public int? LoopOrder { get; }

    /// <summary>All patterns, including ones no order plays.</summary>
    public IReadOnlyList<Pattern> Patterns { get; }

    /// <summary>All instrument slots, including empty ones (their names may carry credits).</summary>
    public IReadOnlyList<Instrument> Instruments { get; }

    /// <summary>All sample slots, including empty ones.</summary>
    public IReadOnlyList<Sample> Samples { get; }

    /// <summary>Speed and tempo when the song starts.</summary>
    public Timing Timing { get; }

    private void Validate()
    {
        for (int i = 0; i < Orders.Count; i++)
        {
            Require(Orders[i] >= 0 && Orders[i] < Patterns.Count, $"Order {i} plays pattern {Orders[i]}, but the song has {Patterns.Count} patterns.");
        }

        if (LoopOrder is { } loop)
        {
            Require(loop >= 0 && loop < Orders.Count, $"Loop order {loop} is outside the {Orders.Count} orders.");
        }

        for (int p = 0; p < Patterns.Count; p++)
        {
            for (int r = 0; r < Patterns[p].Rows.Count; r++)
            {
                Row row = Patterns[p].Rows[r];
                Require(row.Cells.Count == Channels.Count, $"Pattern {p} row {r} has {row.Cells.Count} cells for {Channels.Count} channels.");
                foreach (Cell cell in row.Cells)
                {
                    Require(cell.Instrument is null || (cell.Instrument >= 0 && cell.Instrument < Instruments.Count), $"Pattern {p} row {r} uses instrument {cell.Instrument}, but the song has {Instruments.Count}.");
                }
            }
        }

        for (int i = 0; i < Instruments.Count; i++)
        {
            int sample = Instruments[i].Sample;
            Require(sample >= 0 && sample < Samples.Count, $"Instrument {i} plays sample {sample}, but the song has {Samples.Count}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}

/// <summary>One song channel.</summary>
/// <param name="Pan">Starting pan: −1 hard left, 0 centre, +1 hard right (Amiga MODs: −1, +1, +1, −1).</param>
internal sealed record Channel(double Pan);

/// <summary>Speed and tempo when the song starts; changes during the song are <see cref="SetSpeed"/> and <see cref="SetTempo"/> effects.</summary>
/// <param name="Speed">Ticks per row.</param>
/// <param name="Bpm">Tracker tempo; the tick rate is <see cref="TickRateHz"/>.</param>
internal sealed record Timing(int Speed, int Bpm)
{
    /// <summary>Ticks per second: BPM × 2 / 5 (50 Hz at 125 BPM), the rule MOD, S3M, XM and IT share.</summary>
    public double TickRateHz => Bpm * 2.0 / 5.0;
}
