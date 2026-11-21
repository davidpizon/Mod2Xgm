using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// How a plan's voices sound, decided once for every output: the pitch an FM note is placed at, the level each voice
/// plays at (docs/adr/0050-voice-levels.md), the fixed rate of a noise note and the side a pan plays on. The DMF
/// translator and the Genesis renderer (docs/adr/0059-genesis-renderer.md) both use it, so the render plays the plan the
/// way the file does. Measurements are made on first use and kept.
/// </summary>
/// <param name="plan">The mapped song: its FM patches.</param>
/// <param name="song">The song the plan was made from: its samples.</param>
internal sealed class PlanVoicing(GenesisPlan plan, Song song)
{
    /// <summary>A-4 (440 Hz), counted in semitones from C-0.</summary>
    public const int A4 = 57;

    /// <summary>Concert pitch of A-4.</summary>
    private const double A4Hz = 440;

    /// <summary>Without a measured pitch, an FM note is placed this many semitones above the MOD note's name (ProTracker C-2 → C-4).</summary>
    private const int FallbackShift = 24;

    /// <summary>Noise brighter than this uses the highest fixed noise rate (note D); see <see cref="NoiseNote"/>.</summary>
    private const double BrightNoise = 0.5;

    /// <summary>Noise brighter than this (but not <see cref="BrightNoise"/>) uses the middle rate (note C#); darker noise the lowest (C).</summary>
    private const double MiddleNoise = 0.25;

    /// <summary>Pan beyond this from the centre plays on one side only: the Genesis has left, right or both, nothing between.</summary>
    private const double PanSide = 1.0 / 3;

    /// <summary>The factor on a centred bass note's level: the same power on both sides as it had on one (<see cref="CentredBass"/>).</summary>
    public static readonly double CentredGain = Math.Sqrt(0.5);

    /// <summary>Notes heard below this are bass (<see cref="CentredBass"/>); David preferred them centred on the two songs he graded.</summary>
    private const double CentredBassHz = 250;

    /// <summary>What's left of a noise sample's highs on the PSG: 12 dB down, David's pick over 6 dB, as the PSG's noise reaches far higher than the MOD's samples (docs/adr/0087-greedy-allocator.md).</summary>
    private const double TunedNoiseGain = 0.25;

    /// <summary>Frames of the recording <see cref="FmDecay"/> measures at a time: as many as a sample's loudest window (<see cref="VoiceLevel.Loudest"/>).</summary>
    private const int DecayWindowFrames = 256;

    private readonly Dictionary<int, SamplePitch> _pitches = [];
    private readonly Dictionary<int, int> _noiseNotes = [];
    private readonly Dictionary<(int Sample, int Patch), (double Gain, bool Limited)> _fmLevels = [];
    private readonly Dictionary<(int Sample, int Patch), double> _decayScales = [];

    /// <summary>The pitch an FM note sounds at, in semitones from C-0 with a fraction; <paramref name="measured"/> says how it was found.</summary>
    /// <param name="note">The MOD note: its playback rate, and its name for the fallback.</param>
    /// <param name="sample">The song sample the note plays.</param>
    /// <param name="measured">True when the sample's pitch was measured; false when the note's name was used.</param>
    public double HeardSemitones(NotePitch note, int sample, out bool measured)
    {
        if (!_pitches.TryGetValue(sample, out SamplePitch pitch))
        {
            pitch = SamplePitch.Measure(song.Samples[sample]);
            _pitches[sample] = pitch;
        }

        if (pitch.FrequencyAt(note.PlaybackRateHz) is { } hz)
        {
            measured = true;
            return A4 + (12 * Math.Log2(hz / A4Hz));
        }

        measured = false;
        return note.Semitone + FallbackShift + (note.Cents / 100);
    }

    /// <summary>The frequency, in hertz, of a pitch in semitones from C-0.</summary>
    public static double Hz(double semitones) => A4Hz * Math.Pow(2, (semitones - A4) / 12);

    /// <summary>The note that picks a fixed noise rate for <paramref name="sample"/>: 2 = D (fastest) for bright noise, 1 = C# for middling, 0 = C (slowest) for dark.</summary>
    public int NoiseNote(int sample)
    {
        if (!_noiseNotes.TryGetValue(sample, out int note))
        {
            note = NoiseNoteOf(song.Samples[sample]);
            _noiseNotes[sample] = note;
        }

        return note;
    }

    /// <summary>The note that picks a fixed noise rate for <paramref name="sample"/> (<see cref="NoiseNote"/>).</summary>
    public static int NoiseNoteOf(Sample sample)
    {
        double brightness = SampleTraits.Analyze(sample).Brightness;
        return brightness > BrightNoise ? 2 : brightness > MiddleNoise ? 1 : 0;
    }

    /// <summary>
    /// The factor on the MOD volume that plays <paramref name="sample"/> on patch <paramref name="patch"/> as loud as the
    /// MOD does (ADR 0050): the sample's level over the patch's. It is at most the patch's ceiling, full volume or lower
    /// where the carriers would add up past full scale; <c>Limited</c> says the sample is louder than that. A patch that
    /// was rendered and measured (<see cref="FmPatch.HeardRms"/>) uses that level instead of the one worked out from its registers.
    /// A sample whose loop is a short tail after a long recording (<see cref="LoopKind.Tail"/>) is matched by its recording's
    /// loudest moment, as a sample that plays once is, not by its tail (docs/adr/0126-tail-loop-level.md).
    /// </summary>
    public (double Gain, bool Limited) FmLevel(int sample, int patch)
    {
        if (!_fmLevels.TryGetValue((sample, patch), out (double Gain, bool Limited) level))
        {
            Sample played = song.Samples[sample];
            FmPatch fm = plan.FmPatches[patch];

            // A held note sounds like its loop; a short tail after a long recording is not what the MOD's notes play, so
            // that sample is matched by its recording, as one that plays once is (docs/adr/0126-tail-loop-level.md).
            bool held = played.Loop is not null && LoopShape.Of(played, SampleTraits.Analyze(played)) != LoopKind.Tail;
            double rms = fm.HeardRms ?? VoiceLevel.Fm(fm, sustained: held).Rms;
            double peak = VoiceLevel.Fm(fm, sustained: false).Peak;
            double heard = held ? VoiceLevel.Sample(played) : VoiceLevel.Loudest(played);
            level = Limit(rms > 0 ? heard / rms : 1, peak > 1 ? 1 / peak : 1);
            _fmLevels[(sample, patch)] = level;
        }

        return level;
    }

