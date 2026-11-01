using Mod2Xgm.Audio;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Analysis;

/// <summary>
/// Measures every sample a song plays (plan T8.1, docs/adr/0068-sample-analysis.md): how percussive, tonal and noisy it
/// is, how steady its pitch is, its loudness over time and its loop, and its spectrum (mel bands, and harmonics when it
/// has a pitch) in each pitch region the song plays it in. Pure measurement: nothing here changes a conversion.
/// </summary>
internal static class SampleAnalyzer
{
    /// <summary>The quietest level reported, in dB; the same floor as the scorer's (<see cref="MelAnalyzer.FloorDb"/>).</summary>
    public const double FloorDb = MelAnalyzer.FloorDb;

    /// <summary>The rate a region's sound is resampled to before its spectrum is taken: the harness's and the renderers' rate.</summary>
    public const int AnalysisRateHz = 44_100;

    /// <summary>Mel bands in a region's spectrum, from 0 Hz to half <see cref="AnalysisRateHz"/>.</summary>
    public const int MelBands = 64;

    /// <summary>Most harmonics kept per frame (David).</summary>
    public const int MaxHarmonics = 64;

    /// <summary>Harmonics stop below this frequency, the top of hearing.</summary>
    private const double TopHarmonicHz = 20_000;

    /// <summary>A region spans at most this many semitones (David: an octave).</summary>
    private const double RegionSemitones = 12;

    /// <summary>Most regions per sample: FM fitting makes 1–3 patches per instrument (docs/multi-pass.md#candidates).</summary>
    private const int MaxRegions = 3;

    /// <summary>ProTracker's C-2 period: a sample the song never plays is analyzed at C-2 on a PAL Amiga (docs/formats/mod.md#pitch).</summary>
    private const int C2Period = 428;

    /// <summary>Envelope step: 5 ms of playback.</summary>
    private const double EnvelopeHopSeconds = 0.005;

    /// <summary>Each envelope level is the RMS of the next 20 ms (or one cycle, if longer), so a low tone's cycle doesn't wobble it.</summary>
    private const double EnvelopeWindowSeconds = 0.020;

    /// <summary>The envelope covers a one-shot up to this long, and a looped sample's loop until at least <see cref="MinLoopEnvelopeSeconds"/>.</summary>
    private const double MaxEnvelopeSeconds = 4;

    private const double MinLoopEnvelopeSeconds = 1;

    /// <summary>The attack ends at the first window within this many dB of the peak.</summary>
    private const double AttackDb = 3;

    /// <summary>The decay ends this many dB below the peak; a fall this large in <see cref="DecayWindowSeconds"/> is fully percussive.</summary>
    private const double DecayDb = 20;

    /// <summary>How long after the peak the fall is measured for the percussive score.</summary>
    private const double DecayWindowSeconds = 0.5;

    /// <summary>An attack this short or shorter counts as a hit; one of <see cref="SlowAttackSeconds"/> or longer as none.</summary>
    private const double FastAttackSeconds = 0.010;

    private const double SlowAttackSeconds = 0.080;

    /// <summary>Frames in each pitch-track and flatness frame (a power of two, for the FFT), and the longest cycle searched.</summary>
    private const int TrackFrames = 1024;

    private const int TrackMaxLag = 512;

    /// <summary>The pitch track looks for the sample's cycle at most this many semitones from its one pitch, so a frame can't jump an octave.</summary>
    private const double TrackSemitones = 7;

    /// <summary>The longest cycle the pitch track can follow, in frames.</summary>
    private const int MaxTrackLag = 1024;

    /// <summary>Frames between pitch-track frames.</summary>
    private const int TrackHop = 256;

    /// <summary>The pitch track covers this much of the envelope's sound.</summary>
    private const double TrackSeconds = 2;

    /// <summary>Frames quieter than the peak by more than this are left out of the scores.</summary>
    private const double TrackFloorDb = 40;

    /// <summary>Shortest cycle searched, in frames.</summary>
    private const int MinLag = 2;

    /// <summary>A frame has a clear cycle when YIN's normalized difference dips below this (YIN's usual threshold).</summary>
    private const double VoicedThreshold = 0.15;

    /// <summary>A frame whose best normalized difference reaches this is not tonal at all.</summary>
    private const double AperiodicDifference = 0.5;

