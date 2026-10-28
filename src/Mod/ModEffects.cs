using System.Diagnostics;
using System.Globalization;
using Mod2Xgm.Model;

namespace Mod2Xgm.Mod;

/// <summary>Something decoding changed or ignored in one cell, reported by <see cref="ModToSong"/> as a conversion note.</summary>
internal enum EffectIssue
{
    /// <summary><c>Cxx</c> above 64: played at 64.</summary>
    VolumeAbove64,

    /// <summary><c>8xx</c> panning, which ProTracker 2 ignores.</summary>
    PanIgnored,

    /// <summary><c>Dxx</c> with a digit above 9: read as x × 10 + y anyway.</summary>
    BreakRowNotDecimal,

    /// <summary><c>Dxx</c> naming a row above 63: breaks to row 0.</summary>
    BreakRowAbove63,

    /// <summary><c>E8x</c>, which ProTracker 2 leaves unused.</summary>
    UnusedExtended,

    /// <summary>A command Ultimate Soundtracker doesn't have (3–F), ignored as it would.</summary>
    NotInUltimateSoundtracker,
}

/// <summary>What one cell's effect command means.</summary>
/// <param name="Effects">The effects, in the order the player applies them (empty when the command does nothing).</param>
/// <param name="Volume">The volume <c>Cxx</c> sets, 0–1, or <c>null</c>.</param>
/// <param name="Issue">Something decoding changed or ignored, or <c>null</c>.</param>
internal readonly record struct DecodedEffect(IReadOnlyList<Effect> Effects, double? Volume, EffectIssue? Issue);

/// <summary>
/// Turns a MOD cell's effect command (a number 0–F and a parameter byte) into song-model effects under a player's
/// rules (plans T3.3 and T3.3a, docs/formats/mod.md#effects-protracker). Decoding is per cell: what happens over time
/// (effect memory, slides, waveforms, flow) is the tick player's job (plan T3.4).
/// <para>ProTracker 2 rules: a zero parameter means "repeat the last value" only for <c>3xx</c>, <c>4xy</c> (each
/// half), <c>7xy</c> (each half) and <c>9xx</c>; for every other command it means a step of 0, so the command does
/// nothing and decodes to no effect.</para>
/// <para>Ultimate Soundtracker rules: only arpeggio (<c>1xy</c>, and <c>0xy</c> from 03 up) and pitch bend
/// (<c>2xy</c>: y up, or else x down) exist; every other command is ignored.</para>
/// </summary>
internal static class ModEffects
{
    // Effect numbers (docs/formats/mod.md#effects-protracker).
    private const int ArpeggioCommand = 0x0;
    private const int SlideUpCommand = 0x1;
    private const int SlideDownCommand = 0x2;
    private const int TonePortamentoCommand = 0x3;
    private const int VibratoCommand = 0x4;
    private const int TonePortamentoVolumeSlideCommand = 0x5;
    private const int VibratoVolumeSlideCommand = 0x6;
    private const int TremoloCommand = 0x7;
    private const int PanCommand = 0x8;
    private const int SampleOffsetCommand = 0x9;
    private const int VolumeSlideCommand = 0xA;
    private const int PositionJumpCommand = 0xB;
    private const int SetVolumeCommand = 0xC;
    private const int PatternBreakCommand = 0xD;
    private const int ExtendedCommand = 0xE;
    private const int SetSpeedCommand = 0xF;

    /// <summary>MOD volumes run 0–64.</summary>
    private const int MaxVolume = 64;

    /// <summary>Ultimate Soundtracker ignores <c>0xy</c> below this (OpenMPT: jackdance.mod uses 0xy arpeggios, smaller values aren't).</summary>
    private const int FirstSoundtrackerArpeggio = 0x03;

    /// <summary>Speeds below this are ticks per row; from it up, <c>Fxx</c> sets the BPM.</summary>
    private const int FirstTempo = 0x20;

    /// <summary>Patterns have 64 rows (0–63).</summary>
    private const int LastRow = 63;

    /// <summary><c>9xx</c> counts in steps of 256 bytes (one byte is one frame of 8-bit sound).</summary>
    private const int SampleOffsetStep = 256;

    /// <summary>
    /// Peak of ProTracker's vibrato/tremolo table (sine, ramp and square all reach 255). Vibrato moves the period by
    /// table × depth / 128, tremolo the volume by table × depth / 64 (volume 0–64).
    /// </summary>
    private const double WavePeak = 255;

