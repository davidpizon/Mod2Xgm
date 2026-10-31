using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Render;

/// <summary>How <see cref="GenesisRenderer"/> renders.</summary>
/// <param name="Driver">The PCM driver that plays the DAC notes: DefleMask's (DMF), XGM's or XGM2's.</param>
/// <param name="Region">The Genesis that plays (<c>--output-region</c>): its YM3438 and PSG clocks.</param>
/// <param name="SampleRate">Output samples per second, or <c>null</c> for the YM3438's own rate rounded to the hertz (no resampling).</param>
/// <param name="Resampler">How DAC samples are brought to the driver's rate (<c>--resampler</c>); the output goes through <see cref="ChipRateResampler"/>.</param>
internal sealed record GenesisRenderOptions(
    PcmDriver Driver = PcmDriver.Dmf,
    Region Region = Region.Pal,
    int? SampleRate = 44_100,
    ResamplingMode Resampler = ResamplingMode.Smooth)
{
    /// <summary>The defaults: the DMF's DAC on a PAL Genesis, 44.1 kHz.</summary>
    public static GenesisRenderOptions Default { get; } = new();

    /// <summary>
    /// The length of each tick in seconds, in place of the playback's own (one per tick), or <c>null</c> to keep them;
    /// as <see cref="ModRenderOptions.TickDurations"/>.
    /// </summary>
    public IReadOnlyList<double>? TickDurations { get; init; }
}

/// <summary>
/// Plays a <see cref="GenesisPlan"/> on a model of the Genesis's sound hardware (plan T7.5,
/// docs/adr/0059-genesis-renderer.md): the plan says which Genesis channel plays each note with which voice, and the
/// tick player's <see cref="Playback"/> gives each note's pitch, volume and pan on every tick. Those become register
/// writes into the YM3438 (T7.2) and the SN76489 (T7.3), and plays and stops for the PCM driver model (T7.4), at the
/// first YM3438 sample of each tick. The voices are measured as the DMF translator measures them (<see cref="PlanVoicing"/>).
/// </summary>
internal static class GenesisRenderer
{
    /// <summary>Renders <paramref name="playback"/> once through with the voices of <paramref name="plan"/>.</summary>
    /// <param name="plan">The mapped song: which Genesis channel plays each note, and with which voice.</param>
    /// <param name="song">The song the plan was made from: its samples.</param>
    /// <param name="playback">The same song played by the tick player: every tick's pitch, volume and pan.</param>
    /// <param name="options">How to render; <c>null</c> for <see cref="GenesisRenderOptions.Default"/>.</param>
    /// <param name="notes">Collects what the render can't play as the plan says.</param>
    /// <exception cref="ArgumentException">The options don't fit the playback, or the playback isn't of the plan's song.</exception>
    public static StereoAudio Render(GenesisPlan plan, Song song, Playback playback, GenesisRenderOptions? options, ConversionNotes notes)
    {
        GenesisRenderOptions settings = options ?? GenesisRenderOptions.Default;
        (float[] left, float[] right, double rate) = RenderAtChipRate(plan, song, playback, settings, notes);
        if (settings.SampleRate is not { } sampleRate)
        {
            return new StereoAudio(left, right, (int)Math.Round(rate));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate, nameof(options));
        ChipRateResampler resampler = new(rate, sampleRate);
        return new StereoAudio(resampler.Resample(left), resampler.Resample(right), sampleRate);
    }

    /// <summary>The render at the YM3438's own rate (returned as <c>Rate</c>), before resampling.</summary>
    internal static (float[] Left, float[] Right, double Rate) RenderAtChipRate(GenesisPlan plan, Song song, Playback playback, GenesisRenderOptions options, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(notes);
        IReadOnlyList<PlaybackTick> ticks = playback.Ticks;
        IReadOnlyList<double> durations = options.TickDurations ?? [.. ticks.Select(t => t.DurationSeconds)];
        if (durations.Count != ticks.Count)
        {
            throw new ArgumentException($"{durations.Count} tick lengths for {ticks.Count} ticks.", nameof(options));
        }

        GenesisChips chips = new(options.Region, options.Driver);
        int[] starts = TickStarts(durations, chips.SampleRateHz);
        float[] left = new float[starts[^1]];
        float[] right = new float[starts[^1]];
        GenesisPlanPlayer player = new(plan, song, playback, durations, chips, options.Resampler, notes);
        for (int t = 0; t < ticks.Count; t++)
        {
            int at = starts[t];
            int end = starts[t + 1];

            // A note that starts again needs its key off for one sample first, or the chip doesn't see the key go down.
            if (player.BeginTick(t) && at < end)
            {
                chips.Render(left.AsSpan(at, 1), right.AsSpan(at, 1));
                at++;
            }

            player.FinishTick(t);
            chips.Render(left.AsSpan(at, end - at), right.AsSpan(at, end - at));
        }

        player.Report();
        return (left, right, chips.SampleRateHz);
    }

    /// <summary>The YM3438 sample each tick starts on, and the length of the render last.</summary>
    private static int[] TickStarts(IReadOnlyList<double> durations, double sampleRate)
    {
        int[] starts = new int[durations.Count + 1];
        double seconds = 0;
        for (int i = 0; i < durations.Count; i++)
        {
            if (durations[i] < 0 || !double.IsFinite(durations[i]))
            {
                throw new ArgumentException($"Tick {i} lasts {durations[i]} s.", nameof(durations));
            }

            seconds += durations[i];
            starts[i + 1] = checked((int)Math.Round(seconds * sampleRate));
        }

        return starts;
    }
}