    /// <summary>The spectral flatness of white noise's power spectrum (e^−γ), which scores 1 for noisy.</summary>
    private const double WhiteNoiseFlatness = 0.5615;

    /// <summary>Each region is analyzed over this much of its playback.</summary>
    public const double RegionSeconds = 1;

    /// <summary>A region's mel frame size and the step between frames (10 ms at <see cref="AnalysisRateHz"/>).</summary>
    private const int MelFftSize = 2048;

    private const int SpectrumHop = 441;

    /// <summary>The harmonic frame size: 10.8 Hz bins, fine enough to separate the harmonics of a 40 Hz bass.</summary>
    private const int HarmonicFftSize = 4096;

    /// <summary>A score at or above this counts for the label.</summary>
    private const double LabelScore = 0.5;

    /// <summary>
    /// Analyzes every sample of <paramref name="song"/> that has sound, except those in <paramref name="ignore"/>
    /// (indexes into <c>Song.Samples</c>), with the notes <paramref name="playback"/> starts on each. A sample a channel
    /// swaps in without a note (MOD: an instrument alone, which changes the loop) counts as one note at the channel's
    /// pitch, once per swap.
    /// </summary>
    public static IReadOnlyList<SampleAnalysis> AnalyzeSong(Song song, Playback playback, IReadOnlySet<int> ignore)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(ignore);
        List<double>[] rates = [.. song.Samples.Select(_ => new List<double>())];
        int?[] previous = new int?[song.Channels.Count];
        foreach (PlaybackTick tick in playback.Ticks)
        {
            for (int c = 0; c < tick.Channels.Count && c < previous.Length; c++)
            {
                ChannelTick channel = tick.Channels[c];
                int? played = channel.Started is { } started ? started.Sample
                    : channel.Playing && channel.Next.Sample != previous[c] ? channel.Next.Sample
                    : null;
                if (played is { } sample && sample < rates.Length && channel.Pitch.PlaybackRateHz > 0)
                {
                    rates[sample].Add(channel.Pitch.PlaybackRateHz);
                }

                previous[c] = channel.Next.Sample;
            }
        }

