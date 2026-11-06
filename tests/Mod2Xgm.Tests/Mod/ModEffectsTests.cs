using Mod2Xgm.Mod;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for <see cref="ModEffects"/> and <see cref="ModPlayers"/>: decoding MOD effect commands with ProTracker 2 and Ultimate Soundtracker rules (plan T3.3).</summary>
public sealed class ModEffectsTests
{
    private const PitchScale Period = PitchScale.AmigaPeriod;
    private const SlideTiming EveryTick = SlideTiming.EveryTickAfterFirst;
    private const SlideTiming RowStart = SlideTiming.OnceAtRowStart;

    /// <summary>
    /// ProTracker 2 commands, written as in a tracker, and the effects each decodes to. The effects are internal records,
    /// so they travel as <c>object</c>.
    /// </summary>
    public static TheoryData<int, object[]> ProTrackerCommands() => new()
    {
        // Zero parameters that are a step of 0 in ProTracker 2 do nothing.
        { 0x000, [] },
        { 0x100, [] },
        { 0x200, [] },
        { 0xA00, [] },
        { 0xE10, [] },
        { 0xE20, [] },
        { 0xE90, [] },
        { 0xEA0, [] },
        { 0xEB0, [] },
        { 0xED0, [] },
        { 0xEE0, [] },

        // 0xy: the note, then +x, then +y semitones.
        { 0x037, [new Arpeggio(3, 7)] },

        // 1xx / 2xx: slide by xx periods every tick after the first; up raises the pitch.
        { 0x110, [new PitchSlide(16, Period, EveryTick)] },
        { 0x2FF, [new PitchSlide(-255, Period, EveryTick)] },

        // 3xx: slide toward the note; 300 reuses the last speed.
        { 0x308, [new TonePortamento(8, Period)] },
        { 0x300, [new TonePortamento(null, Period)] },

        // 4xy: speed x / 64 cycles per tick, depth 255 × y / 128 periods; a 0 half reuses its last value.
        { 0x484, [new Vibrato(8 / 64.0, 255 * 4 / 128.0, Period)] },
        { 0x480, [new Vibrato(8 / 64.0, null, Period)] },
        { 0x404, [new Vibrato(null, 255 * 4 / 128.0, Period)] },
        { 0x400, [new Vibrato(null, null, Period)] },

        // 5xy: continue the tone portamento and slide the volume; x wins over y, and 500 only continues.
        { 0x520, [new TonePortamento(null, Period), new VolumeSlide(2 / 64.0, EveryTick)] },
        { 0x503, [new TonePortamento(null, Period), new VolumeSlide(-3 / 64.0, EveryTick)] },
        { 0x523, [new TonePortamento(null, Period), new VolumeSlide(2 / 64.0, EveryTick)] },
        { 0x500, [new TonePortamento(null, Period)] },

        // 6xy: continue the vibrato and slide the volume.
        { 0x610, [new Vibrato(null, null, Period), new VolumeSlide(1 / 64.0, EveryTick)] },
        { 0x600, [new Vibrato(null, null, Period)] },

        // 7xy: speed x / 64, depth 255 × y / 64 volume steps (of 64); 0 halves reuse their last value.
        { 0x748, [new Tremolo(4 / 64.0, 255 * 8 / 64.0 / 64)] },
        { 0x700, [new Tremolo(null, null)] },

        // 9xx: start xx × 256 frames in; 900 reuses the last offset.
        { 0x910, [new SampleOffset(4096)] },
        { 0x900, [new SampleOffset(null)] },

        // Axy: slide the volume up by x, or else down by y, in 64ths, every tick after the first.
        { 0xA40, [new VolumeSlide(4 / 64.0, EveryTick)] },
        { 0xA04, [new VolumeSlide(-4 / 64.0, EveryTick)] },
        { 0xA44, [new VolumeSlide(4 / 64.0, EveryTick)] },

        // Bxx: jump to order xx.
        { 0xB05, [new PositionJump(5)] },

        // Dxx: the row in decimal.
        { 0xD00, [new PatternBreak(0)] },
        { 0xD16, [new PatternBreak(16)] },
        { 0xD63, [new PatternBreak(63)] },

        // E0x: E00 turns the Amiga filter on, E01 off (only bit 0 counts).
        { 0xE00, [new SetLowPassFilter(true)] },
        { 0xE01, [new SetLowPassFilter(false)] },
        { 0xE02, [new SetLowPassFilter(true)] },

        // E1x / E2x: slide by x periods once, at the start of the row.
        { 0xE13, [new PitchSlide(3, Period, RowStart)] },
        { 0xE2F, [new PitchSlide(-15, Period, RowStart)] },

        // E3x: glissando on for any x but 0.
        { 0xE31, [new SetGlissando(true)] },
        { 0xE30, [new SetGlissando(false)] },

        // E5x: finetune, a signed nibble in eighths of a semitone (12.5 cents).
        { 0xE50, [new SetFineTune(0.0)] },
        { 0xE57, [new SetFineTune(87.5)] },
        { 0xE58, [new SetFineTune(-100.0)] },
        { 0xE5F, [new SetFineTune(-12.5)] },

        // E60 marks the loop start; E6x loops x more times.
        { 0xE60, [new PatternLoopStart()] },
        { 0xE63, [new PatternLoop(3)] },

        // E9x: retrigger every x ticks.
        { 0xE93, [new Retrigger(3)] },

        // EAx / EBx: slide the volume by x 64ths once, at the start of the row.
        { 0xEA2, [new VolumeSlide(2 / 64.0, RowStart)] },
        { 0xEB5, [new VolumeSlide(-5 / 64.0, RowStart)] },

        // ECx: cut the note on tick x; EC0 cuts it at once.
        { 0xEC3, [new NoteCut(3)] },
        { 0xEC0, [new NoteCut(0)] },

        // EDx delays the note to tick x; EEx holds the row for x extra rows.
        { 0xED2, [new NoteDelay(2)] },
        { 0xEE4, [new PatternDelay(4)] },

        // EFx: invert loop at speed x; EF0 stops it, so it is kept.
        { 0xEF5, [new InvertLoop(5)] },
        { 0xEF0, [new InvertLoop(0)] },

        // Fxx: F00 stops the song, 01–1F set the ticks per row, 20–FF the BPM.
        { 0xF00, [new StopSong()] },
        { 0xF01, [new SetSpeed(1)] },
        { 0xF1F, [new SetSpeed(31)] },
        { 0xF20, [new SetTempo(32)] },
        { 0xFFF, [new SetTempo(255)] },
    };

