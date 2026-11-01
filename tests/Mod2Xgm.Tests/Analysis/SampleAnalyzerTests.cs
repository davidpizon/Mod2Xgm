using Mod2Xgm.Analysis;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="SampleAnalyzer"/>: the scores, pitch, envelope, loop and pitch regions of made-up samples.</summary>
public sealed class SampleAnalyzerTests
{
    /// <summary>ProTracker's C-2 rate on a PAL Amiga, where most samples are played.</summary>
    private const double C2 = AmigaPitch.PalClockHz / 428;

    /// <summary>A rate two octaves above <see cref="C2"/>.</summary>
    private const double C4 = C2 * 4;

    /// <summary>Notes played at C-2, eight times.</summary>
    private static readonly double[] AtC2 = [.. Enumerable.Repeat(C2, 8)];

    /// <summary>One cycle of a sine of amplitude <paramref name="amplitude"/>, <paramref name="period"/> frames long, looped.</summary>
    private static Sample LoopedSine(int period, double amplitude = 0.5) =>
        new("sine", [.. Enumerable.Range(0, period).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * i / period)))], new SampleLoop(0, period));

    /// <summary>A one-shot of <paramref name="frames"/> frames: <paramref name="wave"/> times an envelope.</summary>
    private static Sample OneShot(int frames, Func<int, double> wave, Func<double, double> envelope, double rate = C2) =>
        new("one-shot", [.. Enumerable.Range(0, frames).Select(i => (float)(wave(i) * envelope(i / rate)))], null);

    /// <summary>White noise from a fixed seed, −1 to 1.</summary>
    private static Func<int, double> Noise()
    {
        Random random = new(7);
        double[] values = [.. Enumerable.Range(0, 1 << 17).Select(_ => (random.NextDouble() * 2) - 1)];
        return i => values[i % values.Length];
    }

    /// <summary>A sine of <paramref name="period"/> frames.</summary>
    private static Func<int, double> Sine(double period) => i => Math.Sin(2 * Math.PI * i / period);

    /// <summary>Falls by <paramref name="dbPerSecond"/>, from full level at once.</summary>
    private static Func<double, double> Decay(double dbPerSecond) => t => Math.Pow(10, -dbPerSecond * t / 20);

    /// <summary>A looped one-cycle sine is a held tone: tonal, not percussive or noisy, steady, one cycle per loop, holding its level.</summary>
    [Fact]
    public void ALoopedSineIsATone()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2);

        Assert.Equal(SampleKind.Tone, a.Kind);
        Assert.True(a.Tonal > 0.95, $"tonal {a.Tonal}");
        Assert.True(a.Noisy < 0.05, $"noisy {a.Noisy}");
        Assert.True(a.Percussive < 0.1, $"percussive {a.Percussive}");
        Assert.NotNull(a.Pitch);
        Assert.Equal(64, a.Pitch.PeriodFrames, 0.1);
        Assert.Equal(1, a.Pitch.VoicedShare, 0.01);
        Assert.True(a.Pitch.DeviationCents < 1, $"deviation {a.Pitch.DeviationCents}");
        Assert.NotNull(a.Loop);
        Assert.Equal(1, a.Loop.Cycles!.Value, 0.01);
        Assert.Equal(0, a.Envelope.SustainDb!.Value, 0.2);
        Assert.Null(a.Envelope.DecaySeconds);
        Assert.Equal(20 * Math.Log10(0.5 / Math.Sqrt(2)), a.Envelope.PeakDb, 0.2);
    }

    /// <summary>A sine that starts at once and falls 60 dB a second is a plucked tone: percussive and tonal; 20 dB down after a third of a second.</summary>
    [Fact]
    public void ADecayingSineIsAPluckedTone()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, OneShot(16_000, Sine(40), Decay(60)), AtC2);

        Assert.Equal(SampleKind.PluckedTone, a.Kind);
        Assert.True(a.Percussive > 0.9, $"percussive {a.Percussive}");
        Assert.True(a.Tonal > 0.9, $"tonal {a.Tonal}");
        Assert.Equal(0, a.Envelope.AttackSeconds, 0.01);
        Assert.NotNull(a.Envelope.DecaySeconds);
        Assert.Equal(1 / 3.0, a.Envelope.DecaySeconds.Value, 0.03);
        Assert.Null(a.Envelope.SustainDb);
        Assert.Null(a.Loop);
    }

    /// <summary>A burst of noise that fades is a drum: percussive and noisy, without a pitch.</summary>
    [Fact]
    public void AFadingNoiseBurstIsADrum()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, OneShot(4_000, Noise(), Decay(100)), AtC2);

        Assert.Equal(SampleKind.Drum, a.Kind);
        Assert.True(a.Percussive > 0.9, $"percussive {a.Percussive}");
        Assert.True(a.Noisy > 0.8, $"noisy {a.Noisy}");
        Assert.True(a.Tonal < 0.3, $"tonal {a.Tonal}");
        Assert.Null(a.Pitch);
        Assert.All(a.Regions, r => Assert.Null(r.Spectrum.HarmonicsDb));
    }

    /// <summary>Held noise is noise: noisy, neither percussive nor tonal.</summary>
    [Fact]
    public void HeldNoiseIsNoise()
    {
        Func<int, double> noise = Noise();
        Sample sample = new("hiss", [.. Enumerable.Range(0, 8_192).Select(i => (float)(0.5 * noise(i)))], new SampleLoop(0, 8_192));

        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        Assert.Equal(SampleKind.Noise, a.Kind);
        Assert.True(a.Noisy > 0.8, $"noisy {a.Noisy}");
        Assert.True(a.Percussive < 0.2, $"percussive {a.Percussive}");
    }

    /// <summary>A sine that fades in over 200 ms is not percussive, and its attack is measured.</summary>
    [Fact]
    public void ASlowAttackIsNotPercussive()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, OneShot(8_287, Sine(40), t => Math.Min(1, t / 0.2)), AtC2);

        Assert.Equal(SampleKind.Tone, a.Kind);
        Assert.Equal(0, a.Percussive, 0.01);
        Assert.InRange(a.Envelope.AttackSeconds, 0.12, 0.2);
    }

    /// <summary>A sine whose pitch falls by a fifth drifts down, and strays from its median.</summary>
    [Fact]
    public void AFallingPitchDrifts()
    {
        double phase = 0;
        float[] data = new float[16_000];
        for (int i = 0; i < data.Length; i++)
        {
            double period = 40 * Math.Pow(1.5, (double)i / data.Length);
            phase += 2 * Math.PI / period;
            data[i] = (float)(0.8 * Math.Sin(phase));
        }

        SampleAnalysis a = SampleAnalyzer.Analyze(0, new Sample("sweep", data, null), AtC2);

        Assert.NotNull(a.Pitch);
        Assert.True(a.Pitch.DriftCents < -400, $"drift {a.Pitch.DriftCents}");
        Assert.True(a.Pitch.DeviationCents > 100, $"deviation {a.Pitch.DeviationCents}");
    }

    /// <summary>A sample of zeros is silent and scores nothing, but still gets its regions.</summary>
    [Fact]
    public void ZerosAreSilent()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(2, new Sample("blank", new float[500], null), AtC2);

        Assert.Equal(SampleKind.Silent, a.Kind);
        Assert.Equal(2, a.Sample);
        Assert.Equal((0, 0, 0), (a.Percussive, a.Tonal, a.Noisy));
        Assert.Single(a.Regions);
    }

    /// <summary>A sample too short for the pitch track still gets an envelope and no scores from the track.</summary>
    [Fact]
    public void AVeryShortSampleHasNoTrack()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, OneShot(40, Sine(8), _ => 1), AtC2);

        Assert.Equal((0, 0), (a.Tonal, a.Noisy));
        Assert.NotEmpty(a.Envelope.LevelsDb);
    }

    /// <summary>
    /// A region's harmonics are its fundamental's: a sine has one strong harmonic at its level (a 0.5 sine is −6 dB) and
    /// nothing at the second; a saw has all of them, falling 6 dB per octave.
    /// </summary>
    [Fact]
    public void HarmonicsFollowTheWave()
    {
        PitchRegion sine = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2).Regions.Single();
        Sample saw = new("saw", [.. Enumerable.Range(0, 64).Select(i => (float)((i / 32.0) - 1) * 0.5f)], new SampleLoop(0, 64));
        PitchRegion sawRegion = SampleAnalyzer.Analyze(0, saw, AtC2).Regions.Single();

        Assert.Equal(C2 / 64, sine.FundamentalHz!.Value, 0.05);
        float[] frame = sine.Spectrum.HarmonicsDb![10];
        Assert.Equal(-6.02, frame[0], 0.5);
        Assert.True(frame[1] < -40, $"second harmonic {frame[1]} dB");
        float[] sawFrame = sawRegion.Spectrum.HarmonicsDb![10];
        Assert.Equal(6.02, sawFrame[0] - sawFrame[1], 0.5);
        Assert.Equal(6.02, sawFrame[1] - sawFrame[3], 0.6);
    }

    /// <summary>Harmonics stop at 64 (David), or below 20 kHz for a high note: a 950 Hz tone keeps 21.</summary>
    [Fact]
    public void HarmonicsStopAt64OrBelow20Khz()
    {
        PitchRegion low = SampleAnalyzer.Analyze(0, LoopedSine(256), AtC2).Regions.Single();
        double rate = 950.0 * 16;
        PitchRegion high = SampleAnalyzer.Analyze(0, LoopedSine(16), [rate]).Regions.Single();

        Assert.Equal(SampleAnalyzer.MaxHarmonics, low.Spectrum.HarmonicsDb![0].Length);
        Assert.Equal(21, high.Spectrum.HarmonicsDb![0].Length);
        Assert.Equal(SampleAnalyzer.MelBands, high.Spectrum.MelBandsDb[0].Length);
        Assert.Equal(0.01, high.Spectrum.HopSeconds, 1e-9);
    }

    /// <summary>Without notes, a sample gets one region at C-2.</summary>
    [Fact]
    public void WithoutNotesTheRegionIsC2()
    {
        RegionBounds region = Assert.Single(SampleAnalyzer.Regions([]));
        Assert.Equal(new RegionBounds(C2, C2, C2, 0), region);
    }

    /// <summary>Notes within an octave share one region, analyzed at their median; bad rates are left out.</summary>
    [Fact]
    public void NotesWithinAnOctaveShareARegion()
    {
        RegionBounds region = Assert.Single(SampleAnalyzer.Regions([C2 * 1.5, C2, C2 * 2, C2 * 1.2, 0, double.NaN]));
        Assert.Equal(new RegionBounds(C2, C2 * 2, C2 * 1.2, 4), region);
    }

    /// <summary>Notes over more than an octave split into regions from the lowest note up.</summary>
    [Fact]
    public void WiderNotesSplitByOctave()
    {
        IReadOnlyList<RegionBounds> regions = SampleAnalyzer.Regions([C2, C2 * 1.9, C2 * 2.1, C4]);

        Assert.Equal([new RegionBounds(C2, C2 * 1.9, C2, 2), new RegionBounds(C2 * 2.1, C4, C2 * 2.1, 2)], regions);
    }

    /// <summary>Notes over more than three octaves make three regions of equal width instead of four or more.</summary>
    [Fact]
    public void AtMostThreeRegions()
    {
        IReadOnlyList<RegionBounds> regions = SampleAnalyzer.Regions([C2, C2 * 2.5, C2 * 6, C2 * 16, C2 * 32]);

        Assert.Equal(3, regions.Count);
        Assert.Equal([2, 1, 2], regions.Select(r => r.Notes));
        Assert.Equal(C2 * 32, regions[^1].HighRateHz);
    }

    /// <summary>Every region is analyzed at its own rate: a higher region sounds higher.</summary>
    [Fact]
    public void EachRegionIsAnalyzedAtItsRate()
    {
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), [C2, C2, C4]);

        Assert.Equal(2, a.Regions.Count);
        Assert.Equal(C2, a.Envelope.RateHz);
        Assert.Equal(4, a.Regions[1].FundamentalHz!.Value / a.Regions[0].FundamentalHz!.Value, 1e-9);
        Assert.True(a.Regions[1].Spectrum.HarmonicsDb![0].Length < a.Regions[0].Spectrum.HarmonicsDb![0].Length);
    }

    /// <summary>
    /// A song's analysis covers each sample with sound that isn't ignored, with the rates the playback starts it at.
    /// </summary>
    [Fact]
    public void ASongAnalyzesItsPlayedSamples()
    {
        Song song = new(
            "song",
            [new Channel(-1), new Channel(1)],
            [0],
            null,
            [new Pattern([new Row([new Cell(null, null, null, []), new Cell(null, null, null, [])])])],
            [new Instrument("a", 0, 1, 0), new Instrument("b", 1, 1, 0), new Instrument("c", 2, 1, 0)],
            [LoopedSine(64), new Sample("empty", [], null), LoopedSine(32)],
            new Timing(6, 125));
        ChannelTick Start(int sample, double rate) => new(true, new NotePitch(36, 0, rate), 1, 0, new SampleBlock(sample, 0, 64), new SampleBlock(sample, 0, 64));
        ChannelTick Idle() => new(false, default, 0, 0, null, new SampleBlock(null, 0, 0));
        Playback playback = new(
            [
                new PlaybackTick(0, 0.02, 0, 0, 0, false, [Start(0, C2), Start(2, C4)]),
                new PlaybackTick(0.02, 0.02, 0, 0, 1, false, [Start(0, C4), Idle()]),
            ],
            null,
            PlaybackEnd.Stops);

        IReadOnlyList<SampleAnalysis> all = SampleAnalyzer.AnalyzeSong(song, playback, new HashSet<int>());
        IReadOnlyList<SampleAnalysis> some = SampleAnalyzer.AnalyzeSong(song, playback, new HashSet<int> { 2 });

        Assert.Equal([0, 2], all.Select(a => a.Sample));
        Assert.Equal(2, all[0].Regions.Count);
        Assert.Equal(C4, all[1].Regions.Single().RateHz);
        Assert.Equal([0], some.Select(a => a.Sample));
    }
}
