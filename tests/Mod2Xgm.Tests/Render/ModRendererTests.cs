using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Render;

/// <summary>
/// Tests for the in-app MOD renderer (plan T7.1): OpenMPT's level, panning, volume ramps, note crossfade and click removal
/// as measured on libopenmpt (docs/adr/0054-mod-renderer.md), pitch from the playback rate, sample parts and timing.
/// </summary>
public sealed class ModRendererTests
{
    private const int Rate = 44_100;

    /// <summary>Full scale at full volume on one side: channel gain 0.5 × OpenMPT's 75 % pan.</summary>
    private const double FullLeft = 0.375;

    /// <summary>The other side gets 25 %.</summary>
    private const double FullRight = 0.125;

    /// <summary>A sample of <paramref name="frames"/> frames all at <paramref name="value"/>, looped or not.</summary>
    private static Sample Constant(int frames, float value = 1f, bool loops = true) =>
        new("dc", [.. Enumerable.Repeat(value, frames)], loops ? new SampleLoop(0, frames) : null);

    /// <summary>A song holding only <paramref name="samples"/> (the renderer reads nothing else).</summary>
    private static Song SongOf(params Sample[] samples) =>
        new("test", [new Channel(-1)], [], null, [], [], samples, new Timing(6, 125));

    /// <summary>One channel's state on a tick.</summary>
    private static ChannelTick Channel(double volume, SampleBlock next, SampleBlock? started = null, double rateHz = Rate, double pan = -1, bool playing = true) =>
        new(playing, new NotePitch(0, 0, rateHz), volume, pan, started, next);

    /// <summary>A playback of one-channel ticks, each <paramref name="seconds"/> long.</summary>
    private static Playback PlaybackOf(double seconds, params ChannelTick[] ticks) =>
        new([.. ticks.Select((t, i) => new PlaybackTick(i * seconds, seconds, 0, i, 0, false, [t]))], null, PlaybackEnd.Stops);

    /// <summary>The whole of sample 0, <paramref name="frames"/> long.</summary>
    private static SampleBlock Whole(int frames, int sample = 0) => new(sample, 0, frames);

    /// <summary>The 2-frame part at a sample's start that ends a one-shot sample.</summary>
    private static SampleBlock OneShotEnd(int sample = 0) => new(sample, 0, 2);

    /// <summary>A constant sample at full volume plays at libopenmpt's level, panned 75 % / 25 %; separation 1 is hard left.</summary>
    [Fact]
    public void Render_WithNote_MatchesOpenMptsLevelAndPan()
    {
        // Arrange
        Song song = SongOf(Constant(1000));
        Playback playback = PlaybackOf(0.01, Channel(1, Whole(1000), Whole(1000)));
        StereoAudio audio = ModRenderer.Render(song, playback);
        StereoAudio hard = ModRenderer.Render(song, playback, new ModRenderOptions(StereoSeparation: 1));

        // Act
        StereoAudio mono = ModRenderer.Render(song, PlaybackOf(0.01, Channel(1, Whole(1000), Whole(1000), pan: 1)), new ModRenderOptions(StereoSeparation: 0));

        // Assert
        Assert.Equal(441, audio.Length);
        Assert.Equal(FullLeft, audio.Left[100], 6);
        Assert.Equal(FullRight, audio.Right[100], 6);
        Assert.Equal(0.5, hard.Left[100], 6);
        Assert.Equal(0, hard.Right[100], 6);
        Assert.Equal(0.25, mono.Left[100], 6);
        Assert.Equal(0.25, mono.Right[100], 6);
    }

    /// <summary>A note fades in over 16 samples at 44.1 kHz; a lower volume is reached over 42 (OpenMPT's default ramps).</summary>
    [Fact]
    public void Render_WithVolumeChange_RampsIt()
    {
        // Arrange
        Song song = SongOf(Constant(10_000));
        Playback playback = PlaybackOf(0.01, Channel(1, Whole(10_000), Whole(10_000)), Channel(0.5, Whole(10_000)));

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback);

