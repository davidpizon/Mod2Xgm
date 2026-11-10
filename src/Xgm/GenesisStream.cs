using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Xgm;

/// <summary>What one <see cref="ChipEvent"/> is.</summary>
internal enum ChipEventKind
{
    /// <summary>A YM3438 register write.</summary>
    FmWrite,

    /// <summary>A byte to the PSG's port.</summary>
    PsgWrite,

    /// <summary>A PCM play.</summary>
    PcmPlay,

    /// <summary>A PCM stop.</summary>
    PcmStop,
}

/// <summary>
/// One thing the player told the sound hardware: a YM3438 register write (<see cref="Part"/>, <see cref="Register"/>,
/// <see cref="Value"/>), a PSG byte (<see cref="Value"/>), or a PCM play or stop on <see cref="Channel"/>.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Part">The YM3438 part (port) 0 or 1 of a register write.</param>
/// <param name="Register">The register of a YM3438 write.</param>
/// <param name="Value">The value of a YM3438 write, or the PSG byte.</param>
/// <param name="Channel">The PCM channel of a play or stop (0–2).</param>
/// <param name="Data">The stored copy a play plays: 8-bit signed at the driver's rate, the same array for the same copy.</param>
/// <param name="HalfSpeed">True when a play plays at half speed.</param>
/// <param name="Sample">The song sample a play's copy was made from.</param>
/// <param name="Name">Which sample a play's copy is, for the report.</param>
internal readonly record struct ChipEvent(
    ChipEventKind Kind,
    int Part = 0,
    byte Register = 0,
    byte Value = 0,
    int Channel = 0,
    sbyte[]? Data = null,
    bool HalfSpeed = false,
    int Sample = 0,
    string Name = "")
{
    /// <summary>A YM3438 register write.</summary>
    public static ChipEvent Fm(int part, byte register, byte value) => new(ChipEventKind.FmWrite, part, register, value);

    /// <summary>A PSG port write.</summary>
    public static ChipEvent Psg(byte value) => new(ChipEventKind.PsgWrite, Value: value);

    /// <summary>A PCM play.</summary>
    public static ChipEvent Play(int channel, sbyte[] data, bool halfSpeed, int sample, string name) =>
        new(ChipEventKind.PcmPlay, Channel: channel, Data: data, HalfSpeed: halfSpeed, Sample: sample, Name: name);

    /// <summary>A PCM stop.</summary>
    public static ChipEvent Stop(int channel) => new(ChipEventKind.PcmStop, Channel: channel);
}

/// <summary>
/// A song as the XGM writers store it: what the sound hardware is told on each frame (1/50 s PAL, 1/60 s NTSC), in order
/// (docs/formats/xgm.md#common-to-both, docs/adr/0099-xgm2-writer-records-the-player.md).
/// </summary>
/// <param name="Frames">Every frame of one pass through the song, each with its events in order.</param>
/// <param name="LoopFrame">The frame the song goes back to after the last one, or <c>null</c> when it ends.</param>
/// <param name="Pal">True for a PAL Genesis (50 frames a second), false for NTSC (60).</param>
/// <param name="PcmRateHz">The PCM driver's full-speed rate on that Genesis, in hertz.</param>
internal sealed record GenesisStream(IReadOnlyList<IReadOnlyList<ChipEvent>> Frames, int? LoopFrame, bool Pal, double PcmRateHz)
{
    /// <summary>Frames a second on a PAL Genesis.</summary>
    private const int PalFrameRate = 50;

    /// <summary>Frames a second on an NTSC Genesis.</summary>
    private const int NtscFrameRate = 60;

    /// <summary>Frames a second.</summary>
    public int FrameRate => Pal ? PalFrameRate : NtscFrameRate;

    /// <summary>
    /// Plays <paramref name="plan"/> with the same player the Genesis render uses, on XGM2's PCM driver, and records what it
    /// tells the hardware, frame by frame: each tick goes to the frame it starts in. Says in a note how many ticks share a
    /// frame with the tick before, whose changes then play together.
    /// </summary>
    /// <param name="plan">Which channel plays each note, with which voice.</param>
    /// <param name="song">The plan's song.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="region">The Genesis that plays the file (<c>--output-region</c>): its clocks and frame rate.</param>
    /// <param name="resampler">How recordings are brought to the driver's rate (<c>--resampler</c>).</param>
    /// <param name="notes">Collects what can't be played as the plan says.</param>
    public static GenesisStream RecordXgm2(GenesisPlan plan, Song song, Playback playback, Region region, ResamplingMode resampler, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(notes);
        bool pal = region == Region.Pal;
        int rate = pal ? PalFrameRate : NtscFrameRate;
        Recorder recorder = new();
        GenesisChips chips = new(region, PcmDriver.Xgm2) { Recorder = recorder };
        IReadOnlyList<PlaybackTick> ticks = playback.Ticks;
        double[] durations = [.. ticks.Select(t => t.DurationSeconds)];

        // The player's first writes (LFO, CH3 mode, pans) go to frame 0.
        GenesisPlanPlayer player = new(plan, song, playback, durations, chips, resampler, notes);
        int[] frameOf = new int[ticks.Count];
        double seconds = 0;
        int shared = 0;
        for (int t = 0; t < ticks.Count; t++)
        {
            frameOf[t] = FrameAt(seconds, rate);
            if (t > 0 && frameOf[t] == frameOf[t - 1])
            {
                shared++;
            }

            recorder.Frame = frameOf[t];
            player.BeginTick(t);
            player.FinishTick(t);
            seconds += durations[t];
        }

        player.Report();
        if (shared > 0)
        {
            notes.Add("{Count} ticks start in the same 1/{Rate} s frame as the tick before, so XGM2 plays their changes together", shared, rate);
        }

        int length = Math.Max(FrameAt(seconds, rate), recorder.Frames.Count);
        while (recorder.Frames.Count < Math.Max(1, length))
        {
            recorder.Frames.Add([]);
        }

        int? loop = playback.LoopTick is { } loopTick && loopTick < ticks.Count ? frameOf[loopTick] : null;
        return new GenesisStream(recorder.Frames, loop, pal, Xgm2PcmDriver.BeatRateHz(pal ? Ym3438.PalClockHz : Ym3438.NtscClockHz));
    }

    /// <summary>The frame a moment <paramref name="seconds"/> into the song falls in: the nearest frame start.</summary>
    internal static int FrameAt(double seconds, int frameRate) => (int)Math.Round(seconds * frameRate, MidpointRounding.AwayFromZero);

    /// <summary>Collects the player's events into the frame it is on.</summary>
    private sealed class Recorder : IChipRecorder
    {
        /// <summary>The frames so far, each with its events.</summary>
        public List<IReadOnlyList<ChipEvent>> Frames { get; } = [];

        /// <summary>The frame new events go to.</summary>
        public int Frame { get; set; }

        /// <inheritdoc/>
        public void FmWrite(int part, byte register, byte value) => Add(ChipEvent.Fm(part, register, value));

        /// <inheritdoc/>
        public void PsgWrite(byte value) => Add(ChipEvent.Psg(value));

        /// <inheritdoc/>
        public void PcmPlay(int channel, sbyte[] data, bool halfSpeed, int sample, string name) => Add(ChipEvent.Play(channel, data, halfSpeed, sample, name));

        /// <inheritdoc/>
        public void PcmStop(int channel) => Add(ChipEvent.Stop(channel));

        private void Add(ChipEvent e)
        {
            while (Frames.Count <= Frame)
            {
                Frames.Add(new List<ChipEvent>());
            }

            ((List<ChipEvent>)Frames[Frame]).Add(e);
        }
    }
}