    /// <summary>Ultimate Soundtracker commands and the effects each decodes to.</summary>
    public static TheoryData<int, object[]> SoundtrackerCommands() => new()
    {
        // 1xy is arpeggio, and 0xy too from 03 up (smaller values do nothing).
        { 0x137, [new Arpeggio(3, 7)] },
        { 0x037, [new Arpeggio(3, 7)] },
        { 0x002, [] },
        { 0x100, [] },

        // 2xy bends the pitch: y up, or else x down; 200 does nothing.
        { 0x252, [new PitchSlide(2, Period, EveryTick)] },
        { 0x250, [new PitchSlide(-5, Period, EveryTick)] },
        { 0x200, [] },
    };

    /// <summary>Each ProTracker 2 command decodes to exactly its effects, with no volume and no issue.</summary>
    [Theory]
    [MemberData(nameof(ProTrackerCommands))]
    public void Decode_WithProTrackerCommand_ReturnsItsEffects(int command, object[] expected)
    {
        // Act
        DecodedEffect decoded = Decode(command);

        // Assert
        Assert.Equal(expected, decoded.Effects);
        Assert.Null(decoded.Volume);
        Assert.Null(decoded.Issue);
    }

    /// <summary><c>E4x</c>: bits 0–1 pick the vibrato wave (3, listed as random, plays square in ProTracker 2); bit 2 set keeps the wave running across new notes.</summary>
    [Theory]
    [InlineData(0, nameof(Waveform.Sine), true)]
    [InlineData(1, nameof(Waveform.RampDown), true)]
    [InlineData(2, nameof(Waveform.Square), true)]
    [InlineData(3, nameof(Waveform.Square), true)]
    [InlineData(4, nameof(Waveform.Sine), false)]
    [InlineData(7, nameof(Waveform.Square), false)]
    public void Decode_WithE4x_SetsVibratoWaveform(int x, string shape, bool retrigger)
    {
        // Act
        DecodedEffect decoded = Decode(0xE40 + x);

        // Assert
        Assert.Equal(new SetVibratoWaveform(Enum.Parse<Waveform>(shape), retrigger), Assert.Single(decoded.Effects));
    }

