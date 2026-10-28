using Mod2Xgm.Mod;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Per-effect tests for decoding MOD effect commands with ProTracker 2 rules (plan T3.3).</summary>
public sealed class ModEffectsTests
{
    private const PitchScale Period = PitchScale.AmigaPeriod;
    private const SlideTiming EveryTick = SlideTiming.EveryTickAfterFirst;
    private const SlideTiming RowStart = SlideTiming.OnceAtRowStart;

    /// <summary>Decodes <paramref name="command"/> written as in a tracker (e.g. 0xA04) and returns its effects.</summary>
    private static IReadOnlyList<Effect> Effects(int command) => Decode(command).Effects;

    /// <summary>Decodes <paramref name="command"/> written as in a tracker (e.g. 0xA04).</summary>
    private static DecodedEffect Decode(int command) => ModEffects.Decode(command >> 8, command & 0xFF);

    /// <summary>Asserts that <paramref name="command"/> decodes to exactly <paramref name="expected"/>, with no volume and no issue.</summary>
    private static void DecodesTo(int command, params Effect[] expected)
    {
        DecodedEffect decoded = Decode(command);
        Assert.Equal(expected, decoded.Effects);
        Assert.Null(decoded.Volume);
        Assert.Null(decoded.Issue);
    }

    /// <summary>Commands whose zero parameter is a step of 0 in ProTracker 2 do nothing, so they decode to no effect.</summary>
    [Theory]
    [InlineData(0x000)]
    [InlineData(0x100)]
    [InlineData(0x200)]
    [InlineData(0xA00)]
    [InlineData(0xE10)]
    [InlineData(0xE20)]
    [InlineData(0xE90)]
    [InlineData(0xEA0)]
    [InlineData(0xEB0)]
    [InlineData(0xED0)]
    [InlineData(0xEE0)]
    public void ZeroParametersThatDoNothingDecodeToNoEffect(int command) => DecodesTo(command);

    /// <summary><c>0xy</c> plays the note, then +x, then +y semitones.</summary>
    [Fact]
    public void DecodesArpeggio() => DecodesTo(0x037, new Arpeggio(3, 7));

    /// <summary><c>1xx</c>/<c>2xx</c> slide by xx periods every tick after the first; up raises the pitch.</summary>
    [Fact]
    public void PitchSlides()
    {
        DecodesTo(0x110, new PitchSlide(16, Period, EveryTick));
        DecodesTo(0x2FF, new PitchSlide(-255, Period, EveryTick));
    }

    /// <summary><c>3xx</c> slides toward the note; 300 reuses the last speed.</summary>
    [Fact]
    public void DecodesTonePortamento()
    {
        DecodesTo(0x308, new TonePortamento(8, Period));
        DecodesTo(0x300, new TonePortamento(null, Period));
    }

    /// <summary>
    /// <c>4xy</c>: speed x / 64 cycles per tick, depth 255 × y / 128 periods at the peak; each half that is 0 reuses its
    /// last value.
    /// </summary>
    [Fact]
    public void DecodesVibrato()
    {
        DecodesTo(0x484, new Vibrato(8 / 64.0, 255 * 4 / 128.0, Period));
        DecodesTo(0x480, new Vibrato(8 / 64.0, null, Period));
        DecodesTo(0x404, new Vibrato(null, 255 * 4 / 128.0, Period));
        DecodesTo(0x400, new Vibrato(null, null, Period));
    }

    /// <summary><c>5xy</c> continues the tone portamento and slides the volume; x wins over y, and 500 only continues.</summary>
    [Fact]
    public void TonePortamentoWithVolumeSlide()
    {
        DecodesTo(0x520, new TonePortamento(null, Period), new VolumeSlide(2 / 64.0, EveryTick));
        DecodesTo(0x503, new TonePortamento(null, Period), new VolumeSlide(-3 / 64.0, EveryTick));
        DecodesTo(0x523, new TonePortamento(null, Period), new VolumeSlide(2 / 64.0, EveryTick));
        DecodesTo(0x500, new TonePortamento(null, Period));
    }

    /// <summary><c>6xy</c> continues the vibrato and slides the volume.</summary>
    [Fact]
    public void VibratoWithVolumeSlide()
    {
        DecodesTo(0x610, new Vibrato(null, null, Period), new VolumeSlide(1 / 64.0, EveryTick));
        DecodesTo(0x600, new Vibrato(null, null, Period));
    }

    /// <summary><c>7xy</c>: speed x / 64, depth 255 × y / 64 volume steps (of 64) at the peak; 0 halves reuse their last value.</summary>
    [Fact]
    public void DecodesTremolo()
    {
        DecodesTo(0x748, new Tremolo(4 / 64.0, 255 * 8 / 64.0 / 64));
        DecodesTo(0x700, new Tremolo(null, null));
    }

