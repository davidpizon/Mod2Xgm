using Mod2Xgm.Cli;
using Mod2Xgm.Model.Window;

namespace Mod2Xgm.Tests.Cli;

/// <summary>Tests for <see cref="ValueParsers"/>: sample lists, byte sizes, durations and song positions as the command line writes them.</summary>
public sealed class ValueParsersTests
{
    /// <summary>Single numbers, lists, spaces, hex and ranges all parse into the set of samples.</summary>
    [Theory]
    [InlineData("7", new[] { 7 })]
    [InlineData("3,7,12-14", new[] { 3, 7, 12, 13, 14 })]
    [InlineData(" 1 , 2 ", new[] { 1, 2 })]
    [InlineData("0x1F", new[] { 31 })]
    [InlineData("0X0a-0x0C", new[] { 10, 11, 12 })]
    [InlineData("5-5", new[] { 5 })]
    public void TryParseSampleList_WithValidText_ReturnsNoErrorAndAddsSamples(string text, int[] expected)
    {
        // Arrange
        HashSet<int> samples = [];

        // Act
        string? error = ValueParsers.TryParseSampleList(text, 31, samples);

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, samples.Order());
    }

    /// <summary>Each kind of bad sample list is rejected with an error saying what is wrong.</summary>
    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("1,,2", "empty entry")]
    [InlineData("abc", "not a sample number")]
    [InlineData("0x", "not a sample number")]
    [InlineData("-3", "not a sample number")]
    [InlineData("14-12", "backwards")]
    [InlineData("0", "out of range")]
    [InlineData("32", "out of range")]
    [InlineData("30-33", "out of range")]
    public void TryParseSampleList_WithInvalidText_ReturnsErrorSayingWhy(string text, string expectedFragment)
    {
        // Act
        string? error = ValueParsers.TryParseSampleList(text, 31, new HashSet<int>());

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    /// <summary>Plain bytes and K/M suffixes, in either case and with spaces, parse to bytes.</summary>
    [Theory]
    [InlineData("131072", 131072L)]
    [InlineData("128K", 131072L)]
    [InlineData("128k", 131072L)]
    [InlineData("1M", 1048576L)]
    [InlineData(" 2m ", 2097152L)]
    public void TryParseByteSize_WithValidText_ReturnsBytes(string text, long expected)
    {
        // Act
        string? error = ValueParsers.TryParseByteSize(text, out long bytes);

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, bytes);
    }

    /// <summary>Empty, zero, negative, unknown-unit, fractional and overflowing sizes are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("0")]
    [InlineData("-5K")]
    [InlineData("12G")]
    [InlineData("1.5M")]
    [InlineData("9223372036854775807K")]
    public void TryParseByteSize_WithInvalidText_ReturnsError(string text)
    {
        // Act
        string? error = ValueParsers.TryParseByteSize(text, out _);

        // Assert
        Assert.NotNull(error);
    }

    /// <summary>Seconds (bare or <c>s</c>), minutes and hours parse to a duration.</summary>
    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("5M", 300)]
    [InlineData("1h", 3600)]
    public void TryParseDuration_WithValidText_ReturnsDuration(string text, int expectedSeconds)
    {
        // Act
        string? error = ValueParsers.TryParseDuration(text, out TimeSpan duration);

        // Assert
        Assert.Null(error);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    /// <summary>Empty, zero, unknown-unit, fractional, negative and overflowing durations are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("0s")]
    [InlineData("5d")]
    [InlineData("1.5m")]
    [InlineData("-5m")]
    [InlineData("2147483647h")]
    public void TryParseDuration_WithInvalidText_ReturnsError(string text)
    {
        // Act
        string? error = ValueParsers.TryParseDuration(text, out _);

        // Assert
        Assert.NotNull(error);
    }
    /// <summary>Seconds, m:ss and h:mm:ss, with or without decimals, parse to a time; 11:25 is minutes and seconds.</summary>
    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("125", 125.0)]
    [InlineData("125.5s", 125.5)]
    [InlineData("2:05", 125.0)]
    [InlineData("2:05.250", 125.25)]
    [InlineData("11:25", 685.0)]
    [InlineData("1:02:05", 3725.0)]
    [InlineData(" 90 ", 90.0)]
    public void TryParseSongPosition_WithTime_ReturnsTimePosition(string text, double expected)
    {
        // Act
        string? error = ValueParsers.TryParseSongPosition(text, out SongPosition? position);

        // Assert
        Assert.Null(error);
        Assert.Equal(new TimePosition(expected), position);
    }

    /// <summary>o and an order, optionally r and a row, in either case, parse to a tracker position; no row means row 0.</summary>
    [Theory]
    [InlineData("o11r25", 11, 25)]
    [InlineData("O3", 3, 0)]
    [InlineData("o0R63", 0, 63)]
    [InlineData("o127", 127, 0)]
    public void TryParseSongPosition_WithTrackerPosition_ReturnsTrackerPosition(string text, int order, int row)
    {
        // Act
        string? error = ValueParsers.TryParseSongPosition(text, out SongPosition? position);

        // Assert
        Assert.Null(error);
        Assert.Equal(new TrackerPosition(order, row), position);
    }

    /// <summary>Malformed times and positions outside a MOD are rejected with an error saying why.</summary>
    [Theory]
    [InlineData("2:5", "not a time")]
    [InlineData("2:65", "not a time")]
    [InlineData("-1", "not a time")]
    [InlineData("1.2.3", "not a time")]
    [InlineData("r25", "not a time")]
    [InlineData("soon", "not a time")]
    [InlineData("o11r64", "outside a MOD")]
    [InlineData("o128", "outside a MOD")]
    public void TryParseSongPosition_WithInvalidText_ReturnsErrorSayingWhy(string text, string expectedFragment)
    {
        // Act
        string? error = ValueParsers.TryParseSongPosition(text, out SongPosition? position);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
        Assert.Null(position);
    }

    /// <summary>A length of time is seconds or m:ss above 0.</summary>
    [Theory]
    [InlineData("4", 4.0)]
    [InlineData("2.5s", 2.5)]
    [InlineData("0:04", 4.0)]
    public void TryParseSongTime_WithValidText_ReturnsSeconds(string text, double expected)
    {
        // Act
        string? error = ValueParsers.TryParseSongTime(text, out double seconds);

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, seconds);
    }

    /// <summary>Zero, tracker positions and other text aren't lengths of time.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("o1r0")]
    [InlineData("long")]
    public void TryParseSongTime_WithInvalidText_ReturnsError(string text)
    {
        // Act
        string? error = ValueParsers.TryParseSongTime(text, out _);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("not a length of time", error, StringComparison.Ordinal);
    }
}