    /// <summary><c>E7x</c> picks the tremolo wave the same way as <c>E4x</c> picks the vibrato's.</summary>
    [Theory]
    [InlineData(0, nameof(Waveform.Sine), true)]
    [InlineData(1, nameof(Waveform.RampDown), true)]
    [InlineData(2, nameof(Waveform.Square), true)]
    [InlineData(3, nameof(Waveform.Square), true)]
    [InlineData(4, nameof(Waveform.Sine), false)]
    [InlineData(7, nameof(Waveform.Square), false)]
    public void Decode_WithE7x_SetsTremoloWaveform(int x, string shape, bool retrigger)
    {
        // Act
        DecodedEffect decoded = Decode(0xE70 + x);

        // Assert
        Assert.Equal(new SetTremoloWaveform(Enum.Parse<Waveform>(shape), retrigger), Assert.Single(decoded.Effects));
    }

    /// <summary><c>8xx</c> panning is ignored by ProTracker 2: kept as an unsupported effect, with an issue for the note.</summary>
    [Fact]
    public void Decode_WithPanning_ReturnsUnsupportedWithIssue()
    {
        // Act
        DecodedEffect decoded = Decode(0x880);

        // Assert
        UnsupportedEffect effect = Assert.IsType<UnsupportedEffect>(Assert.Single(decoded.Effects));
        Assert.Contains("8xx panning (880)", effect.Description, StringComparison.Ordinal);
        Assert.Equal(EffectIssue.PanIgnored, decoded.Issue);
    }

    /// <summary><c>Cxx</c> up to 64 sets the cell's volume, not an effect.</summary>
    [Theory]
    [InlineData(0xC00, 0.0)]
    [InlineData(0xC20, 0.5)]
    [InlineData(0xC40, 1.0)]
    public void Decode_WithSetVolume_ReturnsVolumeWithoutEffect(int command, double volume)
    {
        // Act
        DecodedEffect decoded = Decode(command);

        // Assert
        Assert.Empty(decoded.Effects);
        Assert.Equal(volume, decoded.Volume);
        Assert.Null(decoded.Issue);
    }

    /// <summary><c>Cxx</c> above 64 plays at 64, with an issue.</summary>
    [Theory]
    [InlineData(0xC41)]
    [InlineData(0xCFF)]
    public void Decode_WithSetVolumeAbove64_ReturnsFullVolumeWithIssue(int command)
    {
        // Act
        DecodedEffect decoded = Decode(command);

        // Assert
        Assert.Equal(1.0, decoded.Volume);
        Assert.Equal(EffectIssue.VolumeAbove64, decoded.Issue);
    }

    /// <summary>ProTracker's x × 10 + y also applies to <c>Dxx</c> digits above 9, with an issue.</summary>
    [Fact]
    public void Decode_WithNonDecimalBreakRow_ReadsTensAndUnitsWithIssue()
    {
        // Act
        DecodedEffect decoded = Decode(0xD1A);

        // Assert
        Assert.Equal(new PatternBreak(20), Assert.Single(decoded.Effects));
        Assert.Equal(EffectIssue.BreakRowNotDecimal, decoded.Issue);
    }

    /// <summary>A <c>Dxx</c> row above 63 breaks to row 0, as in ProTracker, with an issue.</summary>
    [Theory]
    [InlineData(0xD64)]
    [InlineData(0xD99)]
    [InlineData(0xDF0)]
    public void Decode_WithBreakRowAbove63_BreaksToRowZeroWithIssue(int command)
    {
        // Act
        DecodedEffect decoded = Decode(command);

        // Assert
        Assert.Equal(new PatternBreak(0), Assert.Single(decoded.Effects));
        Assert.Equal(EffectIssue.BreakRowAbove63, decoded.Issue);
    }

    /// <summary><c>E8x</c> has no meaning in ProTracker 2: kept as an unsupported effect, with an issue.</summary>
    [Fact]
    public void Decode_WithE8x_ReturnsUnsupportedWithIssue()
    {
        // Act
        DecodedEffect decoded = Decode(0xE83);

        // Assert
        Assert.Equal(new UnsupportedEffect("E8x with x = 3"), Assert.Single(decoded.Effects));
        Assert.Equal(EffectIssue.UnusedExtended, decoded.Issue);
    }

    /// <summary>Of every command 0–F with every parameter, only Cxx sets a volume.</summary>
    [Fact]
    public void Decode_WithEveryCommand_OnlyCxxSetsVolume()
    {
        // Arrange
        IEnumerable<int> commands = Enumerable.Range(0, 0x1000);

        // Act & Assert
        Assert.All(commands, command => Assert.Equal(command >> 8 == 0xC, Decode(command).Volume is not null));
    }