    /// <summary>Vibrato and tremolo speed x advances a 64-step cycle by x steps per tick.</summary>
    private const double StepsPerCycle = 64;

    private static readonly DecodedEffect Nothing = new([], null, null);

    /// <summary>Decodes one effect command.</summary>
    /// <param name="effect">The effect number, 0–15.</param>
    /// <param name="parameter">The parameter byte, 0–255.</param>
    /// <param name="player">Whose rules to follow.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its range.</exception>
    public static DecodedEffect Decode(int effect, int parameter, ModPlayer player = ModPlayer.ProTracker2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(effect);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effect, 0xF);
        ArgumentOutOfRangeException.ThrowIfNegative(parameter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameter, 0xFF);
        int x = parameter >> 4;
        int y = parameter & 0xF;
        return player switch
        {
            ModPlayer.ProTracker2 => DecodeProTracker(effect, parameter, x, y),
            ModPlayer.UltimateSoundtracker => DecodeUltimateSoundtracker(effect, parameter, x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(player), player, "Unknown MOD player rules."),
        };
    }

    /// <summary>Ultimate Soundtracker: arpeggio and pitch bend; any other command is ignored.</summary>
    private static DecodedEffect DecodeUltimateSoundtracker(int effect, int parameter, int x, int y) => effect switch
    {
        ArpeggioCommand => parameter < FirstSoundtrackerArpeggio ? Nothing : Only(new Arpeggio(x, y)),
        SlideUpCommand => parameter == 0 ? Nothing : Only(new Arpeggio(x, y)),
        SlideDownCommand => (x, y) switch
        {
            (_, > 0) => Only(new PitchSlide(y, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)),
            (> 0, 0) => Only(new PitchSlide(-x, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)),
            _ => Nothing,
        },
        _ => new DecodedEffect([], null, EffectIssue.NotInUltimateSoundtracker),
    };

    /// <summary>ProTracker 2: all sixteen commands.</summary>
    private static DecodedEffect DecodeProTracker(int effect, int parameter, int x, int y)
    {
        return effect switch
        {
            ArpeggioCommand => parameter == 0 ? Nothing : Only(new Arpeggio(x, y)),
            SlideUpCommand => parameter == 0 ? Nothing : Only(new PitchSlide(parameter, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)),
            SlideDownCommand => parameter == 0 ? Nothing : Only(new PitchSlide(-parameter, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)),
            TonePortamentoCommand => Only(new TonePortamento(parameter == 0 ? null : parameter, PitchScale.AmigaPeriod)),
            VibratoCommand => Only(new Vibrato(Speed(x), VibratoDepth(y), PitchScale.AmigaPeriod)),
            TonePortamentoVolumeSlideCommand => WithVolumeSlide(new TonePortamento(null, PitchScale.AmigaPeriod), x, y),
            VibratoVolumeSlideCommand => WithVolumeSlide(new Vibrato(null, null, PitchScale.AmigaPeriod), x, y),
            TremoloCommand => Only(new Tremolo(Speed(x), TremoloDepth(y))),
            PanCommand => new DecodedEffect([Unsupported($"8xx panning (8{parameter:X2}); ProTracker 2 ignores it")], null, EffectIssue.PanIgnored),
            SampleOffsetCommand => Only(new SampleOffset(parameter == 0 ? null : parameter * SampleOffsetStep)),
            VolumeSlideCommand => VolumeSlideStep(x, y, SlideTiming.EveryTickAfterFirst) is { } slide ? Only(slide) : Nothing,
            PositionJumpCommand => Only(new PositionJump(parameter)),
            SetVolumeCommand => new DecodedEffect([], Math.Min(parameter, MaxVolume) / (double)MaxVolume, parameter > MaxVolume ? EffectIssue.VolumeAbove64 : null),
            PatternBreakCommand => DecodePatternBreak(x, y),
            ExtendedCommand => DecodeExtended(x, y),
            SetSpeedCommand => DecodeSpeed(parameter),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary><c>Dxx</c>: the row is written in decimal (BCD); ProTracker computes x × 10 + y even for digits above 9.</summary>
    private static DecodedEffect DecodePatternBreak(int x, int y)
    {
        int row = (x * 10) + y;
        if (row > LastRow)
        {
            return new DecodedEffect([new PatternBreak(0)], null, EffectIssue.BreakRowAbove63);
        }

        return new DecodedEffect([new PatternBreak(row)], null, x > 9 || y > 9 ? EffectIssue.BreakRowNotDecimal : null);
    }

    /// <summary><c>Exy</c>: x picks the command, y is its parameter.</summary>
    private static DecodedEffect DecodeExtended(int command, int y) => command switch
    {
        // E00 turns the Amiga's LED filter on, E01 off (bit 0 drives the LED line, which is active low).
        0x0 => Only(new SetLowPassFilter((y & 1) == 0)),
        0x1 => y == 0 ? Nothing : Only(new PitchSlide(y, PitchScale.AmigaPeriod, SlideTiming.OnceAtRowStart)),
        0x2 => y == 0 ? Nothing : Only(new PitchSlide(-y, PitchScale.AmigaPeriod, SlideTiming.OnceAtRowStart)),
        0x3 => Only(new SetGlissando(y != 0)),
        0x4 => Only(new SetVibratoWaveform(Shape(y), Retrigger: (y & 4) == 0)),
        0x5 => Only(new SetFineTune(SignedNibble(y) * AmigaPitch.CentsPerFineTune)),
        0x6 => Only(y == 0 ? new PatternLoopStart() : new PatternLoop(y)),
        0x7 => Only(new SetTremoloWaveform(Shape(y), Retrigger: (y & 4) == 0)),
        0x8 => new DecodedEffect([Unsupported($"E8x with x = {y}")], null, EffectIssue.UnusedExtended),
        0x9 => y == 0 ? Nothing : Only(new Retrigger(y)),
        0xA => y == 0 ? Nothing : Only(new VolumeSlide(y / (double)MaxVolume, SlideTiming.OnceAtRowStart)),
        0xB => y == 0 ? Nothing : Only(new VolumeSlide(-y / (double)MaxVolume, SlideTiming.OnceAtRowStart)),
        0xC => Only(new NoteCut(y)),
        0xD => y == 0 ? Nothing : Only(new NoteDelay(y)),
        0xE => y == 0 ? Nothing : Only(new PatternDelay(y)),
        _ => Only(new InvertLoop(y)),
    };

    /// <summary><c>Fxx</c>: 0 stops the song, below 0x20 sets the ticks per row, from 0x20 up the BPM.</summary>
    private static DecodedEffect DecodeSpeed(int parameter) => parameter switch
    {
        0 => Only(new StopSong()),
        < FirstTempo => Only(new SetSpeed(parameter)),
        _ => Only(new SetTempo(parameter)),
    };

    /// <summary><c>5xy</c>/<c>6xy</c>: the slide or vibrato continues with its remembered values, plus a volume slide.</summary>
    private static DecodedEffect WithVolumeSlide(Effect first, int x, int y) =>
        VolumeSlideStep(x, y, SlideTiming.EveryTickAfterFirst) is { } slide ? new DecodedEffect([first, slide], null, null) : Only(first);

    /// <summary>A volume slide by x up, or else y down (ProTracker ignores y when x is set); <c>null</c> when both are 0.</summary>
    private static VolumeSlide? VolumeSlideStep(int x, int y, SlideTiming timing) => (x, y) switch
    {
        (> 0, _) => new VolumeSlide(x / (double)MaxVolume, timing),
        (0, > 0) => new VolumeSlide(-y / (double)MaxVolume, timing),
        _ => null,
    };

    /// <summary>Vibrato or tremolo speed in cycles per tick; 0 keeps the last speed (<c>null</c>).</summary>
    private static double? Speed(int x) => x == 0 ? null : x / StepsPerCycle;

    /// <summary>Peak vibrato deviation in Amiga periods; 0 keeps the last depth (<c>null</c>).</summary>
    private static double? VibratoDepth(int y) => y == 0 ? null : WavePeak * y / 128;

    /// <summary>Peak tremolo deviation as a fraction of full volume; 0 keeps the last depth (<c>null</c>).</summary>
    private static double? TremoloDepth(int y) => y == 0 ? null : WavePeak * y / 64 / MaxVolume;

    /// <summary>ProTracker 2 waveforms: 0 sine, 1 ramp down, 2 square, and 3 (listed as random) also plays square.</summary>
    private static Waveform Shape(int y) => (y & 3) switch
    {
        0 => Waveform.Sine,
        1 => Waveform.RampDown,
        _ => Waveform.Square,
    };

    /// <summary>A 4-bit two's-complement value: 0–7 stay, 8–15 become −8…−1.</summary>
    private static int SignedNibble(int value) => value >= 8 ? value - 16 : value;

    private static DecodedEffect Only(Effect effect) => new([effect], null, null);

    private static UnsupportedEffect Unsupported(FormattableString description) =>
        new(description.ToString(CultureInfo.InvariantCulture));
}
