using System.Globalization;
using Mod2Xgm.Audio;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Dmf;

/// <summary>What the DMF song builder needs besides the plan.</summary>
/// <param name="AmigaClockHz">The source Amiga's Paula clock (<c>--input-region</c>): turns playback rates back into periods.</param>
/// <param name="Resampler">How DAC samples are resampled (<c>--resampler</c>).</param>
internal sealed record DmfBuildSettings(double AmigaClockHz, ResamplingMode Resampler);

/// <summary>
/// Builds the whole DMF song from a <see cref="GenesisPlan"/> (plan T5.5, docs/adr/0044-dmf-effects-and-song-grid.md):
/// the FM and noise instruments, one DMF pattern per order on every channel (the parts as the MOD has them), the cells
/// and their effects (<see cref="DmfPatternTranslator"/>), and the DAC samples: one copy per start point (<c>9xx</c>)
/// and per 3 dB of loudness the song plays each at, because neither DefleMask nor SGDK can change a sample's volume.
/// <see cref="DmfWriter"/> writes the result.
/// </summary>
internal static class DmfSongBuilder
{
    /// <summary>Most matrix rows (orders) a DMF holds (docs/formats/dmf.md#4-module-information).</summary>
    public const int MaxMatrixRows = 127;

    /// <summary>The file version the writer makes.</summary>
    private const int FileVersion = 24;

    /// <summary>Row highlights: a beat of 4 rows and a bar of 16, as ProTracker shows them.</summary>
    private const int HighlightA = 4;

    private const int HighlightB = 16;

    /// <summary>DAC sample rates by rate code (docs/formats/dmf.md#9-pcm-samples).</summary>
    private static readonly int[] RatesHz = [4000, 8000, 11025, 16000, 22050, 32000];

    /// <summary>Sample pitch code for "played as stored", and amp for "unchanged" (docs/formats/dmf.md#9-pcm-samples).</summary>
    private const int SamplePitchAsStored = 5;

    private const int SampleAmpUnchanged = 50;

    private const int SampleBits = 16;

    /// <summary>A looped DAC sample is played out at most this long; a longer note is cut.</summary>
    private const double MaxLoopSeconds = 10;

    /// <summary>A looped DAC sample is played out at least this long, so a note whose end can't be known still sounds.</summary>
    private const double MinLoopSeconds = 0.5;

    /// <summary>Most values a macro holds (docs/formats/dmf.md#6-instruments).</summary>
    private const int MaxMacroLength = 127;

    /// <summary>One PSG volume step, in decibels: a noise instrument's volume macro follows its sample's loudness in these.</summary>
    private const double PsgStepDb = 2;

    /// <summary>Builds the DMF song; every lossy choice becomes a note in <paramref name="notes"/>.</summary>
    /// <param name="plan">The mapped song.</param>
    /// <param name="song">The song the plan was made from (samples, instruments, channel pans).</param>
    /// <param name="timing">Tick rate and tick-time changes (<see cref="DmfTimingPlanner"/>).</param>
    /// <param name="settings">Clock and resampler.</param>
    /// <param name="notes">Collects conversion notes.</param>
    public static DmfFile Build(GenesisPlan plan, Song song, DmfTimingPlan timing, DmfBuildSettings settings, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.AmigaClockHz);

        int orders = Math.Min(plan.Patterns.Count, MaxMatrixRows);
        if (plan.Patterns.Count > MaxMatrixRows)
        {
            notes.Add("The song has {Orders} orders; DMF holds {Max}, so the rest are left out", plan.Patterns.Count, MaxMatrixRows);
        }

        List<DmfInstrument> instruments = [.. plan.FmPatches.Select(Instrument)];
        Dictionary<int, int> noiseInstruments = [];
        foreach (PlanCell cell in plan.Patterns.Take(orders).SelectMany(p => p).Select(r => r.Cells[(int)GenesisChannel.Psg4]))
        {
            if (cell is { Voice.Kind: VoiceKind.PsgNoise, Sample: { } sample, Note: { } note } && !noiseInstruments.ContainsKey(sample))
            {
                noiseInstruments[sample] = instruments.Count;
                instruments.Add(NoiseInstrument(song.Samples[sample], note.PlaybackRateHz, timing.Timing.TickRateHz));
            }
        }