        return [.. Enumerable.Range(0, song.Samples.Count)
            .Where(s => !song.Samples[s].IsEmpty && !ignore.Contains(s))
            .Select(s => Analyze(s, song.Samples[s], rates[s]))];
    }

    /// <summary>Analyzes one sample, played at <paramref name="noteRatesHz"/> (one entry per note start; may be empty).</summary>
    public static SampleAnalysis Analyze(int index, Sample sample, IReadOnlyList<double> noteRatesHz)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(noteRatesHz);
        IReadOnlyList<RegionBounds> bounds = Regions(noteRatesHz);
        double rate = bounds.MaxBy(r => r.Notes)!.RateHz; // Regions never returns an empty list.
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        float[] sound = PlayOut(sample, rate, EnvelopeSeconds(sample, rate));
        AmplitudeEnvelope envelope = Envelope(sample, sound, rate, period);
        if (envelope.PeakDb <= FloorDb)
        {
            return new SampleAnalysis(index, sample.Name, SampleKind.Silent, 0, 0, 0, null, envelope, Loop(sample, envelope, period), [.. bounds.Select(b => Region(sample, b, null))]);
        }

        Track track = PitchTrack(sound.AsSpan(0, Math.Min(sound.Length, (int)(TrackSeconds * rate))), envelope.PeakDb, period);
        double percussive = PercussiveScore(sample, envelope, period);
        PitchStability? pitch = period is { } p ? Stability(p, track) : null;
        SampleKind kind = Label(percussive, track.Tonal, track.Noisy);
        return new SampleAnalysis(
            index,
            sample.Name,
            kind,
            percussive,
            track.Tonal,
            track.Noisy,
            pitch,
            envelope,
            Loop(sample, envelope, period),
            [.. bounds.Select(b => Region(sample, b, period))]);
    }

    /// <summary>
    /// Groups note rates into regions at most an octave wide, from the lowest note up (the fewest such regions); when
    /// that makes more than three, into three of equal width instead. Each region's rate is its median note. No notes
    /// give one region at C-2.
    /// </summary>
    internal static IReadOnlyList<RegionBounds> Regions(IReadOnlyList<double> noteRatesHz)
    {
        double[] rates = [.. noteRatesHz.Where(r => r > 0 && double.IsFinite(r)).Order()];
        if (rates.Length == 0)
        {
            double c2 = AmigaPitch.PalClockHz / C2Period;
            return [new RegionBounds(c2, c2, c2, 0)];
        }

        List<List<double>> groups = [];
        foreach (double rate in rates)
        {
            if (groups.Count == 0 || Semitones(groups[^1][0], rate) > RegionSemitones)
            {
                groups.Add([]);
            }

            groups[^1].Add(rate);
        }

        if (groups.Count > MaxRegions)
        {
            double width = Semitones(rates[0], rates[^1]) / MaxRegions;
            groups = [.. rates
                .GroupBy(r => Math.Min(MaxRegions - 1, (int)(Semitones(rates[0], r) / width)))
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())];
        }

        return [.. groups.Select(g => new RegionBounds(g[0], g[^1], g[(g.Count - 1) / 2], g.Count))];
    }

    private static double Semitones(double fromHz, double toHz) => 12 * Math.Log2(toHz / fromHz);

    private static double Db(double rms) => rms <= 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(rms));

    /// <summary>A one-shot up to its end or <see cref="MaxEnvelopeSeconds"/>; a loop for at least <see cref="MinLoopEnvelopeSeconds"/>.</summary>
    private static double EnvelopeSeconds(Sample sample, double rate)
    {
        double seconds = Math.Min(MaxEnvelopeSeconds, sample.Length / rate);
        return sample.Loop is null ? seconds : Math.Max(seconds, MinLoopEnvelopeSeconds);
    }

    /// <summary>The sample as a note plays it for <paramref name="seconds"/> at <paramref name="rate"/>: its loop repeated, a one-shot stopping at its end.</summary>
    private static float[] PlayOut(Sample sample, double rate, double seconds) =>
        SampleProcessor.Unroll(sample, (int)Math.Ceiling(seconds * rate));

    /// <summary>
    /// Frame <paramref name="i"/> of the sample as a note plays it, however long: a one-shot is silent after its end; a
    /// looped sample plays to its loop's end, then repeats the loop (as <c>SampleProcessor.Unroll</c>).
    /// </summary>
    private static float PlayedFrame(Sample sample, int i)
    {
        if (sample.Loop is not { } loop)
        {
            return i < sample.Length ? sample.Data[i] : 0;
        }

        int loopEnd = loop.Start + loop.Length;
        return sample.Data[i < loopEnd ? i : loop.Start + ((i - loopEnd) % loop.Length)];
    }

    /// <summary>The envelope's step and window, in frames at <paramref name="rate"/>.</summary>
    private static (int Hop, int Window) EnvelopeSteps(double rate, double? period)
    {
        int hop = Math.Max(1, (int)Math.Round(rate * EnvelopeHopSeconds));
        return (hop, Math.Max(hop, (int)Math.Ceiling(Math.Max(rate * EnvelopeWindowSeconds, period ?? 0))));
    }

    /// <summary>True when the envelope reaches a one-shot's end, which counts as silence after it.</summary>
    private static bool EndsInSilence(Sample sample, float[] sound) => sample.Loop is null && sound.Length >= sample.Length;

    private static AmplitudeEnvelope Envelope(Sample sample, float[] sound, double rate, double? period)
    {
        (int hop, int window) = EnvelopeSteps(rate, period);
        double hopSeconds = hop / rate;
        List<double> levels = [];
        for (int start = 0; start < sound.Length; start += hop)
        {
            // A window past the end of the envelope's sound reads on as the note plays, not as silence.
            levels.Add(Db(WindowRms(sample, start, window)));
        }

        if (levels.Count == 0)
        {
            return new AmplitudeEnvelope(rate, hopSeconds, levels, FloorDb, 0, null, null);
        }

        double peak = levels.Max();
        int peakIndex = levels.IndexOf(peak);
        int attack = levels.FindIndex(level => level >= peak - AttackDb);
        int decayIndex = levels.FindIndex(peakIndex, level => level <= peak - DecayDb);
        double? decay = decayIndex >= 0 ? (decayIndex - peakIndex) * hopSeconds
            : EndsInSilence(sample, sound) ? (levels.Count - peakIndex) * hopSeconds
            : null;
        double? sustain = sample.Loop is { } loop ? Db(Rms(sample.Data.AsSpan(loop.Start, loop.Length))) - peak : null;
        return new AmplitudeEnvelope(rate, hopSeconds, levels, peak, attack * hopSeconds, decay, sustain);
    }

    /// <summary>
    /// A fast attack (1 at 10 ms or less, 0 at 80 ms or more, even in log time between) times the fall in the half second
    /// after the peak (1 at 20 dB or more): a hit that fades. Half a second past the envelope's end, the level is read
    /// from the sample as it plays on (silence only after a one-shot's real end).
    /// </summary>
    private static double PercussiveScore(Sample sample, AmplitudeEnvelope envelope, double? period)
    {
        double attack = Math.Clamp(Math.Log(SlowAttackSeconds / Math.Max(envelope.AttackSeconds, 1e-6)) / Math.Log(SlowAttackSeconds / FastAttackSeconds), 0, 1);
        IReadOnlyList<double> levels = envelope.LevelsDb;
        int peakIndex = levels.ToList().IndexOf(envelope.PeakDb);
        int later = peakIndex + (int)Math.Round(DecayWindowSeconds / envelope.HopSeconds);
        (int hop, int window) = EnvelopeSteps(envelope.RateHz, period);
        double after = later < levels.Count ? levels[later] : Db(WindowRms(sample, later * hop, window));
        double fall = Math.Clamp((envelope.PeakDb - after) / DecayDb, 0, 1);
        return attack * fall;
    }

    private static SampleKind Label(double percussive, double tonal, double noisy) =>
        (percussive >= LabelScore, tonal >= LabelScore, noisy >= LabelScore) switch
        {
            (true, true, _) => SampleKind.PluckedTone,
            (false, true, _) => SampleKind.Tone,
            (true, false, _) => SampleKind.Drum,
            (false, false, true) => SampleKind.Noise,
            _ => SampleKind.Unclear,
        };

    /// <summary>
    /// The RMS of <paramref name="window"/> frames from <paramref name="start"/> as a note plays them (<see cref="PlayedFrame"/>).
    /// </summary>
    private static double WindowRms(Sample sample, int start, int window)
    {
        double sum = 0;
        for (int i = start; i < start + window; i++)
        {
            float v = PlayedFrame(sample, i);
            sum += (double)v * v;
        }

        return Math.Sqrt(sum / window);
    }

    /// <summary>One pitch-track frame: its energy (the weight), its cycle in frames if it has a clear one near the sample's pitch.</summary>
    private readonly record struct TrackFrame(double Energy, double? Period);

    /// <summary>The pitch track and the tonal and noisy scores, energy-weighted over the frames loud enough to count.</summary>
    private sealed record Track(IReadOnlyList<TrackFrame> Frames, double Tonal, double Noisy);

    /// <summary>
    /// Steps through <paramref name="sound"/> in frames of <see cref="TrackFrames"/> (fewer for a short sample): YIN's
    /// best normalized difference gives how tonal each frame is and its cycle, the spectral flatness how noisy.
    /// </summary>
    private static Track PitchTrack(ReadOnlySpan<float> sound, double peakDb, double? samplePeriod)
    {
        int size = TrackFrames;
        while (size > 64 && size * 2 > sound.Length)
        {
            size /= 2;
        }

        int maxLag = Math.Min(Math.Min(TrackMaxLag, size), sound.Length - size);
        if (maxLag <= MinLag)
        {
            return new Track([], 0, 0);
        }

        // Around the sample's own cycle: the shortest and longest lag the track may follow.
        int lowLag = 0;
        int highLag = 0;
        if (samplePeriod is { } p)
        {
            maxLag = Math.Min(Math.Max(maxLag, (int)Math.Ceiling(p * Math.Pow(2, TrackSemitones / 12)) + 2), Math.Min(MaxTrackLag, sound.Length - size));
            lowLag = Math.Max(MinLag, (int)Math.Floor(p * Math.Pow(2, -TrackSemitones / 12)));
            highLag = Math.Min(maxLag - 1, (int)Math.Ceiling(p * Math.Pow(2, TrackSemitones / 12)));
        }

        double floorPower = Math.Pow(10, (peakDb - TrackFloorDb) / 10);
        FftPlan fft = new(size);
        double[] window = Hann(size);
        double[] real = new double[size];
        double[] imag = new double[size];
        List<TrackFrame> frames = [];
        double weight = 0;
        double tonal = 0;
        double noisy = 0;
        for (int start = 0; start + size + maxLag <= sound.Length; start += TrackHop)
        {
            ReadOnlySpan<float> frame = sound.Slice(start, size + maxLag);
            double energy = 0;
            for (int i = 0; i < size; i++)
            {
                energy += (double)frame[i] * frame[i];
            }

            if (energy / size < floorPower)
            {
                continue;
            }

            double[] normalized = Yin.Normalized(frame, size, maxLag);
            double best = normalized.AsSpan(MinLag).ToArray().Min();
            double? period = lowLag < highLag ? Yin.DipNear(normalized, lowLag, highLag, VoicedThreshold) : null;
            frames.Add(new TrackFrame(energy, period));
            weight += energy;
            tonal += energy * Math.Clamp(1 - (best / AperiodicDifference), 0, 1);
            noisy += energy * Math.Clamp(Flatness(frame[..size], window, fft, real, imag) / WhiteNoiseFlatness, 0, 1);
        }

        return weight <= 0 ? new Track(frames, 0, 0) : new Track(frames, tonal / weight, noisy / weight);
    }

    /// <summary>The geometric mean of the frame's power spectrum over its arithmetic mean (DC left out): 0 for a pure tone, about 0.56 for white noise.</summary>
    private static double Flatness(ReadOnlySpan<float> frame, double[] window, FftPlan fft, double[] real, double[] imag)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            real[i] = frame[i] * window[i];
            imag[i] = 0;
        }

        fft.Forward(real, imag);
        int bins = frame.Length / 2;
        double logSum = 0;
        double sum = 0;
        for (int k = 1; k <= bins; k++)
        {
            double power = (real[k] * real[k]) + (imag[k] * imag[k]) + 1e-20;
            logSum += Math.Log(power);
            sum += power;
        }

        return Math.Exp(logSum / bins) / (sum / bins);
    }

    /// <summary>How steady the voiced frames' cycle is, around the sample's one pitch <paramref name="period"/>.</summary>
    private static PitchStability Stability(double period, Track track)
    {
        double total = track.Frames.Sum(f => f.Energy);
        TrackFrame[] voiced = [.. track.Frames.Where(f => f.Period is not null)];
        if (voiced.Length == 0 || total <= 0)
        {
            return new PitchStability(period, 0, 0, 0);
        }

        double voicedEnergy = voiced.Sum(f => f.Energy);
        double[] periods = [.. voiced.Select(f => f.Period!.Value).Order()]; // Only voiced frames, which have a period.
        double median = periods[(periods.Length - 1) / 2];
        double deviation = Math.Sqrt(voiced.Sum(f => f.Energy * Square(Cents(median, f.Period!.Value))) / voicedEnergy);
        double drift = Cents(voiced[0].Period!.Value, voiced[^1].Period!.Value);
        return new PitchStability(period, voicedEnergy / total, deviation, drift);
    }

    /// <summary>The pitch of a cycle of <paramref name="toPeriod"/> frames against one of <paramref name="fromPeriod"/>, in cents (a shorter cycle is higher).</summary>
    private static double Cents(double fromPeriod, double toPeriod) => 1200 * Math.Log2(fromPeriod / toPeriod);

    private static double Square(double x) => x * x;

    private static LoopAnalysis? Loop(Sample sample, AmplitudeEnvelope envelope, double? period) =>
        sample.Loop is { } loop
            ? new LoopAnalysis(loop.Start, loop.Length, Db(Rms(sample.Data.AsSpan(loop.Start, loop.Length))) - envelope.PeakDb, period is { } p ? loop.Length / p : null)
            : null;

    /// <summary>The region's sound at <see cref="AnalysisRateHz"/>: its mel bands, and its harmonics when the sample has a pitch.</summary>
    private static PitchRegion Region(Sample sample, RegionBounds bounds, double? period)
    {
        double? fundamental = period is { } p ? bounds.RateHz / p : null;
        float[] played = PlayOut(sample, bounds.RateHz, RegionSeconds);
        float[] sound = played.Length == 0 ? [] : SampleProcessor.Resample(played, bounds.RateHz, AnalysisRateHz, ResamplingMode.Smooth);
        return new PitchRegion(bounds.LowRateHz, bounds.HighRateHz, bounds.RateHz, bounds.Notes, fundamental, Spectrum(sound, fundamental));
    }

    /// <summary>
    /// The spectrum of <paramref name="sound"/> (at <see cref="AnalysisRateHz"/>) as a region's is taken: its mel bands, and
    /// its harmonics of <paramref name="fundamentalHz"/> when given. The FM fitter measures its renders with this, so they
    /// compare frame for frame with the sample's (docs/adr/0069-fm-patch-fitting.md).
    /// </summary>
    internal static RegionSpectrum Spectrum(float[] sound, double? fundamentalHz)
    {
        double hopSeconds = (double)SpectrumHop / AnalysisRateHz;
        if (sound.Length == 0)
        {
            return new RegionSpectrum(hopSeconds, [], null);
        }

        MelAnalyzer mel = new(AnalysisRateHz, MelFftSize, SpectrumHop, MelBands);
        float[][] bands = [.. mel.LogMel(sound, sound.Length).Select(row => row.Select(v => (float)v).ToArray())];
        IReadOnlyList<float[]>? harmonics = fundamentalHz is { } f ? Harmonics(sound, f) : null;
        return new RegionSpectrum(hopSeconds, bands, harmonics);
    }

    /// <summary>
    /// The level of each harmonic of <paramref name="fundamentalHz"/> in each frame: the strongest bin within a quarter of
    /// the fundamental of the harmonic, in dB of a full-scale sine; <c>null</c> when not even the fundamental is below 20 kHz.
    /// </summary>
    internal static float[][]? Harmonics(float[] sound, double fundamentalHz)
    {
        int count = Math.Min(MaxHarmonics, (int)Math.Floor(TopHarmonicHz / fundamentalHz));
        if (count < 1)
        {
            return null;
        }

        FftPlan fft = new(HarmonicFftSize);
        double[] window = Hann(HarmonicFftSize);
        double peak = window.Sum() / 2;
        double scale = 1 / (peak * peak);
        double binHz = (double)AnalysisRateHz / HarmonicFftSize;
        double halfWidth = Math.Max(1, fundamentalHz / 4 / binHz);
        int bins = HarmonicFftSize / 2;
        int frames = sound.Length <= HarmonicFftSize ? 1 : 1 + (int)Math.Ceiling((double)(sound.Length - HarmonicFftSize) / SpectrumHop);
        double[] real = new double[HarmonicFftSize];
        double[] imag = new double[HarmonicFftSize];
        float[][] result = new float[frames][];
        for (int t = 0; t < frames; t++)
        {
            int start = t * SpectrumHop;
            for (int i = 0; i < HarmonicFftSize; i++)
            {
                real[i] = start + i < sound.Length ? sound[start + i] * window[i] : 0;
                imag[i] = 0;
            }

            fft.Forward(real, imag);
            float[] row = new float[count];
            for (int h = 0; h < count; h++)
            {
                double centre = (h + 1) * fundamentalHz / binHz;
                int from = Math.Clamp((int)Math.Ceiling(centre - halfWidth), 1, bins);
                int to = Math.Clamp((int)Math.Floor(centre + halfWidth), from, bins);
                double power = 0;
                for (int k = from; k <= to; k++)
                {
                    power = Math.Max(power, ((real[k] * real[k]) + (imag[k] * imag[k])) * scale);
                }

                row[h] = (float)(power <= 0 ? FloorDb : Math.Max(FloorDb, 10 * Math.Log10(power)));
            }

            result[t] = row;
        }

        return result;
    }

    /// <summary>A periodic Hann window, as the scorer uses.</summary>
    private static double[] Hann(int size)
    {
        double[] window = new double[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / size));
        }

        return window;
    }

    private static double Rms(ReadOnlySpan<float> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (float v in data)
        {
            sum += (double)v * v;
        }

        return Math.Sqrt(sum / data.Length);
    }
}

/// <summary>Where one pitch region lies before its spectrum is taken.</summary>
/// <param name="LowRateHz">The lowest playback rate in the region.</param>
/// <param name="HighRateHz">The highest.</param>
/// <param name="RateHz">The median note's rate.</param>
/// <param name="Notes">Note starts in the region.</param>
internal readonly record struct RegionBounds(double LowRateHz, double HighRateHz, double RateHz, int Notes);
