using Mod2Xgm.Cli;

namespace Mod2Xgm.Tests.Cli;

public sealed class ValueParsersTests
{
    [Theory]
    [InlineData("7", new[] { 7 })]
    [InlineData("3,7,12-14", new[] { 3, 7, 12, 13, 14 })]
    [InlineData(" 1 , 2 ", new[] { 1, 2 })]
    [InlineData("0x1F", new[] { 31 })]
    [InlineData("0X0a-0x0C", new[] { 10, 11, 12 })]
    [InlineData("5-5", new[] { 5 })]
    public void SampleListParses(string text, int[] expected)
    {
        HashSet<int> samples = [];
        Assert.Null(ValueParsers.TryParseSampleList(text, 31, samples));
        Assert.Equal(expected, samples.Order());
    }

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
    public void SampleListRejects(string text, string expectedFragment)
    {
        string? error = ValueParsers.TryParseSampleList(text, 31, new HashSet<int>());
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("131072", 131072L)]
    [InlineData("128K", 131072L)]
    [InlineData("128k", 131072L)]
    [InlineData("1M", 1048576L)]
    [InlineData(" 2m ", 2097152L)]
    public void ByteSizeParses(string text, long expected)
    {
        Assert.Null(ValueParsers.TryParseByteSize(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("0")]
    [InlineData("-5K")]
    [InlineData("12G")]
    [InlineData("1.5M")]
    [InlineData("9223372036854775807K")]
    public void ByteSizeRejects(string text)
    {
        Assert.NotNull(ValueParsers.TryParseByteSize(text, out _));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("5M", 300)]
    [InlineData("1h", 3600)]
    public void DurationParses(string text, int expectedSeconds)
    {
        Assert.Null(ValueParsers.TryParseDuration(text, out TimeSpan duration));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("0s")]
    [InlineData("5d")]
    [InlineData("1.5m")]
    [InlineData("-5m")]
    [InlineData("2147483647h")]
    public void DurationRejects(string text)
    {
        Assert.NotNull(ValueParsers.TryParseDuration(text, out _));
    }
}