        // Assert
        // From the fourth sample on, the filter reads no silence from before the note's start.
        Assert.Equal(FullLeft * 4 / 16, audio.Left[3], 6);
        Assert.Equal(FullLeft * 8 / 16, audio.Left[7], 6);
        Assert.Equal(FullLeft, audio.Left[15], 6);
        Assert.Equal(FullLeft - (FullLeft / 2 / 42), audio.Left[441], 6);
        Assert.Equal(FullLeft / 2, audio.Left[441 + 41], 6);
        Assert.Equal(FullLeft / 2, audio.Left[600], 6);
    }

    /// <summary>A new note on a playing channel fades the old one out over 42 samples while the new one fades in over 16.</summary>
    [Fact]
    public void Render_WithNewNote_CrossfadesOldOne()
    {
        // Arrange
        Song song = SongOf(Constant(10_000));
        Playback playback = PlaybackOf(0.01, Channel(1, Whole(10_000), Whole(10_000)), Channel(0.5, Whole(10_000), Whole(10_000)));

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback);

        // Assert
        // Sample 16 of the new note: old at 1 − 16/42, new at full.
        Assert.Equal((FullLeft * (1 - (16.0 / 42))) + (FullLeft / 2), audio.Left[441 + 15], 6);
        Assert.True(audio.Left[441 + 15] > FullLeft);
        Assert.Equal(FullLeft / 2, audio.Left[441 + 41], 6);
        Assert.Equal(FullLeft / 2, audio.Left[800], 6);
    }

    /// <summary>
    /// Where a one-shot sample ends, its last value stays as an offset that shrinks by 1/256 per sample (OpenMPT's click
    /// removal) instead of jumping to 0.
    /// </summary>
    [Fact]
    public void Render_WithEndingSample_FadesLastValue()
    {
        // Arrange
        Song song = SongOf(Constant(100, loops: false));
        Playback playback = PlaybackOf(0.01, Channel(1, OneShotEnd(), Whole(100)));

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback);

        // Assert
        Assert.Equal(FullLeft, audio.Left[90], 6);
        int start = Enumerable.Range(100, 20).First(n => audio.Left[n] < audio.Left[n - 1] && audio.Left[n + 1] < audio.Left[n] && Math.Abs((audio.Left[n + 1] / audio.Left[n]) - (255.0 / 256)) < 1e-4);
        Assert.InRange(audio.Left[start], FullLeft * 0.9, FullLeft);
        Assert.Equal(audio.Left[start] * Math.Pow(255.0 / 256, 200), audio.Left[start + 200], 5);
    }

    /// <summary>A sample plays at the playback rate: a 100-frame cycle at 22 050 Hz is a 220.5 Hz tone.</summary>
    [Fact]
    public void Render_WithPlaybackRate_FollowsPitch()
    {
        // Arrange
        Sample sine = new("sine", [.. Enumerable.Range(0, 100).Select(i => (float)Math.Sin(2 * Math.PI * i / 100))], new SampleLoop(0, 100));
        Playback playback = PlaybackOf(1.0, Channel(1, Whole(100), Whole(100), rateHz: 22_050));

        // Act
        StereoAudio audio = ModRenderer.Render(SongOf(sine), playback);

        // Assert
        int rises = Enumerable.Range(1, audio.Length - 1).Count(n => audio.Left[n - 1] < 0 && audio.Left[n] >= 0);
        Assert.InRange(rises, 219, 221);
    }

    /// <summary>Played much faster than the output rate, a tone at the source's top frequency is filtered away, not aliased.</summary>
    [Fact]
    public void Render_WhenDownsampling_FiltersHighTones()
    {
        // Arrange
        Sample nyquist = new("buzz", [.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 1f : -1f)], new SampleLoop(0, 1000));
        Playback playback = PlaybackOf(0.5, Channel(1, Whole(1000), Whole(1000), rateHz: 30_000));

        // Act
        StereoAudio audio = ModRenderer.Render(SongOf(nyquist), playback, new ModRenderOptions(8000));

        // Assert
        double rms = Math.Sqrt(audio.Left.Skip(100).Average(x => (double)x * x));
        Assert.True(rms < 0.05 * FullLeft, FormattableString.Invariant($"RMS {rms}"));
    }

    /// <summary>A looped part repeats after the part that started, and a new next part (a sample swap) takes over when the current one ends.</summary>
    [Fact]
    public void Render_WithNextPart_FollowsStartedOne()
    {
        // Arrange
        Sample first = Constant(10);
        Sample second = Constant(10, 0.5f);
        Playback playback = PlaybackOf(0.001, Channel(1, Whole(10), Whole(10), pan: 1), Channel(1, Whole(10, sample: 1), pan: 1));

        // Act
        StereoAudio audio = ModRenderer.Render(SongOf(first, second), playback, new ModRenderOptions(StereoSeparation: 1));

        // Assert
        Assert.Equal(0.5, audio.Right[30], 5);
        Assert.Equal(0.25, audio.Right[80], 5);
    }

    /// <summary>A one-shot sample that has ended starts again when a sample swap gives the channel a looped part (ProTracker).</summary>
    [Fact]
    public void Render_WithSampleSwap_RestartsEndedChannel()
    {
        // Arrange
        Sample shot = Constant(20, loops: false);
        Sample loop = Constant(10, 0.5f);
        ChannelTick quiet = Channel(1, OneShotEnd());
        Playback playback = PlaybackOf(0.01, Channel(1, OneShotEnd(), Whole(20)), quiet, quiet, quiet, Channel(1, Whole(10, sample: 1)));

        // Act
        StereoAudio audio = ModRenderer.Render(SongOf(shot, loop), playback);

        // Assert
        Assert.InRange(audio.Left[1700], 0, 0.001);
        Assert.Equal(FullLeft / 2, audio.Left[4 * 441 + 100], 3);
    }

    /// <summary>An empty sample slot, or a channel that isn't playing, is silent.</summary>
    [Fact]
    public void Render_WithEmptySamplesAndIdleChannels_IsSilent()
    {
        // Arrange
        Song song = SongOf(Constant(100));
        SampleBlock none = new(null, 0, 0);
        Playback playback = PlaybackOf(0.01, Channel(1, none, none), Channel(1, Whole(100), Whole(100), playing: false), Channel(0, Whole(100)));

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback);

        // Assert
        Assert.All(audio.Left, s => Assert.Equal(0f, s));
        Assert.All(audio.Right, s => Assert.Equal(0f, s));
    }

    /// <summary>Tick lengths can be given instead of the playback's own; the render ends with the last tick.</summary>
    [Fact]
    public void Render_WithTickLengths_UsesThem()
    {
        // Arrange
        Song song = SongOf(Constant(100));
        Playback playback = PlaybackOf(0.01, Channel(1, Whole(100), Whole(100)), Channel(1, Whole(100)));

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback, new ModRenderOptions(1000) { TickDurations = [0.02, 0.0306] });

        // Assert
        Assert.Equal(51, audio.Length);
        Assert.Equal(1000, audio.SampleRate);
        Assert.Equal(0, ModRenderer.Render(song, new Playback([], null, PlaybackEnd.Stops)).Length);
    }

    /// <summary>Options that don't fit are refused.</summary>
    [Fact]
    public void Render_WithBadOptions_Throws()
    {
        // Arrange
        Song song = SongOf(Constant(100));

        // Act
        Playback playback = PlaybackOf(0.01, Channel(1, Whole(100), Whole(100)));

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ModRenderer.Render(song, playback, new ModRenderOptions(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModRenderer.Render(song, playback, new ModRenderOptions(StereoSeparation: 1.5)));
        Assert.Throws<ArgumentException>(() => ModRenderer.Render(song, playback, new ModRenderOptions { TickDurations = [0.01, 0.01] }));
        Assert.Throws<ArgumentException>(() => ModRenderer.Render(song, playback, new ModRenderOptions { TickDurations = [-1] }));
        // null! on purpose: the guard clauses are what is tested.
        Assert.Throws<ArgumentNullException>(() => ModRenderer.Render(null!, playback));
        Assert.Throws<ArgumentNullException>(() => ModRenderer.Render(song, null!));
    }

    /// <summary>A MOD read, played by the tick player and rendered: as long as the playback, and it makes sound.</summary>
    [Fact]
    public void Render_WithMod_PlaysThroughTickPlayer()
    {
        // Arrange
        ModBuilder builder = new ModBuilder()
            .WithSample(1, "saw", 512, loopStart: 0, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 4, channel: 1, sample: 1, period: 214);
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(builder.Build(), notes);
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, notes);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, notes);

        // Act
        StereoAudio audio = ModRenderer.Render(song, playback, new ModRenderOptions(22_050));

        // Assert
        Assert.Equal((int)Math.Round(playback.DurationSeconds * 22_050), audio.Length);
        Assert.Contains(audio.Left, s => Math.Abs(s) > 0.05);
        Assert.Contains(audio.Right, s => Math.Abs(s) > 0.05);
    }

    /// <summary>The interpolation tables, by name, for the theories below.</summary>
    private static readonly Dictionary<string, SincTable> SincTables = new()
    {
        [nameof(SincTable.Normal)] = SincTable.Normal,
        [nameof(SincTable.Downsample13X)] = SincTable.Downsample13X,
        [nameof(SincTable.Downsample2X)] = SincTable.Downsample2X,
    };

    /// <summary>Every interpolation filter sums to 1, so it keeps the level.</summary>
    [Theory]
    [InlineData(nameof(SincTable.Normal), 0)]
    [InlineData(nameof(SincTable.Normal), 0.25)]
    [InlineData(nameof(SincTable.Normal), 0.5)]
    [InlineData(nameof(SincTable.Normal), 0.999)]
    [InlineData(nameof(SincTable.Downsample13X), 0)]
    [InlineData(nameof(SincTable.Downsample13X), 0.25)]
    [InlineData(nameof(SincTable.Downsample13X), 0.5)]
    [InlineData(nameof(SincTable.Downsample13X), 0.999)]
    [InlineData(nameof(SincTable.Downsample2X), 0)]
    [InlineData(nameof(SincTable.Downsample2X), 0.25)]
    [InlineData(nameof(SincTable.Downsample2X), 0.5)]
    [InlineData(nameof(SincTable.Downsample2X), 0.999)]
    public void Filter_WithFraction_SumsToOne(string table, double fraction)
    {
        // Arrange
        SincTable sut = SincTables[table];

        // Act
        float[] taps = sut.Filter(fraction).ToArray();

        // Assert
        Assert.Equal(1, taps.Sum(tap => (double)tap), 5);
    }

    /// <summary>At a whole position the normal filter leans almost all on the current sample.</summary>
    [Fact]
    public void Filter_AtWholePosition_ReadsCurrentSample()
    {
        // Act
        // A cutoff just below the source's Nyquist frequency, so a whole position leans almost all on the current sample.
        float[] whole = SincTable.Normal.Filter(0).ToArray();

        // Assert
        Assert.InRange(whole[SincTable.TapsBefore], 0.95f, 1f);
        Assert.All(whole.Where((_, i) => i != SincTable.TapsBefore), tap => Assert.InRange(tap, -0.05f, 0.05f));
    }

    /// <summary>The faster a sample plays, the lower the table's cutoff.</summary>
    [Theory]
    [InlineData(0.7, nameof(SincTable.Normal))]
    [InlineData(1.2, nameof(SincTable.Downsample13X))]
    [InlineData(1.5, nameof(SincTable.Downsample13X))]
    [InlineData(3, nameof(SincTable.Downsample2X))]
    public void For_WithStep_PicksTable(double step, string expected)
    {
        // Act
        SincTable table = SincTable.For(step);

        // Assert
        Assert.Same(SincTables[expected], table);
    }
}
