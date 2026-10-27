using System.Globalization;

namespace Mod2Xgm.Mod;

/// <summary>Whether a sample slot is played (docs/sample-capacity.md, docs/cli.md#console-report).</summary>
internal enum SampleStatus
{
    /// <summary>The slot holds sound and the song plays it at least once.</summary>
    Used,

    /// <summary>The slot holds sound, but the song never plays it.</summary>
    Unused,

    /// <summary>The slot holds no sound (length 0); its name may still carry credits.</summary>
    Empty,
}

/// <summary>How often one sample slot is played.</summary>
/// <param name="Number">Slot number, 1-based.</param>
/// <param name="Notes">Notes that start this sample during the scanned playthrough.</param>
/// <param name="Status">Used, unused or empty.</param>
internal sealed record SampleUse(int Number, int Notes, SampleStatus Status);

/// <summary>The result of <see cref="SampleUsageScanner.Scan"/>.</summary>
/// <param name="Samples">One entry per sample slot, in slot order.</param>
/// <param name="RowsPlayed">Rows walked, counting repeats.</param>
/// <param name="LoopsBack">True when the song jumps back to rows it already played (it loops forever).</param>
internal sealed record SampleUsage(IReadOnlyList<SampleUse> Samples, int RowsPlayed, bool LoopsBack);

/// <summary>
/// Counts how often each sample is played (plan T2.4) by walking the song row by row in playing order and following
/// the flow effects: position jump <c>Bxx</c>, pattern break <c>Dxx</c>, pattern loop <c>E6x</c> and stop <c>F00</c>.
/// <list type="bullet">
/// <item>A note counts when it really starts: a cell with a period plays the channel's current sample (the last
/// sample number seen on that channel). A sample number without a period only selects the sample.</item>
/// <item>Not counted: tone portamento (<c>3xx</c>, <c>5xy</c>), which slides the playing sound instead of starting
/// one; note delay <c>EDx</c> with x ≥ speed, which never plays. Retrigger <c>E9x</c> counts once.</item>
/// <item>The song is walked once through. If it jumps back to rows already played, the repeating part is walked one
/// more time, then the walk stops; <c>E6x</c> repeats always count. A song that just reaches its end is walked once.</item>
/// </list>
/// Every assumption and odd condition is a conversion note. Exact playback (timing, effects) is plan M3.
/// </summary>
internal static class SampleUsageScanner
{
    // Effect numbers (docs/formats/mod.md#effects-protracker).
    private const int TonePortamento = 0x3;
    private const int TonePortamentoVolumeSlide = 0x5;
    private const int PositionJump = 0xB;
    private const int PatternBreak = 0xD;
    private const int Extended = 0xE;
    private const int SetSpeed = 0xF;
    private const int PatternLoop = 0x6; // E6x
    private const int NoteDelay = 0xD;   // EDx

    private const int DefaultSpeed = 6;
    private const int FirstTempoValue = 0x20; // Fxx at or above this sets BPM, not speed
    private const int TimesThroughLoop = 2;   // once through, plus one more time around a jump back

    // Guard against pathological flow (e.g. pattern loops that never end); no real song comes close.
    private const int MaxRows = 1_000_000;

