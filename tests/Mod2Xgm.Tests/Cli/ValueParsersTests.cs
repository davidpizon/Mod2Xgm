using Mod2Xgm.Cli;

namespace Mod2Xgm.Tests.Cli;

/// <summary>Tests for <see cref="ValueParsers"/>: sample lists, byte sizes and durations as the command line writes them.</summary>
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
}
