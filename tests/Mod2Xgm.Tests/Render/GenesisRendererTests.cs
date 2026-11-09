using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Serilog.Core;

namespace Mod2Xgm.Tests.Render;

/// <summary>
/// Tests for the Genesis renderer (plan T7.5, docs/adr/0059-genesis-renderer.md): Furnace's levels for FM, the DAC and the
/// PSG; pitch, volume and pan from the playback; notes on the tick their sample starts, restarts and slides; note-offs;
/// every PCM driver; the noise following its sample; rates and argument checks.
/// </summary>
public sealed class GenesisRendererTests
{
    /// <summary>Furnace's RMS of a TL-0 sine carrier at full volume, and of a full-scale sine on the DAC (ADR 0050).</summary>
    private const double FurnaceFmDb = -11.41;

    /// <summary>Furnace's RMS of a PSG square at the loudest volume (ADR 0050).</summary>
    private const double FurnacePsgSquareDb = -20.68;

    /// <summary>One tick: 20 ms, PAL at 125 BPM.</summary>
    private const double TickSeconds = 0.02;

    private const int TicksPerRow = 6;

    /// <summary>Plays a 64-frame cycle at 440 Hz.</summary>
    private const double SineRateHz = 64 * 440;

    /// <summary>Renders at the chip's own rate, with no resampling.</summary>
    private static readonly GenesisRenderOptions ChipRate = new(SampleRate: null);

    /// <summary>A looped full-scale sine of one <paramref name="period"/>-frame cycle.</summary>
    private static Sample LoopedSine(int period = 64) =>
        new("sine", [.. Enumerable.Range(0, period).Select(i => (float)Math.Sin(2 * Math.PI * i / period))], new SampleLoop(0, period));