    /// <summary>Walks <paramref name="module"/> and counts sample plays; odd conditions are added to <paramref name="notes"/>.</summary>
    public static SampleUsage Scan(ModModule module, ConversionNotes notes)
    {
        int sampleCount = module.Samples.Count;
        int channels = module.ChannelCount;
        int[] plays = new int[sampleCount + 1];
        int[] current = new int[channels];
        int[] loopStart = new int[channels];
        int[] loopCount = new int[channels];
        Dictionary<(int Order, int Row), int> visits = [];
        HashSet<int> playedOrders = [];
        HashSet<int> playedPatterns = [];

        int speed = DefaultSpeed;
        int order = 0;
        int row = 0;
        int rows = 0;
        bool loopsBack = false;
        Problems problems = new();

        while (true)
        {
            bool repeating = Array.Exists(loopCount, c => c > 0);
            if (!repeating)
            {
                int seen = visits.GetValueOrDefault((order, row));
                if (seen >= TimesThroughLoop)
                {
                    break;
                }

                if (seen == 1 && !loopsBack)
                {
                    loopsBack = true;
                    notes.Add(
                        "The song jumps back to order {Order} row {Row} and repeats from there; the usage counts include one more time around that loop",
                        order,
                        row);
                }

                visits[(order, row)] = seen + 1;
            }

            if (++rows > MaxRows)
            {
                notes.Add("The song's flow didn't end after {Rows} rows; the usage counts stop there", MaxRows);
                break;
            }

            int pattern = module.OrderTable[order];
            playedOrders.Add(order);
            playedPatterns.Add(pattern);
            ModPattern cells = module.Patterns[pattern];

            int? jumpOrder = null;
            int? breakRow = null;
            int? loopBackRow = null;
            bool stop = false;

            // Fxx takes effect on the row it's on, so read the row's speed before deciding about delayed notes.
            for (int channel = 0; channel < channels; channel++)
            {
                ModCell cell = cells[row, channel];
                if (cell.Effect == SetSpeed && cell.EffectParameter is > 0 and < FirstTempoValue)
                {
                    speed = cell.EffectParameter;
                }
            }

            for (int channel = 0; channel < channels; channel++)
            {
                ModCell cell = cells[row, channel];
                if (cell.Sample is > 0 && cell.Sample <= sampleCount)
                {
                    current[channel] = cell.Sample;
                }

                CountNote(cell, channel, current[channel], speed, plays, problems, order, row);

                switch (cell.Effect)
                {
                    case PositionJump:
                        jumpOrder = cell.EffectParameter;
                        break;
                    case PatternBreak:
                        // The parameter is two decimal digits; a row past the end means row 0 (ProTracker).
                        int target = ((cell.EffectParameter >> 4) * 10) + (cell.EffectParameter & 0x0F);
                        breakRow = target < ModPattern.Rows ? target : 0;
                        break;
                    case SetSpeed when cell.EffectParameter == 0:
                        stop = true;
                        break;
                    case Extended when cell.EffectParameter >> 4 == PatternLoop:
                        loopBackRow = PatternLoopTarget(cell.EffectParameter & 0x0F, channel, row, loopStart, loopCount) ?? loopBackRow;
                        break;
                }
            }

            if (stop)
            {
                notes.Add("Order {Order} row {Row}: effect F00 stops the song; nothing after it is counted", order, row);
                break;
            }

            if (loopBackRow is int loopRow)
            {
                row = loopRow;
                continue;
            }

            if (jumpOrder is not null || breakRow is not null)
            {
                int next = jumpOrder ?? (order + 1);
                if (next >= module.SongLength)
                {
                    if (jumpOrder is not null)
                    {
                        notes.Add(
                            "Order {Order} row {Row}: jump to order {Target}, past the song's {Length} orders; treated as the end of the song",
                            order,
                            row,
                            next,
                            module.SongLength);
                    }

                    break;
                }

                (order, row) = (next, breakRow ?? 0);
                ResetPatternLoops(loopStart, loopCount);
                continue;
            }

            if (++row == ModPattern.Rows)
            {
                (order, row) = (order + 1, 0);
                ResetPatternLoops(loopStart, loopCount);
                if (order >= module.SongLength)
                {
                    break;
                }
            }
        }

        problems.Report(notes);
        NoteUnplayed(module, playedOrders, playedPatterns, notes);
        return new SampleUsage(Statuses(module, plays, notes), rows, loopsBack);
    }

    /// <summary>Counts the note in <paramref name="cell"/>, if it really starts a sample.</summary>
    private static void CountNote(ModCell cell, int channel, int sample, int speed, int[] plays, Problems problems, int order, int row)
    {
        if (cell.Period == 0 || cell.Effect is TonePortamento or TonePortamentoVolumeSlide)
        {
            return;
        }

        if (cell.Effect == Extended && cell.EffectParameter >> 4 == NoteDelay && (cell.EffectParameter & 0x0F) >= speed)
        {
            problems.MissedDelay(order, row, channel);
            return;
        }

        if (sample == 0)
        {
            problems.NoSample(order, row, channel);
            return;
        }

        plays[sample]++;
    }

