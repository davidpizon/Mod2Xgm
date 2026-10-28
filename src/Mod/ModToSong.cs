using System.Diagnostics;
using System.Globalization;
using Mod2Xgm.Model;

namespace Mod2Xgm.Mod;

/// <summary>
/// Builds the format-neutral <see cref="Song"/> from a <see cref="ModModule"/> (plan T3.3, docs/architecture.md#layers):
/// notes as pitches (<see cref="AmigaPitch"/>), instruments, samples, and every cell's effect decoded by
/// <see cref="ModEffects"/>. Order traversal, the loop point and timing changes are the tick player's job (plan T3.4),
/// so the song starts at speed 6 / 125 BPM and <see cref="Song.LoopOrder"/> stays <c>null</c> here.
/// <para>Everything decoding changes or ignores becomes a conversion note: one per kind, with the count and the first
/// place it happens.</para>
/// </summary>
internal static class ModToSong
{
    /// <summary>Ticks per row when a MOD starts (docs/formats/mod.md#timing).</summary>
    private const int DefaultSpeed = 6;

    /// <summary>Tempo when a MOD starts (docs/formats/mod.md#timing).</summary>
    private const int DefaultBpm = 125;

    /// <summary>Amiga hardware stereo: channels 1 and 4 hard left, 2 and 3 hard right (docs/formats/mod.md#effects-protracker).</summary>
    private static readonly double[] AmigaPans = [-1, 1, 1, -1];

    /// <summary>Converts <paramref name="module"/>.</summary>
    /// <param name="module">The MOD as read.</param>
    /// <param name="amigaClockHz">Paula clock that turns periods into pitch (<c>--input-region</c>).</param>
    /// <param name="player">Whose rules decide what effects mean.</param>
    /// <param name="notes">Collects conversion notes.</param>
    public static Song Convert(ModModule module, double amigaClockHz, ModPlayer player, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(notes);

        Sample[] samples = [.. module.Samples.Select(ToSample)];
        Instrument[] instruments = [.. module.Samples.Select(ToInstrument)];
        Channel[] channels = [.. Enumerable.Range(0, module.ChannelCount).Select(c => new Channel(AmigaPans[c % AmigaPans.Length]))];

        Tally tally = new();
        Pattern[] patterns = new Pattern[module.Patterns.Count];
        for (int p = 0; p < patterns.Length; p++)
        {
            Row[] rows = new Row[ModPattern.Rows];
            for (int row = 0; row < rows.Length; row++)
            {
                Cell[] cells = new Cell[module.ChannelCount];
                for (int channel = 0; channel < cells.Length; channel++)
                {
                    cells[channel] = ToCell(module, module.Patterns[p][row, channel], amigaClockHz, player, tally, Location(p, row, channel));
                }

                rows[row] = new Row(cells);
            }

            patterns[p] = new Pattern(rows);
        }

        AddNotes(tally, module, notes);
        return new Song(module.Title, channels, [.. module.Orders], null, patterns, instruments, samples, new Timing(DefaultSpeed, DefaultBpm));
    }

    private static Cell ToCell(ModModule module, ModCell cell, double amigaClockHz, ModPlayer player, Tally tally, string location)
    {
        NotePitch? note = cell.Period == 0 ? null : AmigaPitch.FromPeriod(cell.Period, amigaClockHz);

        int? instrument = null;
        if (cell.Sample > module.Samples.Count)
        {
            tally.Count(Tally.SampleAboveCount, location);
        }
        else if (cell.Sample > 0)
        {
            instrument = cell.Sample - 1;
        }

        DecodedEffect decoded = ModEffects.Decode(cell.Effect, cell.EffectParameter, player);
        if (decoded.Issue is { } issue)
        {
            tally.Count(issue.ToString(), location);
        }

        if (module.Format == ModFormat.Soundtracker15 && cell.Effect is 0x1 or 0x2 && cell.EffectParameter != 0)
        {
            tally.Count(Tally.SoundtrackerSlides, location);
        }

        return note is null && instrument is null && decoded.Volume is null && decoded.Effects.Count == 0
            ? Cell.Empty
            : new Cell(note, instrument, decoded.Volume, decoded.Effects);
    }