    /// <summary>A one-shot full-scale 440 Hz sine recorded at <paramref name="rateHz"/>, <paramref name="seconds"/> long.</summary>
    private static Sample OneShotSine(int rateHz, double seconds) =>
        new("dac", [.. Enumerable.Range(0, (int)(rateHz * seconds)).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / rateHz))], null);

    /// <summary>A song with <paramref name="channels"/> centred channels and <paramref name="samples"/> (the renderer reads nothing else).</summary>
    private static Song SongOf(int channels, params Sample[] samples) =>
        new("test", [.. Enumerable.Range(0, channels).Select(_ => new Channel(0))], [], null, [], [], samples, new Timing(TicksPerRow, 125));

    /// <summary>A note of song channel 0, sample 0, at full volume.</summary>
    private static PlanCell Note(VoiceKind kind, int index = 0, double rateHz = SineRateHz, params Effect[] effects) =>
        new(new NotePitch(48, 0, rateHz), new PlanVoice(kind, index), 1, false, effects, Source: 0, Sample: 0);

    /// <summary>A note-off on a Genesis channel.</summary>
    private static PlanCell NoteOff { get; } = PlanCell.Empty with { NoteOff = true };

    /// <summary>A plan row with <paramref name="cells"/> on their Genesis channels and nothing elsewhere.</summary>
    private static PlanRow Row(params (GenesisChannel Channel, PlanCell Cell)[] cells)
    {
        PlanCell[] row = [.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)];
        foreach ((GenesisChannel channel, PlanCell cell) in cells)
        {
            row[(int)channel] = cell;
        }

        return new PlanRow(row, []);
    }

    /// <summary>A one-order plan of <paramref name="rows"/>.</summary>
    private static GenesisPlan PlanOf(IReadOnlyList<FmPatch> patches, IReadOnlyList<DacSample> dacs, params PlanRow[] rows) =>
        new("test", [rows], null, new Timing(TicksPerRow, 125), patches, dacs);

    /// <summary>
    /// <paramref name="rows"/> rows of one channel's playback, <see cref="TicksPerRow"/> ticks each: <paramref name="channel"/>
    /// gives the channel's state on each (row, tick).
    /// </summary>
    private static Playback PlaybackOf(int rows, Func<int, int, ChannelTick> channel) =>
        new(
            [.. Enumerable.Range(0, rows * TicksPerRow).Select(i => new PlaybackTick(i * TickSeconds, TickSeconds, 0, i / TicksPerRow, i % TicksPerRow, false, [channel(i / TicksPerRow, i % TicksPerRow)]))],
            null,
            PlaybackEnd.Stops);

    /// <summary>A channel playing sample 0 at <paramref name="rateHz"/>; <paramref name="start"/> starts it from frame 0 on this tick.</summary>
    private static ChannelTick Playing(bool start, double volume = 1, double pan = 0, double rateHz = SineRateHz, int frames = 64) =>
        new(true, new NotePitch(48, 0, rateHz), volume, pan, start ? new SampleBlock(0, 0, frames) : null, new SampleBlock(0, 0, frames));

    private static StereoAudio Render(GenesisPlan plan, Song song, Playback playback, GenesisRenderOptions? options = null) =>
        GenesisRenderer.Render(plan, song, playback, options ?? ChipRate, new ConversionNotes(Logger.None));

    /// <summary>How often a signal crosses zero going up: a measure of a sine's pitch.</summary>
    private static int Crossings(float[] data) => Enumerable.Range(1, data.Length - 1).Count(i => data[i - 1] < 0 && data[i] >= 0);

    /// <summary>The RMS of <paramref name="data"/> from <paramref name="fromSeconds"/> to <paramref name="toSeconds"/>, in dBFS.</summary>
    private static double Db(float[] data, int rate, double fromSeconds, double toSeconds)
    {
        int from = (int)(fromSeconds * rate);
        int to = Math.Min(data.Length, (int)(toSeconds * rate));
        double sum = 0;
        for (int i = from; i < to; i++)
        {
            sum += (double)data[i] * data[i];
        }

        return 10 * Math.Log10((sum / Math.Max(1, to - from)) + 1e-30);
    }

    /// <summary>Upward zero crossings per second from <paramref name="fromSeconds"/> to <paramref name="toSeconds"/>: the frequency of a clean tone.</summary>
    private static double Hz(float[] data, int rate, double fromSeconds, double toSeconds)
    {
        int crossings = 0;
        for (int i = (int)(fromSeconds * rate) + 1; i < (int)(toSeconds * rate); i++)
        {
            crossings += data[i - 1] < 0 && data[i] >= 0 ? 1 : 0;
        }

        return crossings / (toSeconds - fromSeconds);
    }

    /// <summary>An FM sine note at full volume plays at Furnace's level for a TL-0 carrier, on both sides, at the sample's measured pitch.</summary>
    [Fact]
    public void Render_WithFmSine_PlaysAtFurnacesLevelAndPitch()
    {
        // Act
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        // Assert
        Assert.Equal(52_781, audio.SampleRate);
        Assert.Equal(FurnaceFmDb, Db(audio.Left, audio.SampleRate, 0.2, 1.1), 0.15);
        Assert.Equal(FurnaceFmDb, Db(audio.Right, audio.SampleRate, 0.2, 1.1), 0.15);
        Assert.Equal(440, Hz(audio.Left, audio.SampleRate, 0.2, 1.2), 2.0);
    }

    /// <summary>Volume and pan follow the song channel tick by tick: half volume is 6 dB down, in TL steps; a pan to the left silences the right.</summary>
    [Fact]
    public void Render_WithPlaybackVolumeAndPan_FollowsThem()
    {
        // Act
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))),
            SongOf(1, LoopedSine()),
            PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, volume: r < 10 ? 1 : 0.5, pan: r < 15 ? 0 : -1)));

        // Assert
        double full = Db(audio.Left, audio.SampleRate, 0.2, 1.1);
        double half = Db(audio.Left, audio.SampleRate, 1.3, 1.7);
        Assert.Equal(-6.0, half - full, 0.4);
        Assert.True(Db(audio.Right, audio.SampleRate, 1.9, 2.3) < -90);
        Assert.Equal(half, Db(audio.Left, audio.SampleRate, 1.9, 2.3), 0.2);
    }

    /// <summary>A pitch change on the song channel (a slide, vibrato, arpeggio) moves the FM note: an octave up doubles it.</summary>
    [Fact]
    public void Render_WithPlaybackPitch_FollowsIt()
    {
        // Act
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))),
            SongOf(1, LoopedSine()),
            PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: r < 10 ? SineRateHz : 2 * SineRateHz)));

        // Assert
        Assert.Equal(880, Hz(audio.Left, audio.SampleRate, 1.3, 2.3), 2.0);
    }

    /// <summary>A note waits for the tick its sample starts on (EDx): silent before tick 3, sounding after.</summary>
    [Fact]
    public void Render_WithNote_StartsOnSamplesTick()
    {
        // Act
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(2, (r, t) => Playing(r == 0 && t == 3)));

        // Assert
        Assert.True(Db(audio.Left, audio.SampleRate, 0, 0.058) < -90);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.07, 0.11) > -15);
    }

    /// <summary>A note-off lets go of the key: the FM note releases and is silent soon after.</summary>
    [Fact]
    public void Render_WithNoteOff_ReleasesNote()
    {
        // Act
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, NoteOff))),
            SongOf(1, LoopedSine()),
            PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        // Assert
        Assert.True(Db(audio.Left, audio.SampleRate, 0.05, 0.11) > -15);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.6, 1.1) < -60);
    }

    /// <summary>
    /// The plan's cut on tick 3 of a row, where the MOD's note falls silent, lets go of the FM key on that tick: the looped
    /// note sounds until then and is silent soon after (docs/adr/0095-voices-stop-with-the-mod-note.md).
    /// </summary>
    [Fact]
    public void Render_WithNoteCut_StopsFmNoteOnItsTick()
    {
        // Arrange
        PlanCell cut = PlanCell.Empty with { Effects = [new NoteCut(3, Ends: true)], Source = 0 };
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, cut)));

        // Act
        StereoAudio sut = Render(plan, SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        // Assert: row 1 starts at 0.12 s, its tick 3 at 0.18 s.
        Assert.True(Db(sut.Left, sut.SampleRate, 0.13, 0.17) > -15);
        Assert.True(Db(sut.Left, sut.SampleRate, 0.6, 1.1) < -60);
    }

    /// <summary>
    /// The MOD's own ECx only sets the volume to 0, which the FM voice follows from the playback, so it doesn't stop the voice:
    /// here the playback keeps the volume up and the note sounds on (a later volume command can bring an ECx note back).
    /// </summary>
    [Fact]
    public void Render_WithModNoteCut_LeavesTheVoiceToTheVolume()
    {
        // Arrange
        PlanCell cut = PlanCell.Empty with { Effects = [new NoteCut(3)], Source = 0 };
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, cut)));

        // Act
        StereoAudio sut = Render(plan, SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        // Assert
        Assert.True(Db(sut.Left, sut.SampleRate, 0.6, 1.1) > -15);
    }

    /// <summary>A note cut stops a recording on its tick too, where it would otherwise play on at the volume it started with.</summary>
    [Fact]
    public void Render_WithNoteCut_StopsRecordingOnItsTick()
    {
        // Arrange
        Sample recording = OneShotSine(8000, 2);
        PlanCell cut = PlanCell.Empty with { Effects = [new NoteCut(3, Ends: true)], Source = 0 };
        GenesisPlan plan = PlanOf([], [new DacSample(0, 8000)], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000))), Row((GenesisChannel.Fm6, cut)));
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: recording.Length));

        // Act
        StereoAudio sut = Render(plan, SongOf(1, recording), playback, ChipRate with { Driver = PcmDriver.Xgm2 });

        // Assert: the driver's buffer delays both the start and the stop by about 33 ms.
        Assert.True(Db(sut.Left, sut.SampleRate, 0.1, 0.2) > -20);
        Assert.True(Db(sut.Left, sut.SampleRate, 0.4, 1.5) < -60);
    }

    /// <summary>E9x starts a decaying note again, so it is loud again after the restart; a tone portamento slides the playing note without one.</summary>
    [Fact]
    public void Render_WithRetriggerAndPortamento_RestartsAndSlides()
    {
        // Arrange
        FmPatch decaying = new("decay", 7, 0, [new FmOperator(1, 0, 0, 0, 31, false, 20, 0, 15, 15), Muted(), Muted(), Muted()]);
        Song song = SongOf(1, LoopedSine());
        Playback playback = PlaybackOf(2, (r, t) => Playing((r == 0 && t == 0) || (r == 1 && t == 3)));
        StereoAudio restarted = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new Retrigger(3))))), song, playback);

        // Act
        StereoAudio slid = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new TonePortamento(null, PitchScale.AmigaPeriod))))), song, PlaybackOf(2, (r, t) => Playing(r == 0 && t == 0)));

        // Assert
        double before = Db(restarted.Left, restarted.SampleRate, 0.17, 0.18);
        double after = Db(restarted.Left, restarted.SampleRate, 0.185, 0.195);
        Assert.True(after > before + 10, $"{before} dB before the restart, {after} dB after");
        Assert.True(Db(slid.Left, slid.SampleRate, 0.185, 0.195) < Db(slid.Left, slid.SampleRate, 0.11, 0.12));
    }

    /// <summary>A patch's SSG-EG plays in the song: a repeating shape keeps a decaying carrier sounding where it would have faded out.</summary>
    [Fact]
    public void Render_WithSsgEg_PlaysIt()
    {
        // Arrange
        FmPatch decaying = new("decay", 7, 0, [new FmOperator(1, 0, 0, 0, 31, false, 20, 0, 15, 15), Muted(), Muted(), Muted()]);
        FmPatch repeating = decaying with { Operators = [decaying.Operators[0] with { SsgEg = 8 }, .. decaying.Operators.Skip(1)] };
        Song song = SongOf(1, LoopedSine());
        Playback playback = PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0));
        StereoAudio once = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), song, playback);

        // Act
        StereoAudio again = Render(PlanOf([repeating], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), song, playback);

        // Assert
        Assert.True(Db(once.Left, once.SampleRate, 0.8, 1.0) < -60);
        Assert.True(Db(again.Left, again.SampleRate, 0.8, 1.0) > -30);
    }

    /// <summary>A note delayed past the end of its row (EDx with x ≥ speed) is left out, as ProTracker plays nothing; the render says so.</summary>
    [Fact]
    public void Render_WithNoteDelayedPastRow_LeavesItOut()
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());

        // Act
        StereoAudio audio = GenesisRenderer.Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new NoteDelay(8))))),
            SongOf(1, LoopedSine()),
            PlaybackOf(2, (_, _) => Playing(false)),
            ChipRate,
            notes);

        // Assert
        Assert.True(Db(audio.Left, audio.SampleRate, 0, 0.24) < -90);
        Assert.Contains(notes.Messages, m => m.Contains("delayed past their row's end", StringComparison.Ordinal));
    }

    /// <summary>Each PCM driver plays a full-scale DAC sine at Furnace's level, panned by the song channel, and stops it on a note-off.</summary>
    [Theory]
    [InlineData(nameof(PcmDriver.Dmf))]
    [InlineData(nameof(PcmDriver.Xgm))]
    [InlineData(nameof(PcmDriver.Xgm2))]
    public void Render_WithEveryDriver_PlaysDacAtFurnacesLevel(string driver)
    {
        // Arrange
        GenesisPlan plan = PlanOf([], [new DacSample(0, 16_000)], [.. Enumerable.Range(0, 8).Select(r => r == 0 ? Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 16_000))) : r == 7 ? Row((GenesisChannel.Fm6, NoteOff)) : Row())]);
        Playback playback = PlaybackOf(12, (r, t) => Playing(r == 0 && t == 0, pan: 1, rateHz: 16_000, frames: 32_000));

        // Act
        StereoAudio audio = Render(plan, SongOf(1, OneShotSine(16_000, 2)), playback, ChipRate with { Driver = Enum.Parse<PcmDriver>(driver) });

        // Assert
        Assert.Equal(FurnaceFmDb, Db(audio.Right, audio.SampleRate, 0.1, 0.8), 0.3);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.1, 0.8) < -90);
        Assert.Equal(440, Hz(audio.Right, audio.SampleRate, 0.1, 0.8), 3.0);
        Assert.True(Db(audio.Right, audio.SampleRate, 0.9, 1.4) < -60);
    }

    /// <summary>The DAC has no volume: a note at half volume plays a copy 6 dB down (the DMF in 3 dB steps, the XGM drivers exactly), and stays there.</summary>
    [Theory]
    [InlineData(nameof(PcmDriver.Dmf))]
    [InlineData(nameof(PcmDriver.Xgm2))]
    public void Render_WithDacNote_PlaysAtStartingVolume(string driver)
    {
        // Arrange
        GenesisPlan plan = PlanOf([], [new DacSample(0, 16_000)], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 16_000))));
        Playback playback = PlaybackOf(12, (r, t) => Playing(r == 0 && t == 0, volume: r == 0 ? 0.5 : 1, rateHz: 16_000, frames: 32_000));

        // Act
        StereoAudio audio = Render(plan, SongOf(1, OneShotSine(16_000, 2)), playback, ChipRate with { Driver = Enum.Parse<PcmDriver>(driver) });

        // Assert
        Assert.Equal(FurnaceFmDb - 6.0, Db(audio.Left, audio.SampleRate, 0.2, 1.2), 0.3);
    }

    /// <summary>White noise at full volume plays at Furnace's PSG level; it follows its sample's loudness, 12 dB down where the sample is.</summary>
    [Fact]
    public void Render_WithNoise_FollowsSamplesLoudness()
    {
        // Arrange
        Random random = new(1);
        float[] data = [.. Enumerable.Range(0, 16_000).Select(i => (float)((random.NextDouble() * 2) - 1) * (i < 8000 ? 1f : 0.25f))];
        Sample noise = new("noise", data, null);
        GenesisPlan plan = PlanOf([], [], Row((GenesisChannel.Psg4, Note(VoiceKind.PsgNoise, rateHz: 8000))));

        // Act
        StereoAudio audio = Render(plan, SongOf(1, noise), PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: 16_000)));

        // Assert
        double loud = Db(audio.Left, audio.SampleRate, 0.1, 0.9);
        Assert.Equal(FurnacePsgSquareDb, loud, 0.6);
        Assert.Equal(-12, Db(audio.Left, audio.SampleRate, 1.1, 1.9) - loud, 1.5);
        Assert.True(Db(audio.Left, audio.SampleRate, 2.1, 2.3) < -60);
        Assert.Equal(loud, Db(audio.Right, audio.SampleRate, 0.1, 0.9), 0.01);
    }

    /// <summary>In a plan tuned by ear, a panned bass note plays on both sides, 3 dB below where it plays on one in a plain plan.</summary>
    [Fact]
    public void Render_WithBassInPlanTunedByEar_PlaysCentredAndQuieter()
    {
        // Arrange: a 64-frame cycle at 110 Hz, panned left.
        const double rate = 64 * 110;
        FmPatch patch = FmPatchLibrary.Sine with { HeardRms = 0.25 };
        GenesisPlan plain = PlanOf([patch], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, rateHz: rate))));
        Song song = SongOf(1, LoopedSine());
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, pan: -1, rateHz: rate));

        // Act
        StereoAudio side = Render(plain, song, playback);
        StereoAudio centred = Render(plain with { TunedByEar = true }, song, playback);

        // Assert
        Assert.True(Db(side.Right, side.SampleRate, 0.2, 1.5) < -60);
        double left = Db(centred.Left, centred.SampleRate, 0.2, 1.5);
        Assert.Equal(left, Db(centred.Right, centred.SampleRate, 0.2, 1.5), 0.01);
        Assert.Equal(-3, left - Db(side.Left, side.SampleRate, 0.2, 1.5), 0.8);
    }

    /// <summary>A PSG tone note plays at its pitch on its tone channel.</summary>
    [Fact]
    public void Render_WithPsgTone_PlaysAtSamplePitch()
    {
        // Arrange
        GenesisPlan plan = PlanOf([], [], Row((GenesisChannel.Psg1, Note(VoiceKind.PsgTone))));

        // Act
        StereoAudio audio = Render(plan, SongOf(1, LoopedSine()), PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0)));

        // Assert
        Assert.Equal(440, Hz(audio.Left, audio.SampleRate, 0.2, 1.0), 5.0);
    }

    /// <summary>A layer's level turns its PSG tone down: a quarter is 12 dB, within what the PSG's 2 dB steps allow.</summary>
    [Fact]
    public void Render_WithLayerLevel_PlaysToneQuieter()
    {
        // Arrange
        GenesisPlan full = PlanOf([], [], Row((GenesisChannel.Psg1, Note(VoiceKind.PsgTone))));
        GenesisPlan quarter = PlanOf([], [], Row((GenesisChannel.Psg1, Note(VoiceKind.PsgTone) with { Level = 0.25 })));
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0));

        // Act
        StereoAudio loud = Render(full, SongOf(1, LoopedSine()), playback);
        StereoAudio quiet = Render(quarter, SongOf(1, LoopedSine()), playback);

        // Assert
        Assert.Equal(-12, Db(quiet.Left, quiet.SampleRate, 0.2, 1.0) - Db(loud.Left, loud.SampleRate, 0.2, 1.0), 1.5);
    }

    /// <summary>A voice's cents move its FM note off the pitch: a whole octave's worth doubles the sine's frequency.</summary>
    [Fact]
    public void Render_WithCents_MovesFmPitch()
    {
        // Arrange
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0));
        GenesisPlan plain = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));
        GenesisPlan octave = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm) with { Cents = 1200 })));

        // Act
        StereoAudio low = Render(plain, SongOf(1, LoopedSine()), playback);
        StereoAudio high = Render(octave, SongOf(1, LoopedSine()), playback);

        // Assert
        Assert.Equal(2.0, (double)Crossings(high.Left) / Crossings(low.Left), 1);
    }

    /// <summary>A voice's level turns its FM note down: half is 6 dB.</summary>
    [Fact]
    public void Render_WithLevel_PlaysFmQuieter()
    {
        // Arrange
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0));
        GenesisPlan full = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));
        GenesisPlan half = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm) with { Level = 0.5 })));

        // Act
        StereoAudio loud = Render(full, SongOf(1, LoopedSine()), playback);
        StereoAudio quiet = Render(half, SongOf(1, LoopedSine()), playback);

        // Assert
        Assert.Equal(-6, Db(quiet.Left, quiet.SampleRate, 0.2, 1.0) - Db(loud.Left, loud.SampleRate, 0.2, 1.0), 1.0);
    }

    /// <summary>Several recordings at once play on the PCM columns of a plan with more than one PCM voice.</summary>
    [Fact]
    public void Render_WithSecondPcmColumn_AddsItToTheMix()
    {
        // Arrange: quiet enough that two together don't clip the driver's mix.
        Sample recording = new("quiet", [.. OneShotSine(8000, 2).Data.Select(v => v * 0.25f)], null);
        DacSample[] dacs = [new(0, 8000)];
        GenesisPlan one = PlanOf([], dacs, Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000))));
        PlanCell[] wide = [.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ColumnCount)];
        wide[(int)GenesisChannel.Fm6] = Note(VoiceKind.Dac, rateHz: 8000);
        wide[(int)GenesisChannel.Pcm2] = Note(VoiceKind.Dac, rateHz: 8000);
        GenesisPlan two = one with { Patterns = [[new PlanRow(wide, [])]] };
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: recording.Length));
        GenesisRenderOptions xgm = ChipRate with { Driver = PcmDriver.Xgm };

        // Act
        StereoAudio single = Render(one, SongOf(1, recording), playback, xgm);
        StereoAudio both = Render(two, SongOf(1, recording), playback, xgm);

        // Assert
        Assert.True(Db(both.Left, both.SampleRate, 0.2, 1.0) > Db(single.Left, single.SampleRate, 0.2, 1.0) + 3);
    }

    /// <summary>
    /// Recordings from both sides at once share the DAC's one pan (docs/adr/0091-shrinking-recordings.md): the first voice's
    /// side alone, both sides, or the louder one's side, as the plan says.
    /// </summary>
    [Theory]
    [InlineData("FirstVoice", true, false)]
    [InlineData("Centre", true, true)]
    [InlineData("Louder", false, true)]
    public void Render_WithRecordingsOnBothSides_PansTheDacAsThePlanSays(string clash, bool left, bool right)
    {
        // Arrange: song channel 0 on the left at half volume, channel 1 on the right at full.
        Sample recording = new("quiet", [.. OneShotSine(8000, 2).Data.Select(v => v * 0.25f)], null);
        PlanCell[] wide = [.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ColumnCount)];
        wide[(int)GenesisChannel.Fm6] = Note(VoiceKind.Dac, rateHz: 8000) with { Volume = 0.5 };
        wide[(int)GenesisChannel.Pcm2] = Note(VoiceKind.Dac, rateHz: 8000) with { Source = 1 };
        GenesisPlan plan = new("test", [[new PlanRow(wide, [])]], null, new Timing(TicksPerRow, 125), [], [new DacSample(0, 8000)]) { DacPanClash = Enum.Parse<DacPanClash>(clash) };
        Playback playback = new(
            [.. Enumerable.Range(0, 20 * TicksPerRow).Select(i => new PlaybackTick(
                i * TickSeconds,
                TickSeconds,
                0,
                i / TicksPerRow,
                i % TicksPerRow,
                false,
                [Playing(i == 0, volume: 0.5, pan: -1, rateHz: 8000, frames: recording.Length), Playing(i == 0, pan: 1, rateHz: 8000, frames: recording.Length)]))],
            null,
            PlaybackEnd.Stops);

        // Act
        StereoAudio sut = Render(plan, SongOf(2, recording), playback, ChipRate with { Driver = PcmDriver.Xgm });

        // Assert
        Assert.Equal((left, right), (Db(sut.Left, sut.SampleRate, 0.2, 1.0) > -40, Db(sut.Right, sut.SampleRate, 0.2, 1.0) > -40));
    }

    /// <summary>
    /// A note at 48/64 (2.5 dB down, one step) plays the 6 dB copy the shrinker stored for it, not the full-volume one: the
    /// player rounds loudness exactly as the shrinker counts copies.
    /// </summary>
    [Fact]
    public void Render_WithSharedLoudnessAtThreeQuarterVolume_PlaysTheCopyTheShrinkerCounted()
    {
        // Arrange
        Sample recording = new("quiet", [.. OneShotSine(8000, 2).Data.Select(v => v * 0.5f)], null);
        Playback full = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: recording.Length));
        Playback quarter = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, volume: 0.75, rateHz: 8000, frames: recording.Length));
        GenesisPlan exact = PlanOf([], [new DacSample(0, 8000)], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000))));
        GenesisPlan shared = PlanOf([], [new DacSample(0, 8000) { LoudnessShareSteps = 2 }], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000) with { Volume = 0.75 })));
        GenesisRenderOptions xgm = ChipRate with { Driver = PcmDriver.Xgm };

        // Act
        StereoAudio loudest = Render(exact, SongOf(1, recording), full, xgm);
        StereoAudio stored = Render(shared, SongOf(1, recording), quarter, xgm);

        // Assert
        Assert.Equal(-6, Db(stored.Left, stored.SampleRate, 0.2, 1.0) - Db(loudest.Left, loudest.SampleRate, 0.2, 1.0), 0.5);
    }

    /// <summary>
    /// A left recording that has played out no longer holds the DAC in both speakers: a longer right recording then plays on
    /// the right alone, though neither note has ended (docs/adr/0091-shrinking-recordings.md).
    /// </summary>
    [Fact]
    public void Render_WhenOneSideRecordingPlaysOut_PansTheDacToTheOtherSide()
    {
        // Arrange: a 0.1 s hit on the left (song channel 0) and a 2 s tone on the right (song channel 1).
        Sample hit = OneShotSine(8000, 0.1);
        Sample tone = OneShotSine(8000, 2);
        PlanCell[] wide = [.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ColumnCount)];
        wide[(int)GenesisChannel.Fm6] = Note(VoiceKind.Dac, rateHz: 8000);
        wide[(int)GenesisChannel.Pcm2] = Note(VoiceKind.Dac, index: 1, rateHz: 8000) with { Source = 1, Sample = 1 };
        GenesisPlan plan = new("test", [[new PlanRow(wide, [])]], null, new Timing(TicksPerRow, 125), [], [new DacSample(0, 8000), new DacSample(1, 8000)]) { DacPanClash = DacPanClash.Centre };
        Playback playback = new(
            [.. Enumerable.Range(0, 20 * TicksPerRow).Select(i => new PlaybackTick(
                i * TickSeconds,
                TickSeconds,
                0,
                i / TicksPerRow,
                i % TicksPerRow,
                false,
                [Playing(i == 0, pan: -1, rateHz: 8000, frames: hit.Length), Playing(i == 0, pan: 1, rateHz: 8000, frames: tone.Length)]))],
            null,
            PlaybackEnd.Stops);

        // Act
        StereoAudio sut = Render(plan, SongOf(2, hit, tone), playback, ChipRate with { Driver = PcmDriver.Xgm });

        // Assert
        Assert.True(Db(sut.Left, sut.SampleRate, 0.5, 1.5) < -40);
        Assert.True(Db(sut.Right, sut.SampleRate, 0.5, 1.5) > -40);
    }

    /// <summary>A half-speed recording on XGM2 keeps its pitch: the driver plays each byte twice.</summary>
    [Fact]
    public void Render_WithHalfSpeedRecordingOnXgm2_KeepsItsPitch()
    {
        // Arrange
        Sample recording = OneShotSine(8000, 2);
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: recording.Length));
        GenesisPlan full = PlanOf([], [new DacSample(0, 8000)], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000))));
        GenesisPlan half = full with { DacSamples = [new DacSample(0, 8000) { HalfSpeed = true }] };
        GenesisRenderOptions xgm2 = ChipRate with { Driver = PcmDriver.Xgm2 };

        // Act
        StereoAudio fast = Render(full, SongOf(1, recording), playback, xgm2);
        StereoAudio slow = Render(half, SongOf(1, recording), playback, xgm2);

        // Assert
        Assert.Equal(Hz(fast.Left, fast.SampleRate, 0.3, 1.3), Hz(slow.Left, slow.SampleRate, 0.3, 1.3), 5);
    }

    /// <summary>With one copy per two loudness steps, a note 3.5 dB down with a merged sample's −3 dB of gain plays at the copy the shrinker counted, 6 dB down.</summary>
    [Fact]
    public void Render_WithVolumeStepAndGain_PlaysAtTheStoredLoudness()
    {
        // Arrange
        Sample recording = new("quiet", [.. OneShotSine(8000, 2).Data.Select(v => v * 0.5f)], null);
        double volume = Math.Pow(10, -3.5 / 20);
        Playback playback = PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, volume: volume, rateHz: 8000, frames: recording.Length));
        PlanRow row = Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 8000) with { Volume = volume }));
        GenesisPlan exact = PlanOf([], [new DacSample(0, 8000)], row);
        GenesisPlan stepped = PlanOf([], [new DacSample(0, 8000) { LoudnessShareSteps = 2, GainDb = -3 }], row);
        GenesisRenderOptions xgm = ChipRate with { Driver = PcmDriver.Xgm };

        // Act
        StereoAudio asked = Render(exact, SongOf(1, recording), playback, xgm);
        StereoAudio stored = Render(stepped, SongOf(1, recording), playback, xgm);

        // Assert: −3.5 dB with −3 dB of gain is −6.5 dB, stored at −6: 2.5 dB under the note as asked.
        Assert.Equal(-2.5, Db(stored.Left, stored.SampleRate, 0.2, 1.0) - Db(asked.Left, asked.SampleRate, 0.2, 1.0), 0.5);
    }

    /// <summary>A PSG square at the loudest volume has Furnace's RMS, with no DC.</summary>
    [Fact]
    public void Render_WithPsgSquare_PlaysAtFurnacesLevel()
    {
        // Arrange
        GenesisChips chips = new(Region.Ntsc, PcmDriver.Dmf);
        chips.Psg.Write(0x80 | 0x0e); // tone 1 period, low 4 bits
        chips.Psg.Write(0x0f);        // high 6 bits: period 254, about 440 Hz
        chips.Psg.Write(0x90);        // tone 1 volume: loudest
        float[] left = new float[(int)chips.SampleRateHz];
        float[] right = new float[left.Length];

        // Act
        chips.Render(left, right);

        // Assert
        Assert.Equal(FurnacePsgSquareDb, Db(left, (int)chips.SampleRateHz, 0.5, 1), 0.1);
        Assert.Equal(0, left.Skip(left.Length / 2).Average(), 3);
        Assert.Equal(53_267, chips.SampleRateHz, 1.0);
    }

    /// <summary>By default the render is resampled to 44.1 kHz and keeps the song's length and level.</summary>
    [Fact]
    public void Render_WithDefaultOptions_Uses44100Hz()
    {
        // Arrange
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));

        // Act
        StereoAudio audio = GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)), null, new ConversionNotes(Logger.None));

        // Assert
        Assert.Equal(44_100, audio.SampleRate);
        Assert.Equal(1.2 * 44_100, audio.Length, 1.0);
        Assert.Equal(FurnaceFmDb, Db(audio.Left, audio.SampleRate, 0.2, 1.1), 0.15);
    }

    /// <summary>Tick lengths can be given in place of the playback's; they must match the ticks, and the playback must have the song's channels.</summary>
    [Fact]
    public void Render_WithBadArguments_Throws()
    {
        // Arrange
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));
        Playback playback = PlaybackOf(1, (r, t) => Playing(r == 0 && t == 0));
        ConversionNotes notes = new(Logger.None);

        // Act
        StereoAudio longer = GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [.. Enumerable.Repeat(0.04, TicksPerRow)] }, notes);

        // Assert
        Assert.Equal(0.24 * 52_781.2, longer.Length, 1.0);
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [0.02] }, notes));
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [.. Enumerable.Repeat(-1.0, TicksPerRow)] }, notes));
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(2, LoopedSine()), playback, ChipRate, notes));
        Assert.Throws<ArgumentOutOfRangeException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, new GenesisRenderOptions(SampleRate: 0), notes));
    }

    /// <summary>An NTSC Genesis runs its chips faster: the chip rate is the NTSC YM3438's.</summary>
    [Fact]
    public void Render_WithNtsc_UsesNtscClocks()
    {
        // Act
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(1, (r, t) => Playing(r == 0 && t == 0)), ChipRate with { Region = Region.Ntsc });

        // Assert
        Assert.Equal(53_267, audio.SampleRate);
    }

    /// <summary>An operator turned all the way down.</summary>
    private static FmOperator Muted() => new(1, 0, 127, 0, 31, false, 0, 0, 0, 8);
}
