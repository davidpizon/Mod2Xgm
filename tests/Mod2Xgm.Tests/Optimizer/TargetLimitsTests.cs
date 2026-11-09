using Mod2Xgm.Cli;
using Mod2Xgm.Optimizer;
using Serilog.Core;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="TargetLimits"/>: what each output can hold.</summary>
public sealed class TargetLimitsTests
{
    /// <summary>DMF has only its own limits: all 6 FM channels, one DAC voice, 144 slots, CH3 special and no byte limit.</summary>
    [Fact]
    public void For_WithDmf_HasItsLimits()
    {
        // Act
        TargetLimits sut = TargetLimits.For(PcmTarget.Dmf, 128 * 1024, 0);

        // Assert
        Assert.Equal(new TargetLimits(PcmTarget.Dmf, 6, true, 3, 1, 1, 144, null), sut);
    }

    /// <summary>XGM has 4 PCM voices, 63 slots and no CH3 special until the probe ROM of T11.1.</summary>
    [Fact]
    public void For_WithXgm_HasItsLimits()
    {
        // Act
        TargetLimits sut = TargetLimits.For(PcmTarget.Xgm, 128 * 1024, 0);

        // Assert
        Assert.Equal(new TargetLimits(PcmTarget.Xgm, 5, false, 3, 1, 4, 63, 128 * 1024), sut);
    }

    /// <summary>XGM2 has 3 PCM voices, 123 slots and CH3 special.</summary>
    [Fact]
    public void For_WithXgm2_HasItsLimits()
    {
        // Act
        TargetLimits sut = TargetLimits.For(PcmTarget.Xgm2, 128 * 1024, 0);

        // Assert
        Assert.Equal(new TargetLimits(PcmTarget.Xgm2, 5, true, 3, 1, 3, 123, 128 * 1024), sut);
    }

    /// <summary><c>--reserve-sfx-channels</c> takes PCM voices away, all of them if asked.</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Xgm), 1, 3)]
    [InlineData(nameof(PcmTarget.Xgm), 4, 0)]
    [InlineData(nameof(PcmTarget.Xgm2), 1, 2)]
    [InlineData(nameof(PcmTarget.Xgm2), 3, 0)]
    public void For_WithReservedVoices_LowersPcmVoices(string target, int reserved, int voices)
    {
        // Act
        TargetLimits sut = TargetLimits.For(Enum.Parse<PcmTarget>(target), 128 * 1024, reserved);

        // Assert
        Assert.Equal(voices, sut.PcmVoices);
    }

    /// <summary>Reserving more voices than the target has, or a budget of nothing, is a bug in the caller.</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Xgm), 128 * 1024, 5)]
    [InlineData(nameof(PcmTarget.Xgm2), 128 * 1024, 4)]
    [InlineData(nameof(PcmTarget.Dmf), 128 * 1024, 2)]
    [InlineData(nameof(PcmTarget.Xgm), 128 * 1024, -1)]
    [InlineData(nameof(PcmTarget.Xgm), 0, 0)]
    public void For_WithImpossibleValues_Throws(string target, long bytes, int reserved)
    {
        // Act and assert
        Assert.Throws<ArgumentOutOfRangeException>(() => TargetLimits.For(Enum.Parse<PcmTarget>(target), bytes, reserved));
    }

    /// <summary>The options choose the target, the budget and the reserved voices, and no note is needed.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm), nameof(PcmTarget.Xgm), 2)]
    [InlineData(nameof(OutputFormat.Xgm2), nameof(PcmTarget.Xgm2), 1)]
    public void For_WithOptions_ReadsThemWithoutANote(string format, string target, int voices)
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", Format = Enum.Parse<OutputFormat>(format), MaxPcmBytes = 1024, ReserveSfxChannels = 2 };
        ConversionNotes notes = new(Logger.None);

        // Act
        TargetLimits sut = TargetLimits.For(options, notes);

        // Assert
        Assert.Equal(Enum.Parse<PcmTarget>(target), sut.Target);
        Assert.Equal(voices, sut.PcmVoices);
        Assert.Equal(1024, sut.PcmBytes);
        Assert.Equal(0, notes.Count);
    }

    /// <summary>A plain run writes a DMF, which has no byte limit however big the budget, so nothing is cut and no note is added.</summary>
    [Fact]
    public void For_WithDefaultFormatAndHugeBudget_HasNoLimitAndNoNote()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", MaxPcmBytes = 20L * 1024 * 1024 };
        ConversionNotes notes = new(Logger.None);

        // Act
        TargetLimits sut = TargetLimits.For(options, notes);

        // Assert
        Assert.Equal(PcmTarget.Dmf, sut.Target);
        Assert.Null(sut.PcmBytes);
        Assert.Equal(0, notes.Count);
    }

    /// <summary>With no <c>--max-pcm-bytes</c>, XGM and XGM2 hold what the format can, with no note: the budget is the user's choice (ADR 0093).</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm))]
    [InlineData(nameof(OutputFormat.Xgm2))]
    public void For_WithNoBudget_HoldsWhatTheFormatCan(string format)
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", Format = Enum.Parse<OutputFormat>(format) };
        ConversionNotes notes = new(Logger.None);

        // Act
        TargetLimits sut = TargetLimits.For(options, notes);

        // Assert
        Assert.Equal(PcmTargets.MaxStoredBytes, sut.PcmBytes);
        Assert.Equal(0, notes.Count);
    }

    /// <summary>A budget XGM or XGM2 can't hold is cut to the format's most, and a conversion note says so.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm))]
    [InlineData(nameof(OutputFormat.Xgm2))]
    public void For_WithBudgetOverTheFormat_CutsItWithANote(string format)
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", Format = Enum.Parse<OutputFormat>(format), MaxPcmBytes = 20L * 1024 * 1024 };
        ConversionNotes notes = new(Logger.None);

        // Act
        TargetLimits sut = TargetLimits.For(options, notes);

        // Assert
        Assert.Equal(16_776_960, sut.PcmBytes);
        string note = Assert.Single(notes.Messages);
        Assert.Contains("--max-pcm-bytes 20971520", note, StringComparison.Ordinal);
        Assert.Contains("16776960", note, StringComparison.Ordinal);
    }

    /// <summary>A budget of exactly the format's most fits, so it needs no note.</summary>
    [Fact]
    public void For_WithBudgetAtTheFormatsMost_AddsNoNote()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", Format = OutputFormat.Xgm2, MaxPcmBytes = PcmTargets.MaxStoredBytes };
        ConversionNotes notes = new(Logger.None);

        // Act
        TargetLimits sut = TargetLimits.For(options, notes);

        // Assert
        Assert.Equal(PcmTargets.MaxStoredBytes, sut.PcmBytes);
        Assert.Equal(0, notes.Count);
    }
}
