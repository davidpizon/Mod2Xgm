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
    public void FmSinePlaysAtFurnacesLevelAndPitch()
    {
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        Assert.Equal(52_781, audio.SampleRate);
        Assert.Equal(FurnaceFmDb, Db(audio.Left, audio.SampleRate, 0.2, 1.1), 0.15);
        Assert.Equal(FurnaceFmDb, Db(audio.Right, audio.SampleRate, 0.2, 1.1), 0.15);
        Assert.Equal(440, Hz(audio.Left, audio.SampleRate, 0.2, 1.2), 2.0);
    }

    /// <summary>Volume and pan follow the song channel tick by tick: half volume is 6 dB down, in TL steps; a pan to the left silences the right.</summary>
    [Fact]
    public void VolumeAndPanFollowThePlayback()
    {
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))),
            SongOf(1, LoopedSine()),
            PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, volume: r < 10 ? 1 : 0.5, pan: r < 15 ? 0 : -1)));

        double full = Db(audio.Left, audio.SampleRate, 0.2, 1.1);
        double half = Db(audio.Left, audio.SampleRate, 1.3, 1.7);
        Assert.Equal(-6.0, half - full, 0.4);
        Assert.True(Db(audio.Right, audio.SampleRate, 1.9, 2.3) < -90);
        Assert.Equal(half, Db(audio.Left, audio.SampleRate, 1.9, 2.3), 0.2);
    }

    /// <summary>A pitch change on the song channel (a slide, vibrato, arpeggio) moves the FM note: an octave up doubles it.</summary>
    [Fact]
    public void PitchFollowsThePlayback()
    {
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))),
            SongOf(1, LoopedSine()),
            PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: r < 10 ? SineRateHz : 2 * SineRateHz)));

        Assert.Equal(880, Hz(audio.Left, audio.SampleRate, 1.3, 2.3), 2.0);
    }

    /// <summary>A note waits for the tick its sample starts on (EDx): silent before tick 3, sounding after.</summary>
    [Fact]
    public void ANoteStartsOnTheTickItsSampleStarts()
    {
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(2, (r, t) => Playing(r == 0 && t == 3)));

        Assert.True(Db(audio.Left, audio.SampleRate, 0, 0.058) < -90);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.07, 0.11) > -15);
    }

    /// <summary>A note-off lets go of the key: the FM note releases and is silent soon after.</summary>
    [Fact]
    public void ANoteOffReleasesTheNote()
    {
        StereoAudio audio = Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, NoteOff))),
            SongOf(1, LoopedSine()),
            PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)));

        Assert.True(Db(audio.Left, audio.SampleRate, 0.05, 0.11) > -15);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.6, 1.1) < -60);
    }

    /// <summary>E9x starts a decaying note again, so it is loud again after the restart; a tone portamento slides the playing note without one.</summary>
    [Fact]
    public void RetriggerRestartsAndPortamentoSlides()
    {
        FmPatch decaying = new("decay", 7, 0, [new FmOperator(1, 0, 0, 0, 31, false, 20, 0, 15, 15), Muted(), Muted(), Muted()]);
        Song song = SongOf(1, LoopedSine());
        Playback playback = PlaybackOf(2, (r, t) => Playing((r == 0 && t == 0) || (r == 1 && t == 3)));

        StereoAudio restarted = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new Retrigger(3))))), song, playback);
        StereoAudio slid = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))), Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new TonePortamento(null, PitchScale.AmigaPeriod))))), song, PlaybackOf(2, (r, t) => Playing(r == 0 && t == 0)));

        double before = Db(restarted.Left, restarted.SampleRate, 0.17, 0.18);
        double after = Db(restarted.Left, restarted.SampleRate, 0.185, 0.195);
        Assert.True(after > before + 10, $"{before} dB before the restart, {after} dB after");
        Assert.True(Db(slid.Left, slid.SampleRate, 0.185, 0.195) < Db(slid.Left, slid.SampleRate, 0.11, 0.12));
    }

    /// <summary>A patch's SSG-EG plays in the song: a repeating shape keeps a decaying carrier sounding where it would have faded out.</summary>
    [Fact]
    public void SsgEgPlaysInTheSong()
    {
        FmPatch decaying = new("decay", 7, 0, [new FmOperator(1, 0, 0, 0, 31, false, 20, 0, 15, 15), Muted(), Muted(), Muted()]);
        FmPatch repeating = decaying with { Operators = [decaying.Operators[0] with { SsgEg = 8 }, .. decaying.Operators.Skip(1)] };
        Song song = SongOf(1, LoopedSine());
        Playback playback = PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0));

        StereoAudio once = Render(PlanOf([decaying], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), song, playback);
        StereoAudio again = Render(PlanOf([repeating], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), song, playback);

        Assert.True(Db(once.Left, once.SampleRate, 0.8, 1.0) < -60);
        Assert.True(Db(again.Left, again.SampleRate, 0.8, 1.0) > -30);
    }

    /// <summary>A note delayed past the end of its row (EDx with x ≥ speed) is left out, as ProTracker plays nothing; the render says so.</summary>
    [Fact]
    public void ANoteDelayedPastItsRowIsLeftOut()
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        StereoAudio audio = GenesisRenderer.Render(
            PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm, effects: new NoteDelay(8))))),
            SongOf(1, LoopedSine()),
            PlaybackOf(2, (_, _) => Playing(false)),
            ChipRate,
            notes);

        Assert.True(Db(audio.Left, audio.SampleRate, 0, 0.24) < -90);
        Assert.Contains(notes.Messages, m => m.Contains("delayed past their row's end", StringComparison.Ordinal));
    }

    /// <summary>Each PCM driver plays a full-scale DAC sine at Furnace's level, panned by the song channel, and stops it on a note-off.</summary>
    [Theory]
    [InlineData(nameof(PcmDriver.Dmf))]
    [InlineData(nameof(PcmDriver.Xgm))]
    [InlineData(nameof(PcmDriver.Xgm2))]
    public void EveryDriverPlaysTheDacAtFurnacesLevel(string driver)
    {
        GenesisPlan plan = PlanOf([], [new DacSample(0, 16_000)], [.. Enumerable.Range(0, 8).Select(r => r == 0 ? Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 16_000))) : r == 7 ? Row((GenesisChannel.Fm6, NoteOff)) : Row())]);
        Playback playback = PlaybackOf(12, (r, t) => Playing(r == 0 && t == 0, pan: 1, rateHz: 16_000, frames: 32_000));

        StereoAudio audio = Render(plan, SongOf(1, OneShotSine(16_000, 2)), playback, ChipRate with { Driver = Enum.Parse<PcmDriver>(driver) });

        Assert.Equal(FurnaceFmDb, Db(audio.Right, audio.SampleRate, 0.1, 0.8), 0.3);
        Assert.True(Db(audio.Left, audio.SampleRate, 0.1, 0.8) < -90);
        Assert.Equal(440, Hz(audio.Right, audio.SampleRate, 0.1, 0.8), 3.0);
        Assert.True(Db(audio.Right, audio.SampleRate, 0.9, 1.4) < -60);
    }

    /// <summary>The DAC has no volume: a note at half volume plays a copy 6 dB down (the DMF in 3 dB steps, the XGM drivers exactly), and stays there.</summary>
    [Theory]
    [InlineData(nameof(PcmDriver.Dmf))]
    [InlineData(nameof(PcmDriver.Xgm2))]
    public void TheDacPlaysAtTheVolumeItStartsWith(string driver)
    {
        GenesisPlan plan = PlanOf([], [new DacSample(0, 16_000)], Row((GenesisChannel.Fm6, Note(VoiceKind.Dac, rateHz: 16_000))));
        Playback playback = PlaybackOf(12, (r, t) => Playing(r == 0 && t == 0, volume: r == 0 ? 0.5 : 1, rateHz: 16_000, frames: 32_000));

        StereoAudio audio = Render(plan, SongOf(1, OneShotSine(16_000, 2)), playback, ChipRate with { Driver = Enum.Parse<PcmDriver>(driver) });

        Assert.Equal(FurnaceFmDb - 6.0, Db(audio.Left, audio.SampleRate, 0.2, 1.2), 0.3);
    }

    /// <summary>White noise at full volume plays at Furnace's PSG level; it follows its sample's loudness, 12 dB down where the sample is.</summary>
    [Fact]
    public void NoiseFollowsItsSamplesLoudness()
    {
        Random random = new(1);
        float[] data = [.. Enumerable.Range(0, 16_000).Select(i => (float)((random.NextDouble() * 2) - 1) * (i < 8000 ? 1f : 0.25f))];
        Sample noise = new("noise", data, null);
        GenesisPlan plan = PlanOf([], [], Row((GenesisChannel.Psg4, Note(VoiceKind.PsgNoise, rateHz: 8000))));

        StereoAudio audio = Render(plan, SongOf(1, noise), PlaybackOf(20, (r, t) => Playing(r == 0 && t == 0, rateHz: 8000, frames: 16_000)));

        double loud = Db(audio.Left, audio.SampleRate, 0.1, 0.9);
        Assert.Equal(FurnacePsgSquareDb, loud, 0.6);
        Assert.Equal(-12, Db(audio.Left, audio.SampleRate, 1.1, 1.9) - loud, 1.5);
        Assert.True(Db(audio.Left, audio.SampleRate, 2.1, 2.3) < -60);
        Assert.Equal(loud, Db(audio.Right, audio.SampleRate, 0.1, 0.9), 0.01);
    }

    /// <summary>A PSG square at the loudest volume has Furnace's RMS, with no DC.</summary>
    [Fact]
    public void PsgSquarePlaysAtFurnacesLevel()
    {
        GenesisChips chips = new(Region.Ntsc, PcmDriver.Dmf);
        chips.Psg.Write(0x80 | 0x0e); // tone 1 period, low 4 bits
        chips.Psg.Write(0x0f);        // high 6 bits: period 254, about 440 Hz
        chips.Psg.Write(0x90);        // tone 1 volume: loudest
        float[] left = new float[(int)chips.SampleRateHz];
        float[] right = new float[left.Length];

        chips.Render(left, right);

        Assert.Equal(FurnacePsgSquareDb, Db(left, (int)chips.SampleRateHz, 0.5, 1), 0.1);
        Assert.Equal(0, left.Skip(left.Length / 2).Average(), 3);
        Assert.Equal(53_267, chips.SampleRateHz, 1.0);
    }

    /// <summary>By default the render is resampled to 44.1 kHz and keeps the song's length and level.</summary>
    [Fact]
    public void TheDefaultIs44100Hz()
    {
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));
        StereoAudio audio = GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), PlaybackOf(10, (r, t) => Playing(r == 0 && t == 0)), null, new ConversionNotes(Logger.None));

        Assert.Equal(44_100, audio.SampleRate);
        Assert.Equal(1.2 * 44_100, audio.Length, 1.0);
        Assert.Equal(FurnaceFmDb, Db(audio.Left, audio.SampleRate, 0.2, 1.1), 0.15);
    }

    /// <summary>Tick lengths can be given in place of the playback's; they must match the ticks, and the playback must have the song's channels.</summary>
    [Fact]
    public void ArgumentsAreChecked()
    {
        GenesisPlan plan = PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm))));
        Playback playback = PlaybackOf(1, (r, t) => Playing(r == 0 && t == 0));
        ConversionNotes notes = new(Logger.None);

        StereoAudio longer = GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [.. Enumerable.Repeat(0.04, TicksPerRow)] }, notes);

        Assert.Equal(0.24 * 52_781.2, longer.Length, 1.0);
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [0.02] }, notes));
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, ChipRate with { TickDurations = [.. Enumerable.Repeat(-1.0, TicksPerRow)] }, notes));
        Assert.Throws<ArgumentException>(() => GenesisRenderer.Render(plan, SongOf(2, LoopedSine()), playback, ChipRate, notes));
        Assert.Throws<ArgumentOutOfRangeException>(() => GenesisRenderer.Render(plan, SongOf(1, LoopedSine()), playback, new GenesisRenderOptions(SampleRate: 0), notes));
    }

    /// <summary>An NTSC Genesis runs its chips faster: the chip rate is the NTSC YM3438's.</summary>
    [Fact]
    public void NtscUsesTheNtscClocks()
    {
        StereoAudio audio = Render(PlanOf([FmPatchLibrary.Sine], [], Row((GenesisChannel.Fm1, Note(VoiceKind.Fm)))), SongOf(1, LoopedSine()), PlaybackOf(1, (r, t) => Playing(r == 0 && t == 0)), ChipRate with { Region = Region.Ntsc });

        Assert.Equal(53_267, audio.SampleRate);
    }

    /// <summary>An operator turned all the way down.</summary>
    private static FmOperator Muted() => new(1, 0, 127, 0, 31, false, 0, 0, 0, 8);
}