    /// <summary>
    /// Applies <c>E6x</c> on one channel: <c>E60</c> marks the loop start; <c>E6x</c> repeats back to it x times.
    /// Returns the row to go back to, or null to carry on.
    /// </summary>
    private static int? PatternLoopTarget(int times, int channel, int row, int[] loopStart, int[] loopCount)
    {
        if (times == 0)
        {
            loopStart[channel] = row;
            return null;
        }

        if (loopCount[channel] == 0)
        {
            loopCount[channel] = times;
            return loopStart[channel];
        }

        return --loopCount[channel] > 0 ? loopStart[channel] : null;
    }

    /// <summary>Pattern-loop starts and counts belong to one pattern; a new order starts fresh.</summary>
    private static void ResetPatternLoops(int[] loopStart, int[] loopCount)
    {
        Array.Clear(loopStart);
        Array.Clear(loopCount);
    }

    /// <summary>Notes orders the walk never reached and stored patterns that are never played.</summary>
    private static void NoteUnplayed(ModModule module, HashSet<int> playedOrders, HashSet<int> playedPatterns, ConversionNotes notes)
    {
        int[] skippedOrders = [.. Enumerable.Range(0, module.SongLength).Where(o => !playedOrders.Contains(o))];
        if (skippedOrders.Length > 0)
        {
            notes.Add("Orders {Orders} are never reached (jumps skip them); their notes aren't counted", List(skippedOrders));
        }

        int[] unplayedPatterns = [.. Enumerable.Range(0, module.Patterns.Count).Where(p => !playedPatterns.Contains(p))];
        if (unplayedPatterns.Length > 0)
        {
            notes.Add("Patterns {Patterns} are stored in the file but never played", List(unplayedPatterns));
        }
    }

    /// <summary>Turns play counts into statuses, noting empty samples that are played and sounds that never are.</summary>
    private static List<SampleUse> Statuses(ModModule module, int[] plays, ConversionNotes notes)
    {
        List<SampleUse> uses = [];
        foreach (ModSample sample in module.Samples)
        {
            int count = plays[sample.Number];
            SampleStatus status = sample.IsEmpty ? SampleStatus.Empty : count > 0 ? SampleStatus.Used : SampleStatus.Unused;
            if (status == SampleStatus.Empty && count > 0)
            {
                notes.Add("Sample {Sample} {Name}: played {Count} times but holds no sound; those notes are silent", sample.Number, sample.Name, count);
            }
            else if (status == SampleStatus.Unused)
            {
                notes.Add("Sample {Sample} {Name}: holds {Length} bytes but is never played", sample.Number, sample.Name, sample.Length);
            }

            uses.Add(new SampleUse(sample.Number, count, status));
        }

        return uses;
    }

    /// <summary>"1, 4, 7" in invariant culture.</summary>
    private static string List(IEnumerable<int> numbers) => string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Counts notes that can't be heard, so each kind is reported once with its first location.</summary>
    private sealed class Problems
    {
        private int _missedDelays;
        private int _withoutSample;
        private string? _firstMissedDelay;
        private string? _firstWithoutSample;

        /// <summary>A note delayed by <c>EDx</c> past the end of its row.</summary>
        public void MissedDelay(int order, int row, int channel)
        {
            _missedDelays++;
            _firstMissedDelay ??= Location(order, row, channel);
        }

        /// <summary>A note on a channel that hasn't been given a sample yet.</summary>
        public void NoSample(int order, int row, int channel)
        {
            _withoutSample++;
            _firstWithoutSample ??= Location(order, row, channel);
        }

        /// <summary>Adds one conversion note per kind of problem found.</summary>
        public void Report(ConversionNotes notes)
        {
            if (_missedDelays > 0)
            {
                notes.Add("{Count} notes are delayed (EDx) past the end of their row and never play (first at {Location})", _missedDelays, _firstMissedDelay);
            }

            if (_withoutSample > 0)
            {
                notes.Add("{Count} notes start before their channel has a sample and are silent (first at {Location})", _withoutSample, _firstWithoutSample);
            }
        }

        private static string Location(int order, int row, int channel) =>
            string.Create(CultureInfo.InvariantCulture, $"order {order} row {row} channel {channel + 1}");
    }
}