    /// <summary>Every command 0–F with every parameter decodes without throwing, to a list of effects.</summary>
    [Fact]
    public void Decode_WithEveryCommand_ReturnsEffectList()
    {
        // Arrange
        IEnumerable<int> commands = Enumerable.Range(0, 0x1000);

        // Act & Assert
        Assert.All(commands, command => Assert.NotNull(Decode(command).Effects));
    }

    /// <summary>Each Ultimate Soundtracker command decodes to its effects, with no issue.</summary>
    [Theory]
    [MemberData(nameof(SoundtrackerCommands))]
    public void Decode_WithSoundtrackerCommand_ReturnsItsEffects(int command, object[] expected)
    {
        // Act
        DecodedEffect decoded = DecodeSoundtracker(command);

        // Assert
        Assert.Equal(expected, decoded.Effects);
        Assert.Null(decoded.Issue);
    }

    /// <summary>Ultimate Soundtracker has no commands 3–F: each decodes to nothing (not even Cxx's volume) with an issue.</summary>
    [Theory]
    [InlineData(0x300)]
    [InlineData(0xA04)]
    [InlineData(0xC20)]
    [InlineData(0xD00)]
    [InlineData(0xF06)]
    public void Decode_WithSoundtrackerCommandAbove2_ReturnsNothingWithIssue(int command)
    {
        // Act
        DecodedEffect decoded = DecodeSoundtracker(command);

        // Assert
        Assert.Empty(decoded.Effects);
        Assert.Null(decoded.Volume);
        Assert.Equal(EffectIssue.NotInUltimateSoundtracker, decoded.Issue);
    }

    /// <summary>15-sample files default to Ultimate Soundtracker, others to ProTracker 2.</summary>
    [Theory]
    [InlineData(nameof(ModFormat.Soundtracker15), nameof(ModPlayer.UltimateSoundtracker))]
    [InlineData(nameof(ModFormat.ProTracker31), nameof(ModPlayer.ProTracker2))]
    public void DefaultFor_WithFormat_ReturnsItsPlayer(string format, string expected)
    {
        // Act
        ModPlayer player = ModPlayers.DefaultFor(Enum.Parse<ModFormat>(format));

        // Assert
        Assert.Equal(Enum.Parse<ModPlayer>(expected), player);
    }

    /// <summary>Each player has a name for people.</summary>
    [Theory]
    [InlineData(nameof(ModPlayer.UltimateSoundtracker), "Ultimate Soundtracker")]
    [InlineData(nameof(ModPlayer.ProTracker2), "ProTracker 2")]
    public void DisplayName_WithPlayer_ReturnsName(string player, string expected)
    {
        // Act
        string name = ModPlayers.DisplayName(Enum.Parse<ModPlayer>(player));

        // Assert
        Assert.Equal(expected, name);
    }

    /// <summary>The tempo byte: 0 and 0x78 are 125 BPM, others follow the CIA formula, 240 and up have no tempo.</summary>
    [Theory]
    [InlineData(0x78, 125.0)]
    [InlineData(0, 125.0)]
    [InlineData(100, 709_379.0 * 125 / 50 / (140 * 122))]
    [InlineData(240, null)]
    public void UltimateSoundtrackerBpm_WithTempoByte_ReturnsBpm(int tempoByte, double? expected)
    {
        // Act
        double? bpm = ModPlayers.UltimateSoundtrackerBpm(tempoByte);

        // Assert
        Assert.Equal(expected, bpm);
    }

    /// <summary>Effect numbers and parameters outside their ranges are caller bugs and throw.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(16, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 256)]
    public void Decode_WithOutOfRangeArgument_Throws(int effect, int parameter)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(effect, parameter));
    }

    /// <summary>An unknown player is a caller bug and throws.</summary>
    [Fact]
    public void Decode_WithUnknownPlayer_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ModEffects.Decode(0, 0, (ModPlayer)99));
    }

    /// <summary>Decodes <paramref name="command"/> written as in a tracker (e.g. 0xA04).</summary>
    private static DecodedEffect Decode(int command) => ModEffects.Decode(command >> 8, command & 0xFF);

    /// <summary>Decodes <paramref name="command"/> with Ultimate Soundtracker rules.</summary>
    private static DecodedEffect DecodeSoundtracker(int command) => ModEffects.Decode(command >> 8, command & 0xFF, ModPlayer.UltimateSoundtracker);
}
