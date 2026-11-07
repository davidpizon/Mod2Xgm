using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="CapacityCheck"/>: what a song's recordings need and what happens when they don't fit (plan T9.2).</summary>
public sealed class CapacityCheckTests
{
    /// <summary>A rate that plays 100 frames per 20 ms tick.</summary>
    private const double Rate = 5000;

    /// <summary>A tick's length, in seconds.</summary>
    private const double TickSeconds = 0.02;

    /// <summary>The default PCM budget, 128 KB.</summary>
    private const long Budget = 128 * 1024;

    /// <summary>A different pitch, loudness or start point each take a stored copy; playing one again doesn't.</summary>
    [Fact]
    public void Measure_WithDifferentNotes_CountsOneSlotPerPitchLoudnessAndStart()
    {
        // Arrange
        Song song = SongOf(OneShot(1000));
        Playback playback = PlaybackOf(
            [Start(0)],
            [Start(0)],
            [Start(0, rate: 2 * Rate)],
            [Start(0, volume: 0.5)],
            [Start(0, frame: 10)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(4, sut.Slots);
    }

    /// <summary>A sample the mapper doesn't play as a recording stores nothing and takes no voice.</summary>
    [Fact]
    public void Measure_WithSampleNotPlayedAsRecording_CountsNothing()
    {
        // Arrange
        Song song = SongOf(OneShot(1000));
        Playback playback = PlaybackOf([Start(0)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal((0, 0, 0L), (sut.Slots, sut.Voices, sut.Bytes));
    }

    /// <summary>Notes too quiet for a copy and notes starting past the sample's end are left silent, so they store nothing.</summary>
    [Fact]
    public void Measure_WithSilentNotes_CountsNoSlots()
    {
        // Arrange
        Song song = SongOf(OneShot(1000));
        Playback playback = PlaybackOf([Start(0, volume: 0)], [Start(0, volume: 0.001)], [Start(0, frame: 1000)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(0, sut.Slots);
    }

    /// <summary>Two samples with exactly the same sound share their copies (consolidation step 2).</summary>
    [Fact]
    public void Measure_WithIdenticalSamples_SharesTheCopy()
    {
        // Arrange
        Song song = SongOf(OneShot(1000), OneShot(1000));
        Playback playback = PlaybackOf([Start(0)], [Start(1)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0, 1), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(1, sut.Slots);
    }

    /// <summary>A sample with another sound takes its own copy, even at the same length.</summary>
    [Fact]
    public void Measure_WithDifferentSamples_KeepsTheirCopiesApart()
    {
        // Arrange
        Song song = SongOf(OneShot(1000), OneShot(1000, level: 0.25f));
        Playback playback = PlaybackOf([Start(0)], [Start(1)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0, 1), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(2, sut.Slots);
    }

    /// <summary>A kick still ringing when a snare starts on another channel counts as two at once, though they started apart (David).</summary>
    [Fact]
    public void Measure_WithRecordingStillRinging_CountsTwoVoices()
    {
        // Arrange
        Song song = SongOf(OneShot(1000), OneShot(1000, level: 0.25f));
        Playback playback = PlaybackOf([Start(0), Idle()], [Hold(), Start(1)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0, 1), song, playback, Limits(PcmTarget.Dmf));

        // Assert
        Assert.Equal(2, sut.Voices);
    }

    /// <summary>A kick that has played out before the snare starts leaves one voice at a time.</summary>
    [Fact]
    public void Measure_WithRecordingPlayedOut_CountsOneVoice()
    {
        // Arrange
        Song song = SongOf(OneShot(50), OneShot(50, level: 0.25f));
        Playback playback = PlaybackOf([Start(0, frames: 50), Idle()], [Hold(), Idle()], [Hold(), Start(1, frames: 50)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0, 1), song, playback, Limits(PcmTarget.Dmf));

        // Assert
        Assert.Equal(1, sut.Voices);
    }

    /// <summary>A one-shot's silent tail costs nothing, and XGM stores in 256-byte blocks.</summary>
    [Fact]
    public void Measure_WithSilentTail_CountsOnlyTheSoundInBlocks()
    {
        // Arrange
        double rate = PcmTargets.PlayRateHz(PcmTarget.Xgm, 0, halfSpeed: false);
        float[] data = new float[3000];
        Array.Fill(data, 0.5f, 0, 1000);
        Song song = SongOf(new Sample("shot", data, null));
        Playback playback = PlaybackOf([Start(0, rate: rate, frames: 3000)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(1024, sut.Bytes);
    }

    /// <summary>A short and a long note of one looped sound share one copy, as long as the long one: a note can be stopped early.</summary>
    [Fact]
    public void Measure_WithLoopHeldForDifferentLengths_StoresOneCopyAsLongAsTheLongest()
    {
        // Arrange
        double rate = PcmTargets.PlayRateHz(PcmTarget.Xgm, 0, halfSpeed: false);
        Song song = SongOf(new Sample("loop", Sound(100, 0.5f), new SampleLoop(0, 100)));
        Playback playback = PlaybackOf([Start(0, rate: rate)], [Start(0, rate: rate)], [Hold(rate)], [Hold(rate)]);

        // Act
        CapacityCheck sut = CapacityCheck.Measure(PlanOf(0), song, playback, Limits(PcmTarget.Xgm));

        // Assert
        Assert.Equal(1, sut.Slots);
        Assert.Equal(PcmTargets.Bytes(PcmTarget.Xgm, (int)Math.Ceiling(3 * TickSeconds * rate) + 1), sut.Bytes);
    }

    /// <summary>Exactly the limit fits; one more doesn't. Each target has its own slot limit (docs/sample-capacity.md#limits-per-target).</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Dmf), 144, true)]
    [InlineData(nameof(PcmTarget.Dmf), 145, false)]
    [InlineData(nameof(PcmTarget.Xgm), 63, true)]
    [InlineData(nameof(PcmTarget.Xgm), 64, false)]
    [InlineData(nameof(PcmTarget.Xgm2), 123, true)]
    [InlineData(nameof(PcmTarget.Xgm2), 124, false)]
    public void Fits_WithSlots_ComparesWithTheTargetsLimit(string target, int slots, bool expected)
    {
        // Arrange
        CapacityCheck sut = new(Limits(Enum.Parse<PcmTarget>(target)), slots, 0, 0);

        // Act
        bool fits = sut.Fits;

        // Assert
        Assert.Equal(expected, fits);
    }

    /// <summary>Each target has its own voice limit, lowered by the voices left to the game.</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Dmf), 0, 1, true)]
    [InlineData(nameof(PcmTarget.Dmf), 0, 2, false)]
    [InlineData(nameof(PcmTarget.Xgm), 0, 4, true)]
    [InlineData(nameof(PcmTarget.Xgm), 0, 5, false)]
    [InlineData(nameof(PcmTarget.Xgm2), 0, 3, true)]
    [InlineData(nameof(PcmTarget.Xgm2), 0, 4, false)]
    [InlineData(nameof(PcmTarget.Xgm2), 1, 3, false)]
    public void Fits_WithVoices_ComparesWithTheTargetsLimit(string target, int reserved, int voices, bool expected)
    {
        // Arrange
        CapacityCheck sut = new(TargetLimits.For(Enum.Parse<PcmTarget>(target), Budget, reserved), 0, voices, 0);

        // Act
        bool fits = sut.Fits;

        // Assert
        Assert.Equal(expected, fits);
    }

    /// <summary>XGM and XGM2 have a byte budget; a DMF has none (docs/adr/0085-target-limits.md).</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Dmf), Budget + 1, true)]
    [InlineData(nameof(PcmTarget.Xgm), Budget, true)]
    [InlineData(nameof(PcmTarget.Xgm), Budget + 1, false)]
    [InlineData(nameof(PcmTarget.Xgm2), Budget + 1, false)]
    public void Fits_WithBytes_ComparesWithTheTargetsBudget(string target, long bytes, bool expected)
    {
        // Arrange
        CapacityCheck sut = new(Limits(Enum.Parse<PcmTarget>(target)), 0, 0, bytes);

        // Act
        bool fits = sut.Fits;

        // Assert
        Assert.Equal(expected, fits);
    }

    /// <summary>The report names the target and every limit passed, as docs/sample-capacity.md shows it.</summary>
    [Fact]
    public void Report_WithEveryLimitPassed_ListsThemAll()
    {
        // Arrange
        CapacityCheck sut = new(Limits(PcmTarget.Xgm2), 187, 4, 412 * 1024);

        // Act
        string report = sut.Report();

        // Assert
        Assert.Equal("XGM2 capacity exceeded: needs 187 sample slots (limit 123), 4 simultaneous PCM voices (limit 3), 412 KB PCM (budget 128 KB)", report);
    }

    /// <summary>The report leaves out what fits.</summary>
    [Fact]
    public void Report_WithOneLimitPassed_ListsOnlyThatOne()
    {
        // Arrange
        CapacityCheck sut = new(Limits(PcmTarget.Dmf), 3, 2, 412 * 1024);

        // Act
        string report = sut.Report();

        // Assert
        Assert.Equal("DMF capacity exceeded: needs 2 simultaneous PCM voices (limit 1)", report);
    }

    /// <summary>A song that fits gets no warning, with or without <c>--no-consolidate</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enforce_WhenItFits_SaysNothing(bool noConsolidate)
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        CapacityCheck sut = new(Limits(PcmTarget.Xgm), 1, 1, 1);

        // Act
        sut.Enforce(noConsolidate, notes);

        // Assert
        Assert.Equal(0, notes.Count);
    }

    /// <summary>By default a song that doesn't fit gets the capacity warning, then goes on to consolidate.</summary>
    [Fact]
    public void Enforce_WhenItDoesNotFit_WarnsAndGoesOn()
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        CapacityCheck sut = new(Limits(PcmTarget.Dmf), 1, 2, 1);

        // Act
        sut.Enforce(noConsolidate: false, notes);

        // Assert
        Assert.Equal(
            ["DMF capacity exceeded: needs 2 simultaneous PCM voices (limit 1)", "Consolidating samples to fit (use --no-consolidate to fail instead)"],
            sink.Messages(LogEventLevel.Warning));
    }

    /// <summary>With <c>--no-consolidate</c> a song that doesn't fit stops with exit 6, its message saying what it needs.</summary>
    [Fact]
    public void Enforce_WithNoConsolidateWhenItDoesNotFit_ThrowsDoesNotFit()
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        CapacityCheck sut = new(Limits(PcmTarget.Xgm), 64, 1, 1);

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => sut.Enforce(noConsolidate: true, notes));

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, error.Code);
        Assert.Contains("needs 64 sample slots (limit 63)", error.Message, StringComparison.Ordinal);
        Assert.Equal(["XGM capacity exceeded: needs 64 sample slots (limit 63)"], sink.Messages(LogEventLevel.Warning));
    }

    /// <summary>The limits of <paramref name="target"/> with the default budget and no voices reserved.</summary>
    private static TargetLimits Limits(PcmTarget target) => TargetLimits.For(target, Budget, 0);

    /// <summary>A plan that plays <paramref name="recordings"/> as recordings.</summary>
    private static GenesisPlan PlanOf(params int[] recordings) =>
        new("song", [], null, new Timing(6, 125), [], []) { PcmSamples = recordings.ToHashSet() };

    /// <summary>A two-channel song with these samples.</summary>
    private static Song SongOf(params Sample[] samples) =>
        new("song", [new Channel(-1), new Channel(1)], [0], null, [new Pattern([new Row([Cell.Empty, Cell.Empty])])], [], samples, new Timing(6, 125));

    /// <summary>A one-shot of <paramref name="frames"/> frames at a steady level.</summary>
    private static Sample OneShot(int frames, float level = 0.5f) => new("shot", Sound(frames, level), null);

    /// <summary><paramref name="frames"/> frames at a steady level.</summary>
    private static float[] Sound(int frames, float level)
    {
        float[] data = new float[frames];
        Array.Fill(data, level);
        return data;
    }

    /// <summary>A song played tick by tick: each array is one tick's channels, short ones filled in with idle channels.</summary>
    private static Playback PlaybackOf(params ChannelTick[][] ticks)
    {
        PlaybackTick[] played = new PlaybackTick[ticks.Length];
        for (int t = 0; t < ticks.Length; t++)
        {
            ChannelTick[] channels = ticks[t].Length == 2 ? ticks[t] : [ticks[t][0], Idle()];
            played[t] = new PlaybackTick(t * TickSeconds, TickSeconds, 0, t, 0, false, channels);
        }

        return new Playback(played, null, PlaybackEnd.Stops);
    }

    /// <summary>A channel starting a note of <paramref name="sample"/>; by default long enough to ring through the test, and ending there.</summary>
    private static ChannelTick Start(int sample, double rate = Rate, double volume = 1, int frame = 0, int frames = 1000) =>
        new(true, new NotePitch(36, 0, rate), volume, 0, new SampleBlock(sample, frame, frames), new SampleBlock(sample, 0, 2));

    /// <summary>A channel holding its note.</summary>
    private static ChannelTick Hold(double rate = Rate) => new(true, new NotePitch(36, 0, rate), 1, 0, null, new SampleBlock(0, 0, 100));

    /// <summary>A channel that hasn't started.</summary>
    private static ChannelTick Idle() => new(false, new NotePitch(36, 0, Rate), 0, 0, null, new SampleBlock(null, 0, 0));
}