        if (orders == 0)
        {
            throw new ArgumentException("The plan has no orders to write.", nameof(plan));
        }

        DmfPatternTranslator translator = new(plan, song, timing, settings.AmigaClockHz, orders, noiseInstruments);
        DmfTranslation translation = translator.Translate();
        Report(translation, song, notes);

        DmfChannel[] channels = new DmfChannel[GenesisPlan.ChannelCount];
        for (int c = 0; c < channels.Length; c++)
        {
            IReadOnlyList<IReadOnlyList<DmfCell>> patterns = translation.Cells[c];
            int columns = Math.Max(1, patterns.SelectMany(p => p).Select(cell => cell.Effects.Count).DefaultIfEmpty(0).Max());
            channels[c] = new DmfChannel([.. Enumerable.Range(0, orders)], null, columns, patterns);
        }

        List<DmfSample> samples = [.. translation.DacVariants.Select((variant, i) => Sample(variant, translation.DacSeconds[i], plan, song, settings, notes))];
        return new DmfFile(
            FileVersion,
            DmfSystem.Genesis,
            plan.Title,
            string.Empty,
            HighlightA,
            HighlightB,
            timing.Timing,
            translator.RowsPerPattern,
            channels,
            instruments,
            [],
            samples,
            0);
    }

    /// <summary>The rate code a sample playing at <paramref name="rateHz"/> is stored at: the lowest rate that loses nothing, at most 32 kHz.</summary>
    public static int RateCodeFor(double rateHz)
    {
        for (int code = 0; code < RatesHz.Length; code++)
        {
            if (RatesHz[code] >= rateHz)
            {
                return code;
            }
        }

        return RatesHz.Length - 1;
    }

    /// <summary>An FM patch as a DMF instrument: operators in file order (OP1, OP3, OP2, OP4), detune stored with 3 = none.</summary>
    internal static DmfInstrument Instrument(FmPatch patch)
    {
        int[] fileOrder = [0, 2, 1, 3];
        DmfOperator[] operators = [.. fileOrder.Select(i => Operator(patch.Operators[i]))];
        return new DmfInstrument(patch.Name, new DmfFmInstrument(patch.Algorithm, patch.Feedback, 0, 0, operators), null);
    }

    private static DmfOperator Operator(FmOperator op) =>
        new(op.Am ? 1 : 0, op.Ar, op.Dr, op.Mult, op.Rr, op.Sl, op.Tl, 0, op.Rs, op.Detune + 3, op.D2R, 0);

    private static DmfMacro Empty() => new([], -1);

    /// <summary>
    /// A PSG noise instrument whose volume macro follows <paramref name="sample"/>'s loudness tick by tick, so a hit fades
    /// like the recording; the PSG noise itself never fades. DefleMask adds the macro to the channel volume in 2 dB steps
    /// (Furnace: VOL_SCALE_LOG for SN76489), so the loudest tick is F and a tick 6 dB quieter is C. A sample that loops
    /// holds its loop's level; one that doesn't ends at 0.
    /// </summary>
    /// <param name="sample">The noise sample.</param>
    /// <param name="playbackRateHz">The rate its first note plays it at.</param>
    /// <param name="tickRateHz">The DMF's tick rate.</param>
    internal static DmfInstrument NoiseInstrument(Sample sample, double playbackRateHz, int tickRateHz)
    {
        double framesPerTick = playbackRateHz / tickRateHz;
        int end = sample.Loop is { } loop ? loop.Start + loop.Length : sample.Length;
        List<double> levels = [];
        for (int tick = 0; tick < MaxMacroLength && (int)(tick * framesPerTick) < end; tick++)
        {
            int from = (int)(tick * framesPerTick);
            int to = Math.Min(end, Math.Max(from + 1, (int)((tick + 1) * framesPerTick)));
            double sum = 0;
            for (int i = from; i < to; i++)
            {
                sum += (double)sample.Data[i] * sample.Data[i];
            }

            levels.Add(Math.Sqrt(sum / (to - from)));
        }

        double loudest = levels.DefaultIfEmpty(0).Max();
        List<int> values = [.. levels.Select(level => loudest <= 0 || level <= 0 ? 0 : Math.Clamp((int)Math.Round(DmfUnits.MaxPsgVolume + (20 * Math.Log10(level / loudest) / PsgStepDb), MidpointRounding.AwayFromZero), 0, DmfUnits.MaxPsgVolume))];
        if (sample.Loop is null && values.Count < MaxMacroLength)
        {
            values.Add(0);
        }

        if (values.Count == 0)
        {
            values.Add(DmfUnits.MaxPsgVolume);
        }

        return new DmfInstrument(sample.Name, null, new DmfStdInstrument(new DmfMacro(values, -1), Empty(), 0, Empty(), Empty()));
    }

    /// <summary>One DAC sample copy: played out, cut at its start point, resampled to its rate code, made quieter.</summary>
    private static DmfSample Sample(DacVariant variant, double seconds, GenesisPlan plan, Song song, DmfBuildSettings settings, ConversionNotes notes)
    {
        DacSample dac = plan.DacSamples[variant.Dac];
        Sample sample = SampleProcessor.CutAfterLoop(song.Samples[dac.Sample]);
        float[] data = sample.Data;
        if (sample.Loop is not null)
        {
            double playSeconds = Math.Clamp(seconds, MinLoopSeconds, MaxLoopSeconds);
            if (seconds > MaxLoopSeconds)
            {
                notes.Add("Sample {Sample}: a DAC note holds its loop for {Seconds:0.#} s; the copy plays it for {Max} s", Label(dac.Sample, sample.Name), seconds, MaxLoopSeconds);
            }

            int frames = (int)Math.Ceiling(playSeconds * dac.PlaybackRateHz) + variant.Offset + 1;
            data = SampleProcessor.Unroll(sample, Math.Max(frames, sample.Length));
        }

        data = data[Math.Min(variant.Offset, data.Length)..];
        int code = RateCodeFor(dac.PlaybackRateHz);
        data = SampleProcessor.Resample(data, dac.PlaybackRateHz, RatesHz[code], settings.Resampler);
        if (variant.VolumeSteps > 0)
        {
            data = SampleProcessor.ApplyGain(data, Math.Pow(10, -variant.VolumeSteps * DmfPatternTranslator.DacVolumeStepDb / 20));
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"{sample.Name} {RatesHz[code]}Hz");
        if (variant.Offset > 0)
        {
            name += string.Create(CultureInfo.InvariantCulture, $" +{variant.Offset}");
        }

        if (variant.VolumeSteps > 0)
        {
            name += string.Create(CultureInfo.InvariantCulture, $" -{variant.VolumeSteps * DmfPatternTranslator.DacVolumeStepDb:0}dB");
        }

        short[] pcm = SampleProcessor.ToSigned16(data, sample.Name, notes);
        return new DmfSample(name, code, SamplePitchAsStored, SampleAmpUnchanged, SampleBits, null, null, pcm);
    }

    /// <summary>Says what the translation measured, assumed and left out.</summary>
    private static void Report(DmfTranslation translation, Song song, ConversionNotes notes)
    {
        foreach ((int sample, bool measured) in translation.FmSamples)
        {
            string label = Label(sample, song.Samples[sample].Name);
            if (measured)
            {
                notes.Add("Sample {Sample}: its FM notes are placed at the pitch measured from the sample", label);
            }
            else
            {
                notes.Add("Sample {Sample}: no clear pitch found, so its FM notes are placed two octaves above the MOD note's name", label);
            }
        }

        int copies = translation.DacVariants.Count;
        int sources = translation.DacVariants.Select(v => v.Dac).Distinct().Count();
        if (copies > sources)
        {
            notes.Add(
                "{Copies} DAC sample copies were made from {Samples} samples: one per start point (9xx) and per 3 dB of loudness, because the DAC has no volume",
                copies,
                sources);
        }

        foreach ((string template, int count) in translation.Tallies.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            notes.Add(template, count);
        }
    }

    /// <summary>"3 "bass"": the 1-based number trackers show, and the name.</summary>
    private static string Label(int sample, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{sample + 1} \"{name}\"");
}
