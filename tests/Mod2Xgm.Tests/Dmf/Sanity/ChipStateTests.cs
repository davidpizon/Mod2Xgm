namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>Tests for <see cref="ChipState"/>: what each kind of write changes, as the sanity check compares it.</summary>
public sealed class ChipStateTests
{
    /// <summary>A frequency register write sets its value and is no event.</summary>
    [Fact]
    public void Apply_ForFrequencyRegister_SetsValue()
    {
        // Arrange
        ChipState sut = new();
        List<ChipChange> changes = [];

        // Act
        sut.Apply(ChipWrite.Fm(1, 0xA5, 0x22), changes);

        // Assert
        Assert.Equal([new ChipChange("FM1:A5", -1, 0x22, IsEvent: false)], changes);
    }

    /// <summary>A key-on is an event; the same key write again changes nothing.</summary>
    [Fact]
    public void Apply_ForRepeatedKeyOn_ReportsOneEvent()
    {
        // Arrange
        ChipState sut = new();
        List<ChipChange> changes = [];

        // Act
        sut.Apply(ChipWrite.Fm(0, 0x28, 0xF1), changes);
        sut.Apply(ChipWrite.Fm(0, 0x28, 0xF1), changes);

        // Assert
        Assert.Equal([new ChipChange("KEY 1", -1, 0xF0, IsEvent: true)], changes);
    }

    /// <summary>The DAC registers belong to the PCM players and are left out.</summary>
    [Fact]
    public void Apply_ForDacData_ChangesNothing()
    {
        // Arrange
        ChipState sut = new();
        List<ChipChange> changes = [];

        // Act
        sut.Apply(ChipWrite.Fm(0, 0x2A, 0x80), changes);

        // Assert
        Assert.Empty(changes);
    }

    /// <summary>A PSG latch and data byte make one 10-bit tone.</summary>
    [Fact]
    public void Apply_ForPsgLatchAndData_SetsTone()
    {
        // Arrange
        ChipState sut = new();
        List<ChipChange> changes = [];

        // Act
        sut.Apply(ChipWrite.Psg(0xA5), changes);
        sut.Apply(ChipWrite.Psg(0x12), changes);

        // Assert
        Assert.Equal(0x125, sut.Values["PSG tone 1"]);
    }

    /// <summary>A noise write is an event, so writing the same mode again still counts (it restarts the noise).</summary>
    [Fact]
    public void Apply_ForSameNoiseTwice_ReportsTwoEvents()
    {
        // Arrange
        ChipState sut = new();
        List<ChipChange> changes = [];

        // Act
        sut.Apply(ChipWrite.Psg(0xE4), changes);
        sut.Apply(ChipWrite.Psg(0xE4), changes);

        // Assert
        Assert.Equal(2, changes.Count(c => c is { Key: "PSG noise", IsEvent: true }));
    }

    /// <summary>Report categories follow the register ranges.</summary>
    [Theory]
    [InlineData("FM0:A4", "FM frequency")]
    [InlineData("FM1:4C", "FM total level")]
    [InlineData("FM0:30", "FM operator")]
    [InlineData("FM0:B2", "FM algorithm/feedback")]
    [InlineData("FM1:B6", "FM pan/LFO sensitivity")]
    [InlineData("KEY 4", "FM key")]
    [InlineData("PSG tone 0", "PSG tone")]
    [InlineData("PSG volume 3", "PSG volume")]
    [InlineData("PSG noise", "PSG noise")]
    [InlineData("PCM 0", "PCM")]
    public void Category_ForKey_ReturnsItsCategory(string key, string expected)
    {
        // Act
        string category = ChipState.Category(key);

        // Assert
        Assert.Equal(expected, category);
    }
}