    /// <summary><c>8xx</c> panning is ignored by ProTracker 2: kept as an unsupported effect, with an issue for the note.</summary>
    [Fact]
    public void PanningIsIgnored()
    {
        DecodedEffect decoded = Decode(0x880);

        UnsupportedEffect effect = Assert.IsType<UnsupportedEffect>(Assert.Single(decoded.Effects));
        Assert.Contains("8xx panning (880)", effect.Description, StringComparison.Ordinal);
        Assert.Equal(EffectIssue.PanIgnored, decoded.Issue);
    }

    /// <summary><c>9xx</c> starts xx × 256 frames in; 900 reuses the last offset.</summary>
    [Fact]
    public void DecodesSampleOffset()
    {
        DecodesTo(0x910, new SampleOffset(4096));
        DecodesTo(0x900, new SampleOffset(null));
    }

    /// <summary><c>Axy</c> slides the volume up by x, or else down by y, in 64ths, every tick after the first.</summary>
    [Fact]
    public void DecodesVolumeSlide()
    {
        DecodesTo(0xA40, new VolumeSlide(4 / 64.0, EveryTick));
        DecodesTo(0xA04, new VolumeSlide(-4 / 64.0, EveryTick));
        DecodesTo(0xA44, new VolumeSlide(4 / 64.0, EveryTick));
    }

    /// <summary><c>Bxx</c> jumps to order xx.</summary>
    [Fact]
    public void DecodesPositionJump() => DecodesTo(0xB05, new PositionJump(5));

    /// <summary><c>Cxx</c> sets the cell's volume, not an effect; above 64 plays at 64 with an issue.</summary>
    [Theory]
    [InlineData(0xC00, 0.0, false)]
    [InlineData(0xC20, 0.5, false)]
    [InlineData(0xC40, 1.0, false)]
    [InlineData(0xC41, 1.0, true)]
    [InlineData(0xCFF, 1.0, true)]
    public void SetVolume(int command, double volume, bool above64)
    {
        DecodedEffect decoded = Decode(command);

        Assert.Empty(decoded.Effects);
        Assert.Equal(volume, decoded.Volume);
        Assert.Equal(above64 ? (EffectIssue?)EffectIssue.VolumeAbove64 : null, decoded.Issue);
    }

    /// <summary><c>Dxx</c> reads its row in decimal; ProTracker's x × 10 + y also applies to digits above 9.</summary>
    [Fact]
    public void DecodesPatternBreak()
    {
        DecodesTo(0xD00, new PatternBreak(0));
        DecodesTo(0xD16, new PatternBreak(16));
        DecodesTo(0xD63, new PatternBreak(63));

        DecodedEffect notDecimal = Decode(0xD1A);
        Assert.Equal(new PatternBreak(20), Assert.Single(notDecimal.Effects));
        Assert.Equal(EffectIssue.BreakRowNotDecimal, notDecimal.Issue);
    }

    /// <summary>A <c>Dxx</c> row above 63 breaks to row 0, as in ProTracker, with an issue.</summary>
    [Theory]
    [InlineData(0xD64)]
    [InlineData(0xD99)]
    [InlineData(0xDF0)]
    public void PatternBreakPastTheLastRowGoesToRowZero(int command)
    {
        DecodedEffect decoded = Decode(command);

        Assert.Equal(new PatternBreak(0), Assert.Single(decoded.Effects));
        Assert.Equal(EffectIssue.BreakRowAbove63, decoded.Issue);
    }

    /// <summary><c>E0x</c>: E00 turns the Amiga filter on, E01 off (only bit 0 counts).</summary>
    [Fact]
    public void Filter()
    {
        DecodesTo(0xE00, new SetLowPassFilter(true));
        DecodesTo(0xE01, new SetLowPassFilter(false));
        DecodesTo(0xE02, new SetLowPassFilter(true));
    }

    /// <summary><c>E1x</c>/<c>E2x</c> slide by x periods once, at the start of the row.</summary>
    [Fact]
    public void FinePitchSlides()
    {
        DecodesTo(0xE13, new PitchSlide(3, Period, RowStart));
        DecodesTo(0xE2F, new PitchSlide(-15, Period, RowStart));
    }

    /// <summary><c>E3x</c> turns glissando on for any x but 0.</summary>
    [Fact]
    public void Glissando()
    {
        DecodesTo(0xE31, new SetGlissando(true));
        DecodesTo(0xE30, new SetGlissando(false));
    }

