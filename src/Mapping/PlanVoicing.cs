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
    public const double A4Hz = 440;

    /// <summary>Without a measured pitch, an FM note is placed this many semitones above the MOD note's name (ProTracker C-2 → C-4).</summary>
    private const int FallbackShift = 24;

    /// <summary>Noise brighter than this uses the highest fixed noise rate (note D); see <see cref="NoiseNote"/>.</summary>
    private const double BrightNoise = 0.5;

    /// <summary>Noise brighter than this (but not <see cref="BrightNoise"/>) uses the middle rate (note C#); darker noise the lowest (C).</summary>
    private const double MiddleNoise = 0.25;

    /// <summary>Pan beyond this from the centre plays on one side only: the Genesis has left, right or both, nothing between.</summary>
    private const double PanSide = 1.0 / 3;

    private readonly Dictionary<int, SamplePitch> _pitches = [];
    private readonly Dictionary<int, int> _noiseNotes = [];
    private readonly Dictionary<(int Sample, int Patch), (double Gain, bool Limited)> _fmLevels = [];

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
            double brightness = SampleTraits.Analyze(song.Samples[sample]).Brightness;
            note = brightness > BrightNoise ? 2 : brightness > MiddleNoise ? 1 : 0;
            _noiseNotes[sample] = note;
        }

        return note;
    }

    /// <summary>
    /// The factor on the MOD volume that plays <paramref name="sample"/> on patch <paramref name="patch"/> as loud as the
    /// MOD does (ADR 0050): the sample's level over the patch's. It is at most the patch's ceiling, full volume or lower
    /// where the carriers would add up past full scale; <c>Limited</c> says the sample is louder than that.
    /// </summary>
    public (double Gain, bool Limited) FmLevel(int sample, int patch)
    {
        if (!_fmLevels.TryGetValue((sample, patch), out (double Gain, bool Limited) level))
        {
            Sample played = song.Samples[sample];
            FmPatch fm = plan.FmPatches[patch];
            double rms = VoiceLevel.Fm(fm, sustained: played.Loop is not null).Rms;
            double peak = VoiceLevel.Fm(fm, sustained: false).Peak;
            level = Limit(rms > 0 ? VoiceLevel.Sample(played) / rms : 1, peak > 1 ? 1 / peak : 1);
            _fmLevels[(sample, patch)] = level;
        }

        return level;
    }

    /// <summary>The factor on the MOD volume that plays noise sample <paramref name="sample"/> as loud as the MOD does, at most 1 (ADR 0050).</summary>
    public (double Gain, bool Limited) NoiseLevel(int sample) => Limit(VoiceLevel.Loudest(song.Samples[sample]) / VoiceLevel.PsgNoise, 1);

    /// <summary>The side a pan of −1 (left) to +1 (right) plays on: −1 left only, +1 right only, 0 both.</summary>
    public static int Side(double pan) => pan < -PanSide ? -1 : pan > PanSide ? 1 : 0;

    /// <summary>
    /// <paramref name="gain"/> kept at or below <paramref name="ceiling"/>. A sample too loud for its voice keeps its
    /// dynamics: its notes at full MOD volume play at the voice's loudest, softer ones in proportion.
    /// </summary>
    private static (double Gain, bool Limited) Limit(double gain, double ceiling) => (Math.Min(gain, ceiling), gain > ceiling);
}