    /// <summary>
    /// For a sample whose loop is a short tail after a long recording (<see cref="LoopKind.Tail"/>), the factor on an FM
    /// note's level <paramref name="frame"/> frames into the sample, so the note fades as the recording does into its
    /// tail instead of holding at the hit's level (docs/adr/0131-tail-loop-decay.md); 1 for any other sample. The note's
    /// level (<see cref="FmLevel"/>) matches the hit to the patch's attack; the factor is the recording's level there
    /// against its hit, with the patch's own fall to its sustain level taken out, and never more than 1.
    /// </summary>
    /// <param name="sample">The song sample the note plays.</param>
    /// <param name="patch">The FM patch it plays on.</param>
    /// <param name="frame">Where in the sample the note is, in frames.</param>
    public double FmDecay(int sample, int patch, double frame)
    {
        double scale = DecayScale(sample, patch);
        return scale <= 0 ? 1 : Math.Min(1, scale * VoiceLevel.WindowRms(song.Samples[sample], (int)frame, DecayWindowFrames));
    }

    /// <summary>
    /// The factor <see cref="FmDecay"/> settles at once a note plays <paramref name="sample"/>'s tail on
    /// <paramref name="patch"/>, or <c>null</c> when the sample's notes don't fade (its loop isn't a tail).
    /// </summary>
    public double? FmTailDecay(int sample, int patch)
    {
        double scale = DecayScale(sample, patch);
        return song.Samples[sample].Loop is { } loop && scale > 0
            ? Math.Min(1, scale * VoiceLevel.WindowRms(song.Samples[sample], loop.Start, loop.Length))
            : null;
    }

    /// <summary>
    /// What <see cref="FmDecay"/> multiplies a window's RMS by: the patch's attack over its sustained level, over the
    /// sample's loudest window; 0 when the sample's notes don't fade.
    /// </summary>
    private double DecayScale(int sample, int patch)
    {
        if (!_decayScales.TryGetValue((sample, patch), out double scale))
        {
            Sample played = song.Samples[sample];
            FmPatch fm = plan.FmPatches[patch];
            double attack = VoiceLevel.Fm(fm, sustained: false).Rms;
            double sustained = VoiceLevel.Fm(fm, sustained: true).Rms;
            double loudest = VoiceLevel.Loudest(played);
            bool tail = played.Loop is not null && LoopShape.Of(played, SampleTraits.Analyze(played)) == LoopKind.Tail;
            scale = tail && sustained > 0 && loudest > 0 ? attack / sustained / loudest : 0;
            _decayScales[(sample, patch)] = scale;
        }

        return scale;
    }

    /// <summary>
    /// The factor on the MOD volume for noise sample <paramref name="sample"/>, at most 1: as loud as the MOD plays it
    /// (ADR 0050), or in a plan <see cref="GenesisPlan.TunedByEar"/>, its highs alone, turned down by
    /// <see cref="TunedNoiseGain"/> (docs/adr/0087-greedy-allocator.md).
    /// </summary>
    public (double Gain, bool Limited) NoiseLevel(int sample) => plan.TunedByEar
        ? Limit(VoiceLevel.LoudestHigh(song.Samples[sample]) * TunedNoiseGain / VoiceLevel.PsgNoise, 1)
        : ToneLevel(sample);

    /// <summary>The factor on the MOD volume that plays sample <paramref name="sample"/> on a PSG tone as loud as the MOD does, at most 1 (ADR 0050).</summary>
    public (double Gain, bool Limited) ToneLevel(int sample) => Limit(VoiceLevel.Loudest(song.Samples[sample]) / VoiceLevel.PsgNoise, 1);

    /// <summary>
    /// True when an FM note plays centred though its channel is panned, in a plan <see cref="GenesisPlan.TunedByEar"/>: a
    /// bass panned to one side leaves that side too heavy, where the MOD's 75/25 keeps it near the middle. Play it at
    /// <see cref="CentredGain"/> (docs/adr/0087-greedy-allocator.md).
    /// </summary>
    public bool CentredBass(NotePitch note, int sample, double pan) =>
        plan.TunedByEar && Side(pan) != 0 && Hz(HeardSemitones(note, sample, out _)) < CentredBassHz;

    /// <summary>The side a pan of −1 (left) to +1 (right) plays on: −1 left only, +1 right only, 0 both.</summary>
    public static int Side(double pan) => pan < -PanSide ? -1 : pan > PanSide ? 1 : 0;

    /// <summary>
    /// <paramref name="gain"/> kept at or below <paramref name="ceiling"/>. A sample too loud for its voice keeps its
    /// dynamics: its notes at full MOD volume play at the voice's loudest, softer ones in proportion.
    /// </summary>
    private static (double Gain, bool Limited) Limit(double gain, double ceiling) => (Math.Min(gain, ceiling), gain > ceiling);
}
