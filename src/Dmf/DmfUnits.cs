using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Dmf;

/// <summary>
/// Turns song-model amounts (linear volume, Amiga periods, cycles per tick) into the units DefleMask's effects and
/// volume column count in (docs/formats/dmf.md#effect-units). The units come from Furnace's DMF compatibility mode
/// (read, not copied) and are checked by ear with the effect probes (tests/golden/dmf-effect-probes).
/// </summary>
internal static class DmfUnits
{
    /// <summary>Highest FM channel volume.</summary>
    public const int MaxFmVolume = 0x7F;

    /// <summary>Highest PSG channel volume.</summary>
    public const int MaxPsgVolume = 0xF;

    /// <summary>
    /// The FM chip's F-number for a C: the first F-number of each octave (block) at the Genesis's 7.67 MHz clock,
    /// 261.63 Hz × 144 × 2²⁰ / 7 670 453 / 2³. Pitch slides on FM step the F-number (docs/formats/dmf.md#effect-units).
    /// </summary>
    public const double FnumOfC = 643.8;

    /// <summary>One FM volume step: the volume lowers the carriers' total level, 0.75 dB a step (docs/genesis-audio.md).</summary>
    private const double FmStepDb = 0.75;

    /// <summary>A volume slide moves the volume by its digit / 4 per tick (Furnace: speed × 64 in units of 1/256).</summary>
    public const double VolumeSlideStep = 0.25;

    /// <summary>Vibrato: a full cycle is 64 steps, and the speed digit is steps per tick (as in ProTracker).</summary>
    public const double VibratoSteps = 64;

    /// <summary>Tremolo: a full cycle is 128 steps, and the speed digit is steps per tick.</summary>
    public const double TremoloSteps = 128;

    /// <summary>
    /// Semitones of peak pitch change per vibrato depth digit: the sine (±127) × depth × 15 / 16 / 15, in 1/128 semitone
    /// (Furnace's linear pitch for <c>04xy</c> and <c>E5xx</c> in DMF files).
    /// </summary>
    public const double VibratoSemitonesPerDepth = 127.0 / 16 / 128;

    /// <summary>
    /// Decibels of swing per FM tremolo depth digit: DefleMask's <c>07xy</c> only lowers the volume, by about 0.65 dB per
    /// digit from top to bottom (measured from DefleMask's WAV export of the effect probes: depth F swings 9.7 dB).
    /// </summary>
    public const double TremoloDbPerDepth = 0.65;

    /// <summary><c>E5xx</c> steps per semitone, around <see cref="FinePitchCentre"/>.</summary>
    public const double FinePitchStepsPerSemitone = 128;

    /// <summary><c>E5xx</c> value for no fine pitch.</summary>
    public const int FinePitchCentre = 0x80;

    /// <summary>The FM volume (0–7F) that sounds like linear volume <paramref name="volume"/> (0–1).</summary>
    public static int FmVolume(double volume) => Steps(volume, MaxFmVolume, FmStepDb);

    /// <summary>
    /// The PSG volume (0–F) that sounds like linear volume <paramref name="volume"/> (0–1): the nearest of the chip's
    /// levels, which are not even steps (<see cref="PsgLevels"/>, docs/adr/0067-psg-volume-levels.md).
    /// </summary>
    public static int PsgVolume(double volume) => MaxPsgVolume - PsgLevels.Nearest(volume);

    /// <summary>
    /// The volume-column value for <paramref name="volume"/>, unrounded: full volume is <paramref name="max"/>, and each
    /// step down is <paramref name="stepDb"/> quieter.
    /// </summary>
    private static double ExactSteps(double volume, int max, double stepDb) =>
        volume <= 0 ? 0 : Math.Clamp(max + (20 * Math.Log10(volume) / stepDb), 0, max);

    /// <summary>
    /// The volume steps of <paramref name="volume"/> on an FM (<paramref name="psg"/> false) or PSG channel, unrounded; on
    /// the PSG, evenly in decibels between two of its levels (<see cref="PsgLevels.Attenuation"/>).
    /// </summary>
    public static double ExactVolume(double volume, bool psg) =>
        psg ? MaxPsgVolume - PsgLevels.Attenuation(volume) : ExactSteps(volume, MaxFmVolume, FmStepDb);

    /// <summary>The FM F-number of a pitch, <paramref name="semitones"/> from C-0 (only the place within the octave matters).</summary>
    public static double Fnum(double semitones)
    {
        double withinOctave = ((semitones % 12) + 12) % 12;
        return FnumOfC * Math.Pow(2, withinOctave / 12);
    }

    /// <summary>
    /// The F-number distance a slide covers moving <paramref name="move"/> semitones (either way) from <paramref name="semitones"/>:
    /// the F-number halfway along times the change in log-frequency. Measured at the middle, because the F-number
    /// halves where a slide crosses into the next octave's block (Furnace: octave boundary in pitch slides).
    /// </summary>
    public static double FnumDistance(double semitones, double move) =>
        Fnum(semitones + (move / 2)) * Math.Abs(move) * Math.Log(2) / 12;

    /// <summary>Semitones between an Amiga period and the same period moved <paramref name="periodStep"/> lower (a higher pitch).</summary>
    public static double Semitones(double period, double periodStep) =>
        period - periodStep <= 0 ? 0 : 12 * Math.Log2(period / (period - periodStep));

    /// <summary>A digit, rounded and kept within 1–15 (0 stays 0).</summary>
    public static int Digit(double value) => value <= 0 ? 0 : Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 1, 15);

    /// <summary>A byte value, rounded and kept within 1–255 (0 stays 0).</summary>
    public static int ByteValue(double value) => value <= 0 ? 0 : Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 1, 255);

    private static int Steps(double volume, int max, double stepDb) =>
        (int)Math.Round(ExactSteps(volume, max, stepDb), MidpointRounding.AwayFromZero);
}
