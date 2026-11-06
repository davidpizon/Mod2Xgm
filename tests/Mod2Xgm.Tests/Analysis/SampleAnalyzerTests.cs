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

    /// <summary>A looped one-cycle sine is a held tone: tonal, not percussive or noisy.</summary>
    [Fact]
    public void Analyze_WithLoopedSine_ScoresTone()
    {
        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2);

        // Assert
        Assert.Equal(SampleKind.Tone, a.Kind);
        Assert.True(a.Tonal > 0.95, $"tonal {a.Tonal}");
        Assert.True(a.Noisy < 0.05, $"noisy {a.Noisy}");
        Assert.True(a.Percussive < 0.1, $"percussive {a.Percussive}");
    }

    /// <summary>A looped one-cycle sine has a steady pitch of one cycle per loop.</summary>
    [Fact]
    public void Analyze_WithLoopedSine_FindsSteadyPitch()
    {
        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2);

        // Assert
        Assert.NotNull(a.Pitch);
        Assert.Equal(64, a.Pitch.PeriodFrames, 0.1);
        Assert.Equal(1, a.Pitch.VoicedShare, 0.01);
        Assert.True(a.Pitch.DeviationCents < 1, $"deviation {a.Pitch.DeviationCents}");
        Assert.NotNull(a.Loop);
        Assert.Equal(1, a.Loop.Cycles ?? double.NaN, 0.01);
    }

    /// <summary>A looped sine holds its level, at the RMS level of a 0.5 sine.</summary>
    [Fact]
    public void Analyze_WithLoopedSine_HoldsLevel()
    {
        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2);

        // Assert
        Assert.Equal(0, a.Envelope.SustainDb ?? double.NaN, 0.2);
        Assert.Null(a.Envelope.DecaySeconds);
        Assert.Equal(20 * Math.Log10(0.5 / Math.Sqrt(2)), a.Envelope.PeakDb, 0.2);
    }

    /// <summary>A sine that starts at once and falls 60 dB a second is a plucked tone: percussive and tonal; 20 dB down after a third of a second.</summary>
    [Fact]
    public void Analyze_WithDecayingSine_ReturnsPluckedTone()
    {
        // Arrange
        Sample sample = OneShot(16_000, Sine(40), Decay(60));

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal(SampleKind.PluckedTone, a.Kind);
        Assert.True(a.Percussive > 0.9, $"percussive {a.Percussive}");
        Assert.True(a.Tonal > 0.9, $"tonal {a.Tonal}");
        Assert.Equal(0, a.Envelope.AttackSeconds, 0.01);
        Assert.Equal(1 / 3.0, a.Envelope.DecaySeconds ?? double.NaN, 0.03);
        Assert.Null(a.Envelope.SustainDb);
        Assert.Null(a.Loop);
    }

    /// <summary>A burst of noise that fades is a drum: percussive and noisy, without a pitch.</summary>
    [Fact]
    public void Analyze_WithFadingNoiseBurst_ReturnsDrum()
    {
        // Arrange
        Sample sample = OneShot(4_000, Noise(), Decay(100));

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal(SampleKind.Drum, a.Kind);
        Assert.True(a.Percussive > 0.9, $"percussive {a.Percussive}");
        Assert.True(a.Noisy > 0.8, $"noisy {a.Noisy}");
        Assert.True(a.Tonal < 0.3, $"tonal {a.Tonal}");
        Assert.Null(a.Pitch);
        Assert.All(a.Regions, r => Assert.Null(r.Spectrum.HarmonicsDb));
    }

    /// <summary>Held noise is noise: noisy, neither percussive nor tonal.</summary>
    [Fact]
    public void Analyze_WithHeldNoise_ReturnsNoise()
    {
        // Arrange
        Func<int, double> noise = Noise();
        Sample sample = new("hiss", [.. Enumerable.Range(0, 8_192).Select(i => (float)(0.5 * noise(i)))], new SampleLoop(0, 8_192));

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal(SampleKind.Noise, a.Kind);
        Assert.True(a.Noisy > 0.8, $"noisy {a.Noisy}");
        Assert.True(a.Percussive < 0.2, $"percussive {a.Percussive}");
    }

    /// <summary>A sine that fades in over 200 ms is not percussive, and its attack is measured.</summary>
    [Fact]
    public void Analyze_WithSlowAttack_IsNotPercussive()
    {
        // Arrange
        Sample sample = OneShot(8_287, Sine(40), t => Math.Min(1, t / 0.2));

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal(SampleKind.Tone, a.Kind);
        Assert.Equal(0, a.Percussive, 0.01);
        Assert.InRange(a.Envelope.AttackSeconds, 0.12, 0.2);
    }

    /// <summary>
    /// A one-shot longer than the envelope's 4 s still plays where the envelope stops: its last level doesn't fade at the
    /// cut, and a swell just before the cut isn't a hit that fades.
    /// </summary>
    [Fact]
    public void Analyze_WithOneShotPastEnvelopeEnd_PlaysOn()
    {
        // Arrange
        Sample sample = OneShot((int)(6 * C2), Sine(40), t => t < 3.8 ? 0.5 : 0.6);

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.True(a.Percussive < 0.1, $"percussive {a.Percussive}");
        Assert.Equal(a.Envelope.PeakDb, a.Envelope.LevelsDb[^1], 0.5);
        Assert.Null(a.Envelope.DecaySeconds);
    }

    /// <summary>A loop that starts after the envelope's 4 s: the envelope reads the intro up to its end, not the loop.</summary>
    [Fact]
    public void Analyze_WithIntroPastEnvelopeEnd_ReadsIntro()
    {
        // Arrange
        int intro = (int)(5 * C2);
        float[] data = [.. Enumerable.Range(0, intro + 40).Select(i => (float)((i < intro ? 0.5 : 0.05) * Math.Sin(2 * Math.PI * i / 40)))];
        Sample sample = new("intro", data, new SampleLoop(intro, 40));

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal(a.Envelope.PeakDb, a.Envelope.LevelsDb[^1], 0.5);
        Assert.Equal(-20, a.Envelope.SustainDb ?? double.NaN, 0.5);
        Assert.True(a.Percussive < 0.1, $"percussive {a.Percussive}");
    }

    /// <summary>A sine whose pitch falls by a fifth drifts down, and strays from its median.</summary>
    [Fact]
    public void Analyze_WithFallingPitch_ReportsDrift()
    {
        // Arrange
        double phase = 0;
        float[] data = new float[16_000];
        for (int i = 0; i < data.Length; i++)
        {
            double period = 40 * Math.Pow(1.5, (double)i / data.Length);
            phase += 2 * Math.PI / period;
            data[i] = (float)(0.8 * Math.Sin(phase));
        }

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, new Sample("sweep", data, null), AtC2);

        // Assert
        Assert.NotNull(a.Pitch);
        Assert.True(a.Pitch.DriftCents < -400, $"drift {a.Pitch.DriftCents}");
        Assert.True(a.Pitch.DeviationCents > 100, $"deviation {a.Pitch.DeviationCents}");
    }

    /// <summary>A sample of zeros is silent and scores nothing, but still gets its regions.</summary>
    [Fact]
    public void Analyze_WithZeros_ReturnsSilent()
    {
        // Arrange
        Sample sample = new("blank", new float[500], null);

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(2, sample, AtC2);

        // Assert
        Assert.Equal(SampleKind.Silent, a.Kind);
        Assert.Equal(2, a.Sample);
        Assert.Equal((0, 0, 0), (a.Percussive, a.Tonal, a.Noisy));
        Assert.Single(a.Regions);
    }

    /// <summary>A sample too short for the pitch track still gets an envelope and no scores from the track.</summary>
    [Fact]
    public void Analyze_WithVeryShortSample_HasNoTrack()
    {
        // Arrange
        Sample sample = OneShot(40, Sine(8), _ => 1);

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, sample, AtC2);

        // Assert
        Assert.Equal((0, 0), (a.Tonal, a.Noisy));
        Assert.NotEmpty(a.Envelope.LevelsDb);
    }

    /// <summary>A sine has one strong harmonic at its level (a 0.5 sine is −6 dB) and nothing at the second.</summary>
    [Fact]
    public void Analyze_WithSine_FindsOneHarmonic()
    {
        // Act
        PitchRegion region = SampleAnalyzer.Analyze(0, LoopedSine(64), AtC2).Regions.Single();

        // Assert
        Assert.Equal(C2 / 64, region.FundamentalHz ?? double.NaN, 0.05);
        Assert.NotNull(region.Spectrum.HarmonicsDb);
        float[] frame = region.Spectrum.HarmonicsDb[10];
        Assert.Equal(-6.02, frame[0], 0.5);
        Assert.True(frame[1] < -40, $"second harmonic {frame[1]} dB");
    }

    /// <summary>A saw has all the harmonics, falling 6 dB per octave.</summary>
    [Fact]
    public void Analyze_WithSaw_FindsFallingHarmonics()
    {
        // Arrange
        Sample saw = new("saw", [.. Enumerable.Range(0, 64).Select(i => (float)((i / 32.0) - 1) * 0.5f)], new SampleLoop(0, 64));

        // Act
        PitchRegion region = SampleAnalyzer.Analyze(0, saw, AtC2).Regions.Single();

        // Assert
        Assert.NotNull(region.Spectrum.HarmonicsDb);
        float[] frame = region.Spectrum.HarmonicsDb[10];
        Assert.Equal(6.02, frame[0] - frame[1], 0.5);
        Assert.Equal(6.02, frame[1] - frame[3], 0.6);
    }

    /// <summary>Harmonics stop at 64 (David).</summary>
    [Fact]
    public void Analyze_WithLowNote_StopsAt64Harmonics()
    {
        // Act
        PitchRegion region = SampleAnalyzer.Analyze(0, LoopedSine(256), AtC2).Regions.Single();

        // Assert
        Assert.NotNull(region.Spectrum.HarmonicsDb);
        Assert.Equal(SampleAnalyzer.MaxHarmonics, region.Spectrum.HarmonicsDb[0].Length);
    }

    /// <summary>Harmonics stop below 20 kHz for a high note: a 950 Hz tone keeps 21. The mel bands and hop don't change.</summary>
    [Fact]
    public void Analyze_WithHighNote_StopsHarmonicsBelow20Khz()
    {
        // Arrange
        double[] rates = [950.0 * 16];

        // Act
        PitchRegion region = SampleAnalyzer.Analyze(0, LoopedSine(16), rates).Regions.Single();

        // Assert
        Assert.NotNull(region.Spectrum.HarmonicsDb);
        Assert.Equal(21, region.Spectrum.HarmonicsDb[0].Length);
        Assert.Equal(SampleAnalyzer.MelBands, region.Spectrum.MelBandsDb[0].Length);
        Assert.Equal(0.01, region.Spectrum.HopSeconds, 1e-9);
    }

    /// <summary>Without notes, a sample gets one region at C-2.</summary>
    [Fact]
    public void Regions_WithoutNotes_ReturnsOneAtC2()
    {
        // Act
        RegionBounds region = Assert.Single(SampleAnalyzer.Regions([]));

        // Assert
        Assert.Equal(new RegionBounds(C2, C2, C2, 0), region);
    }

    /// <summary>Notes within an octave share one region, analyzed at their median; bad rates are left out.</summary>
    [Fact]
    public void Regions_WithNotesWithinOctave_ReturnsOneAtMedian()
    {
        // Arrange
        double[] rates = [C2 * 1.5, C2, C2 * 2, C2 * 1.2, 0, double.NaN];

        // Act
        RegionBounds region = Assert.Single(SampleAnalyzer.Regions(rates));

        // Assert
        Assert.Equal(new RegionBounds(C2, C2 * 2, C2 * 1.2, 4), region);
    }

    /// <summary>Notes over more than an octave split into regions from the lowest note up.</summary>
    [Fact]
    public void Regions_WithWiderNotes_SplitsByOctave()
    {
        // Arrange
        double[] rates = [C2, C2 * 1.9, C2 * 2.1, C4];

        // Act
        IReadOnlyList<RegionBounds> regions = SampleAnalyzer.Regions(rates);

        // Assert
        Assert.Equal([new RegionBounds(C2, C2 * 1.9, C2, 2), new RegionBounds(C2 * 2.1, C4, C2 * 2.1, 2)], regions);
    }

    /// <summary>Notes over more than three octaves make three regions of equal width instead of four or more.</summary>
    [Fact]
    public void Regions_WithVeryWideNotes_ReturnsAtMostThree()
    {
        // Arrange
        double[] rates = [C2, C2 * 2.5, C2 * 6, C2 * 16, C2 * 32];

        // Act
        IReadOnlyList<RegionBounds> regions = SampleAnalyzer.Regions(rates);

        // Assert
        Assert.Equal(3, regions.Count);
        Assert.Equal([2, 1, 2], regions.Select(r => r.Notes));
        Assert.Equal(C2 * 32, regions[^1].HighRateHz);
    }

    /// <summary>Every region is analyzed at its own rate: a higher region sounds higher, with room for fewer harmonics.</summary>
    [Fact]
    public void Analyze_WithTwoRegions_AnalyzesEachAtItsRate()
    {
        // Arrange
        double[] rates = [C2, C2, C4];

        // Act
        SampleAnalysis a = SampleAnalyzer.Analyze(0, LoopedSine(64), rates);

        // Assert
        Assert.Equal(2, a.Regions.Count);
        Assert.Equal(C2, a.Envelope.RateHz);
        Assert.Equal(4, (a.Regions[1].FundamentalHz ?? double.NaN) / (a.Regions[0].FundamentalHz ?? double.NaN), 1e-9);
        Assert.NotNull(a.Regions[0].Spectrum.HarmonicsDb);
        Assert.NotNull(a.Regions[1].Spectrum.HarmonicsDb);
        Assert.True(a.Regions[1].Spectrum.HarmonicsDb?[0].Length < a.Regions[0].Spectrum.HarmonicsDb?[0].Length);
    }

    /// <summary>
    /// A song's analysis covers each sample with sound, with the rates the playback starts it at; a sample swapped in
    /// without a note (an instrument alone) counts once, at the channel's pitch.
    /// </summary>
    [Fact]
    public void AnalyzeSong_WithPlayback_AnalyzesPlayedSamples()
    {
        // Act
        IReadOnlyList<SampleAnalysis> all = SampleAnalyzer.AnalyzeSong(ThreeSampleSong(), ThreeSamplePlayback(), new HashSet<int>());

        // Assert
        Assert.Equal([0, 2], all.Select(a => a.Sample));
        Assert.Equal(2, all[0].Regions.Count);
        Assert.Equal([C2, C4], all[1].Regions.Select(r => r.RateHz));
        Assert.Equal([1, 1], all[1].Regions.Select(r => r.Notes));
    }

    /// <summary>A sample that is ignored isn't analyzed.</summary>
    [Fact]
    public void AnalyzeSong_WithIgnoredSample_LeavesItOut()
    {
        // Act
        IReadOnlyList<SampleAnalysis> some = SampleAnalyzer.AnalyzeSong(ThreeSampleSong(), ThreeSamplePlayback(), new HashSet<int> { 2 });

        // Assert
        Assert.Equal([0], some.Select(a => a.Sample));
    }

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

    /// <summary>A song with a looped sine, an empty slot and a shorter looped sine.</summary>
    private static Song ThreeSampleSong() => new(
        "song",
        [new Channel(-1), new Channel(1)],
        [0],
        null,
        [new Pattern([new Row([new Cell(null, null, null, []), new Cell(null, null, null, [])])])],
        [new Instrument(0, 1, 0), new Instrument(1, 1, 0), new Instrument(2, 1, 0)],
        [LoopedSine(64), new Sample("empty", [], null), LoopedSine(32)],
        new Timing(6, 125));

    /// <summary>Sample 0 started at C-2 and C-4; sample 2 started at C-4 and swapped in at C-2 for two ticks.</summary>
    private static Playback ThreeSamplePlayback() => new(
        [
            new PlaybackTick(0, 0.02, 0, 0, 0, false, [Start(0, C2), Start(2, C4)]),
            new PlaybackTick(0.02, 0.02, 0, 0, 1, false, [Start(0, C4), Idle()]),
            new PlaybackTick(0.04, 0.02, 0, 0, 2, false, [Swapped(2, C2), Idle()]),
            new PlaybackTick(0.06, 0.02, 0, 0, 3, false, [Swapped(2, C2), Idle()]),
        ],
        null,
        PlaybackEnd.Stops);

    /// <summary>A channel starting <paramref name="sample"/> at <paramref name="rate"/>.</summary>
    private static ChannelTick Start(int sample, double rate) =>
        new(true, new NotePitch(36, 0, rate), 1, 0, new SampleBlock(sample, 0, 64), new SampleBlock(sample, 0, 64));

    /// <summary>A channel playing nothing.</summary>
    private static ChannelTick Idle() => new(false, default, 0, 0, null, new SampleBlock(null, 0, 0));

    /// <summary>A channel whose next part is <paramref name="sample"/>, with no note.</summary>
    private static ChannelTick Swapped(int sample, double rate) =>
        new(true, new NotePitch(36, 0, rate), 1, 0, null, new SampleBlock(sample, 0, 32));
}
