namespace Mod2Xgm.Model.Window;

/// <summary>
/// Fades a window to silence over its last seconds (<c>--fade-out</c>, docs/adr/0116-song-window.md), evenly in dB so the
/// loudness drops at a steady rate: <see cref="FadeDb"/> down by the end, and silent on the last tick. Every tick's channel
/// volumes are scaled, which the XGM2 and the WAV follow tick by tick; and every row in the fade sets each playing
/// channel's volume to the faded one, which is how a DMF, reading only the cells, fades too (one step per row).
/// </summary>
internal static class FadeOut
{
    /// <summary>How far the fade goes down before the last tick silences it, in dB.</summary>
    private const double FadeDb = 48;

    /// <summary>Volume steps a cell's volume is rounded to, as a MOD's <c>Cxx</c> has.</summary>
    private const double VolumeSteps = 64;

    /// <summary>Fades the last <paramref name="seconds"/> of <paramref name="windowed"/>; <paramref name="rowTicks"/> is the tick each row starts on.</summary>
    public static WindowedSong Apply(WindowedSong windowed, IReadOnlyList<int> rowTicks, double seconds, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(windowed);
        ArgumentNullException.ThrowIfNull(rowTicks);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);

        IReadOnlyList<PlaybackTick> source = windowed.Playback.Ticks;
        double fadeStart = windowed.Playback.DurationSeconds - seconds;
        PlaybackTick[] ticks = [.. source];
        for (int t = 0; t < ticks.Length; t++)
        {
            double gain = t == ticks.Length - 1 ? 0 : Gain(ticks[t].TimeSeconds + (ticks[t].DurationSeconds / 2), fadeStart, seconds);
            if (gain < 1)
            {
                ticks[t] = ticks[t] with { Channels = [.. ticks[t].Channels.Select(c => c with { Volume = c.Volume * gain })] };
            }
        }

        Song song = windowed.Song;
        List<Pattern> patterns = [.. song.Patterns];
        int started = 0;
        int tremolo = 0;
        foreach (int t in rowTicks)
        {
            if (ticks[t].TimeSeconds + (ticks[t].DurationSeconds / 2) < fadeStart)
            {
                continue;
            }

            PlaybackTick tick = ticks[t];
            List<Row> rows = [.. patterns[tick.Order].Rows];
            Row row = rows[tick.Row];
            Cell[] cells = [.. row.Cells];
            for (int c = 0; c < cells.Length; c++)
            {
                started += cells[c].Note is null ? 0 : 1;
                tremolo += cells[c].Effects.OfType<Tremolo>().Any() ? 1 : 0;

                // A fine slide on the row's first tick is already in the tick's volume; setting it as well would count it twice.
                bool fineSlide = cells[c].Effects.OfType<VolumeSlide>().Any(s => s.Timing == SlideTiming.OnceAtRowStart);
                if (tick.Channels[c].Playing && !fineSlide)
                {
                    cells[c] = cells[c] with { Volume = Math.Round(tick.Channels[c].Volume * VolumeSteps) / VolumeSteps };
                }
            }

            rows[tick.Row] = new Row(cells);
            patterns[tick.Order] = new Pattern(rows);
        }

        notes.Add("--fade-out: a DMF steps the fade once per row; the XGM2 follows it on every tick");
        notes.Add("--fade-out: a recording already playing on the DAC keeps the volume it started at, so it doesn't fade");
        if (started > 0)
        {
            notes.Add("--fade-out: {Count} notes start during the fade; any played on the DAC need a quieter copy of their recording", started);
        }

        if (tremolo > 0)
        {
            notes.Add("--fade-out: {Count} tremolos in the fade wobble around each row's faded volume", tremolo);
        }

        Song faded = new(song.Title, song.Channels, song.Orders, song.LoopOrder, patterns, song.Instruments, song.Samples, song.Timing);
        Playback playback = windowed.Playback with { Ticks = ticks };
        return new WindowedSong(faded, playback, windowed.Summary with { FadeSeconds = seconds });
    }

    /// <summary>The fade's gain at <paramref name="time"/>: 1 before it starts, then <see cref="FadeDb"/> down by its end, evenly in dB.</summary>
    internal static double Gain(double time, double fadeStart, double seconds)
    {
        if (time < fadeStart)
        {
            return 1;
        }

        double progress = Math.Min((time - fadeStart) / seconds, 1);
        return Math.Pow(10, -FadeDb * progress / 20);
    }
}
