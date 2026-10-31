using System.Buffers.Binary;
using System.Text;
using Mod2Xgm.Render;

namespace Mod2Xgm.Tests.Render;

/// <summary>Tests for the WAV encoder behind <c>--render-wav</c>: the RIFF header, 16-bit little-endian points, clipping.</summary>
public sealed class WavEncoderTests
{
    /// <summary>The header is a plain 16-bit stereo PCM one with the right sizes and rate, and points are little-endian, left first.</summary>
    [Fact]
    public void WritesSixteenBitStereoPcm()
    {
        byte[] bytes = WavEncoder.Encode(new StereoAudio([0.5f, -1f], [0f, 1f], 22_050));

        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(36u + 8, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(22_050u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(16_384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46)));
        Assert.Equal(-32_767, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48)));
        Assert.Equal(32_767, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50)));
    }

    /// <summary>Points past full scale are clipped and counted on either channel.</summary>
    [Fact]
    public void PointsPastFullScaleAreClippedAndCounted()
    {
        StereoAudio audio = new([1.5f, 0.2f, -2f], [0f, 1.01f, 1f], 8000);

        byte[] bytes = WavEncoder.Encode(audio);

        Assert.Equal(3, WavEncoder.ClippedSamples(audio));
        Assert.Equal(32_767, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
        Assert.Equal(-32_767, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(52)));
    }
}
