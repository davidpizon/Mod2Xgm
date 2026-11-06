namespace Mod2Xgm.Tests;

/// <summary>Tests for <see cref="ConversionException"/>: the expected-failure exception carries its exit code.</summary>
public sealed class ConversionExceptionTests
{
    /// <summary>The inner exception and the exit code given to the constructor are kept.</summary>
    [Fact]
    public void Constructor_WithInnerException_KeepsInnerExceptionAndCode()
    {
        // Arrange
        IOException inner = new("disk");

        // Act
        ConversionException sut = new(ExitCode.InputNotFound, "unreadable", inner);

        // Assert
        Assert.Same(inner, sut.InnerException);
        Assert.Equal(ExitCode.InputNotFound, sut.Code);
    }
}
