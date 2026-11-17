using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="LoopShape"/>: which loops hold a tone (docs/adr/0117-tail-and-noise-loops.md).</summary>
public sealed class LoopShapeTests
{
    /// <summary>A sample without a loop has none.</summary>
    [Fact]
    public void Of_WithoutLoop_ReturnsNone()
    {
        // Arrange
        Sample sample = new("hit", Sine(4000, 50), null);

        // Act
        LoopKind kind = LoopShape.Of(sample, SampleTraits.Analyze(sample));

        // Assert
        Assert.Equal(LoopKind.None, kind);
    }

    /// <summary>A loop of a tone is a tone when the recording before it is short.</summary>
    [Theory]
    [InlineData(0, 32)]
    [InlineData(1000, 500)]
    [InlineData(2047, 64)]
    public void Of_WithToneLoop_ReturnsTone(int start, int length)
    {
        // Arrange
        Sample sample = new("tone", Sine(start + length, 32), new SampleLoop(start, length));

        // Act
        LoopKind kind = LoopShape.Of(sample, SampleTraits.Analyze(sample));

        // Assert
        Assert.Equal(LoopKind.Tone, kind);
    }

    /// <summary>A loop after a recording at least 2048 frames and four loops long is a tail.</summary>
    [Theory]
    [InlineData(2048, 512)]
    [InlineData(7434, 654)]
    public void Of_WithShortLoopAfterLongRecording_ReturnsTail(int start, int length)
    {
        // Arrange
        Sample sample = new("bell", Sine(start + length, 32), new SampleLoop(start, length));

        // Act
        LoopKind kind = LoopShape.Of(sample, SampleTraits.Analyze(sample));

        // Assert
        Assert.Equal(LoopKind.Tail, kind);
    }

    /// <summary>A long loop after a long recording is still a tone: the recording is under four loops long.</summary>
    [Fact]
    public void Of_WithLongLoopAfterLongRecording_ReturnsTone()
    {
        // Arrange
        Sample sample = new("pad", Sine(6000, 32), new SampleLoop(3000, 3000));

        // Act
        LoopKind kind = LoopShape.Of(sample, SampleTraits.Analyze(sample));

        // Assert
        Assert.Equal(LoopKind.Tone, kind);
    }

    /// <summary>A loop of noise is noise, even right at the sample's start.</summary>
    [Fact]
    public void Of_WithNoiseLoop_ReturnsNoise()
    {
        // Arrange
        Sample sample = new("hiss", [.. new Random(5).GetItems([-1f, 1f], 1000)], new SampleLoop(0, 1000));

        // Act
        LoopKind kind = LoopShape.Of(sample, SampleTraits.Analyze(sample));

        // Assert
        Assert.Equal(LoopKind.Noise, kind);
    }

    /// <summary>A sine of <paramref name="frames"/> frames with a cycle of <paramref name="period"/> frames.</summary>
    private static float[] Sine(int frames, double period) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin(2 * Math.PI * i / period))];
}