    private static Sample ToSample(ModSample sample)
    {
        // 8-bit signed sound converts exactly to −1…+1 (docs/architecture.md#layers).
        float[] data = [.. sample.Data.Select(b => b / 128f)];
        SampleLoop? loop = sample.Loops ? new SampleLoop(sample.LoopStart, sample.LoopLength) : null;
        return new Sample(sample.Name, data, loop);
    }

    private static Instrument ToInstrument(ModSample sample, int index) =>
        new(sample.Name, index, Math.Min(sample.Volume, 64) / 64.0, sample.FineTune * AmigaPitch.CentsPerFineTune);

    private static void AddNotes(Tally tally, ModModule module, ConversionNotes notes)
    {
        foreach ((string kind, (int count, string first)) in tally.Kinds)
        {
            switch (kind)
            {
                case Tally.SampleAboveCount:
                    notes.Add("{Count} cells name a sample above {Max} (first at {Location}); read as naming no sample", count, module.Samples.Count, first);
                    break;
                case Tally.SoundtrackerSlides:
                    notes.Add(
                        "{Count} cells use effect 1 or 2 in this 15-sample file (first at {Location}); read as pitch slides, as later Soundtrackers and ProTracker do (Ultimate Soundtracker used them for arpeggio and pitch bend)",
                        count,
                        first);
                    break;
                case nameof(EffectIssue.VolumeAbove64):
                    notes.Add("{Count} cells set a volume above 64 with Cxx (first at {Location}); played at 64, as ProTracker does", count, first);
                    break;
                case nameof(EffectIssue.PanIgnored):
                    notes.Add("{Count} cells use 8xx panning (first at {Location}); ProTracker 2 ignores it, so it's kept only as an unsupported effect", count, first);
                    break;
                case nameof(EffectIssue.BreakRowNotDecimal):
                    notes.Add("{Count} pattern breaks (Dxx) have a digit above 9 (first at {Location}); read as x × 10 + y, as ProTracker does", count, first);
                    break;
                case nameof(EffectIssue.BreakRowAbove63):
                    notes.Add("{Count} pattern breaks (Dxx) name a row above 63 (first at {Location}); they break to row 0, as ProTracker does", count, first);
                    break;
                case nameof(EffectIssue.UnusedExtended):
                    notes.Add("{Count} cells use E8x, which ProTracker 2 leaves unused (first at {Location}); kept as an unsupported effect", count, first);
                    break;
                default:
                    throw new UnreachableException($"No conversion note for {kind}.");
            }
        }
    }

    private static string Location(int pattern, int row, int channel) =>
        string.Create(CultureInfo.InvariantCulture, $"pattern {pattern} row {row} channel {channel + 1}");

    /// <summary>Counts each kind of conversion note, keeping where it first happened; kinds keep the order they were first seen.</summary>
    private sealed class Tally
    {
        /// <summary>A cell's sample number is above the module's sample count.</summary>
        public const string SampleAboveCount = "SampleAboveCount";

        /// <summary>A 15-sample file uses effect 1 or 2, whose meaning depends on the Soundtracker version.</summary>
        public const string SoundtrackerSlides = "SoundtrackerSlides";

        private readonly Dictionary<string, (int Count, string First)> _kinds = [];
        private readonly List<string> _order = [];

        /// <summary>Each kind seen, with its count and first location, in first-seen order.</summary>
        public IEnumerable<(string Kind, (int Count, string First) Seen)> Kinds => _order.Select(k => (k, _kinds[k]));

        public void Count(string kind, string location)
        {
            if (_kinds.TryGetValue(kind, out (int Count, string First) seen))
            {
                _kinds[kind] = (seen.Count + 1, seen.First);
            }
            else
            {
                _kinds[kind] = (1, location);
                _order.Add(kind);
            }
        }
    }
}