    /// <summary>
    /// <c>E4x</c>/<c>E7x</c>: bits 0–1 pick the wave (3, listed as random, plays square in ProTracker 2); bit 2 set
    /// keeps the wave running across new notes.
    /// </summary>
    [Theory]
    [InlineData(0, nameof(Waveform.Sine), true)]
    [InlineData(1, nameof(Waveform.RampDown), true)]
    [InlineData(2, nameof(Waveform.Square), true)]
    [InlineData(3, nameof(Waveform.Square), true)]
    [InlineData(4, nameof(Waveform.Sine), false)]
    [InlineData(7, nameof(Waveform.Square), false)]
    public void Waveforms(int x, string shape, bool retrigger)
    {
        Waveform expected = Enum.Parse<Waveform>(shape);
        DecodesTo(0xE40 + x, new SetVibratoWaveform(expected, retrigger));
        DecodesTo(0xE70 + x, new SetTremoloWaveform(expected, retrigger));
    }

    /// <summary><c>E5x</c> sets the finetune: x is a signed nibble in eighths of a semitone (12.5 cents).</summary>
    [Theory]
    [InlineData(0x0, 0.0)]
    [InlineData(0x7, 87.5)]
    [InlineData(0x8, -100.0)]
    [InlineData(0xF, -12.5)]
    public void FineTune(int x, double cents) => DecodesTo(0xE50 + x, new SetFineTune(cents));

    /// <summary><c>E60</c> marks the loop start; <c>E6x</c> loops x more times.</summary>
    [Fact]
    public void DecodesPatternLoop()
    {
        DecodesTo(0xE60, new PatternLoopStart());
        DecodesTo(0xE63, new PatternLoop(3));
    }

    /// <summary><c>E8x</c> has no meaning in ProTracker 2: kept as an unsupported effect, with an issue.</summary>
    [Fact]
    public void UnusedE8()
    {
        DecodedEffect decoded = Decode(0xE83);

        Assert.Equal(new UnsupportedEffect("E8x with x = 3"), Assert.Single(decoded.Effects));
        Assert.Equal(EffectIssue.UnusedExtended, decoded.Issue);
    }

    /// <summary><c>E9x</c> retriggers every x ticks.</summary>
    [Fact]
    public void DecodesRetrigger() => DecodesTo(0xE93, new Retrigger(3));

    /// <summary><c>EAx</c>/<c>EBx</c> slide the volume by x 64ths once, at the start of the row.</summary>
    [Fact]
    public void FineVolumeSlides()
    {
        DecodesTo(0xEA2, new VolumeSlide(2 / 64.0, RowStart));
        DecodesTo(0xEB5, new VolumeSlide(-5 / 64.0, RowStart));
    }

    /// <summary><c>ECx</c> cuts the note on tick x; EC0 cuts it at once.</summary>
    [Fact]
    public void DecodesNoteCut()
    {
        DecodesTo(0xEC3, new NoteCut(3));
        DecodesTo(0xEC0, new NoteCut(0));
    }

    /// <summary><c>EDx</c> delays the note to tick x; <c>EEx</c> holds the row for x extra rows.</summary>
    [Fact]
    public void DelayCommands()
    {
        DecodesTo(0xED2, new NoteDelay(2));
        DecodesTo(0xEE4, new PatternDelay(4));
    }

    /// <summary><c>EFx</c> runs the invert loop at speed x; EF0 stops it, so it is kept.</summary>
    [Fact]
    public void DecodesInvertLoop()
    {
        DecodesTo(0xEF5, new InvertLoop(5));
        DecodesTo(0xEF0, new InvertLoop(0));
    }

    /// <summary><c>Fxx</c>: F00 stops the song, 01–1F set the ticks per row, 20–FF the BPM.</summary>
    [Fact]
    public void SpeedAndTempo()
    {
        DecodesTo(0xF00, new StopSong());
        DecodesTo(0xF01, new SetSpeed(1));
        DecodesTo(0xF1F, new SetSpeed(31));
        DecodesTo(0xF20, new SetTempo(32));
        DecodesTo(0xFFF, new SetTempo(255));
    }

    /// <summary>Every command 0–F with every parameter decodes without throwing, and only Cxx sets a volume.</summary>
    [Fact]
    public void EveryCommandDecodes()
    {
        for (int command = 0; command <= 0xFFF; command++)
        {
            DecodedEffect decoded = Decode(command);
            Assert.Equal(command >> 8 == 0xC, decoded.Volume is not null);
            Assert.NotNull(decoded.Effects);
        }

        Assert.Empty(Effects(0x000));
    }

    /// <summary>Effect numbers, parameters and player rules outside their ranges are caller bugs and throw.</summary>
    [Fact]
    public void OutOfRangeArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(0, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(0, 0, (ModPlayer)99));
    }
}
