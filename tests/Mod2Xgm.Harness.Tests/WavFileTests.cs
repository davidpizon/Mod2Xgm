using System.Buffers.Binary;
using System.Text;
using Mod2Xgm.Render;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="WavFile"/>: every sample format renderers export, and the files it rejects.</summary>
public sealed class WavFileTests
{
    /// <summary>Builds a WAV file: a fmt chunk of <paramref name="fmt"/>, any <paramref name="extraChunks"/> before it, and the data.</summary>
    private static byte[] Wav(byte[] fmt, byte[] data, params (string Id, byte[] Body)[] extraChunks)
    {
        List<byte> chunks = [];
        foreach ((string id, byte[] body) in extraChunks.Append(("fmt ", fmt)).Append(("data", data)))
        {
            chunks.AddRange(Encoding.ASCII.GetBytes(id));
            byte[] size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            chunks.AddRange(size);
            chunks.AddRange(body);
            if (body.Length % 2 == 1)
            {
                chunks.Add(0);
            }
        }

        byte[] riffSize = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(chunks.Count + 4));
        return [.. "RIFF"u8, .. riffSize, .. "WAVE"u8, .. chunks];
    }

    /// <summary>A plain 16-byte fmt chunk.</summary>
    private static byte[] Fmt(int format, int channels, int rate, int bits)
    {
        byte[] fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(rate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        return fmt;
    }

    /// <summary>What the harness writes reads back the same, to 16-bit precision, with clipping beyond ±1.</summary>
    [Fact]
    public void WrittenFilesReadBack()
    {
        StereoAudio audio = new([0f, 0.5f, -0.5f, 2f], [1f, -1f, 0.25f, -3f], 22_050);

        StereoAudio read = WavFile.Read(WavFile.ToBytes(audio));

        Assert.Equal(22_050, read.SampleRate);
        Assert.Equal([0f, 0.5f, -0.5f, 1f], read.Left.Select(s => MathF.Round(s, 3)));
        Assert.Equal([1f, -1f, 0.25f, -1f], read.Right.Select(s => MathF.Round(s, 3)));
    }

    /// <summary>The header fields <see cref="WavFile.ToBytes"/> writes: 16-bit stereo PCM, sizes and rates.</summary>
    [Fact]
    public void WrittenHeaderIsSixteenBitStereoPcm()
    {
        byte[] bytes = WavFile.ToBytes(new StereoAudio([0f, 0f], [0f, 0f], 44_100));

        Assert.Equal(52, bytes.Length);
        Assert.Equal(44u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(44_100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(176_400u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    /// <summary>Unsigned 8-bit mono: 128 is silence, and the one array serves both channels.</summary>
    [Fact]
    public void EightBitMonoIsRead()
    {
        StereoAudio audio = WavFile.Read(Wav(Fmt(1, 1, 8000, 8), [128, 192, 0]));

        Assert.Equal([0f, 0.5f, -1f], audio.Left);
        Assert.Same(audio.Left, audio.Right);
        Assert.Equal(8000, audio.SampleRate);
    }

    /// <summary>Signed 24-bit stereo, including a negative value whose sign is in the top byte.</summary>
    [Fact]
    public void TwentyFourBitStereoIsRead()
    {
        // Left 0x400000 (+0.5), right 0xC00000 (−0.5).
        StereoAudio audio = WavFile.Read(Wav(Fmt(1, 2, 48_000, 24), [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]));

        Assert.Equal([0.5f], audio.Left);
        Assert.Equal([-0.5f], audio.Right);
    }

    /// <summary>Signed 32-bit integer PCM.</summary>
    [Fact]
    public void ThirtyTwoBitIntegerIsRead()
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, int.MinValue / 4);

        Assert.Equal([-0.25f], WavFile.Read(Wav(Fmt(1, 1, 8000, 32), data)).Left);
    }

    /// <summary>32-bit float inside a WAVE_FORMAT_EXTENSIBLE header (what openmpt123 writes), after an odd-sized chunk that is padded.</summary>
    [Fact]
    public void ExtensibleFloatAfterAPaddedChunkIsRead()
    {
        byte[] fmt = new byte[40];
        Fmt(0xFFFE, 1, 44_100, 32).CopyTo(fmt, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), 3);
        byte[] data = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(data, 0.75f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), -0.125f);

        StereoAudio audio = WavFile.Read(Wav(fmt, data, ("LIST", [1, 2, 3])));

        Assert.Equal([0.75f, -0.125f], audio.Left);
    }

    /// <summary>A data chunk whose size runs past the end of the file is read up to the end.</summary>
    [Fact]
    public void AnOversizedDataChunkIsReadToTheEnd()
    {
        byte[] wav = Wav(Fmt(1, 1, 8000, 16), [0, 0x40, 0, 0xC0]);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(wav.Length - 8), 0xFFFFFFFF);

        Assert.Equal([0.5f, -0.5f], WavFile.Read(wav).Left);
    }

    /// <summary>Files that aren't WAVs, or hold audio the harness doesn't read, are rejected with the reason.</summary>
    [Theory]
    [InlineData("not-riff", "RIFF/WAVE")]
    [InlineData("no-data", "no data chunk")]
    [InlineData("data-first", "before any fmt chunk")]
    [InlineData("short-fmt", "at least 16 expected")]
    [InlineData("three-channels", "3 channels")]
    [InlineData("zero-rate", "sample rate 0 Hz")]
    [InlineData("twelve-bit", "12 bits per sample")]
    [InlineData("sixty-four-bit-float", "64 bits per sample")]
    public void BadFilesAreRejected(string kind, string expected)
    {
        byte[] wav = kind switch
        {
            "not-riff" => "RIFX....WAVE"u8.ToArray(),
            "no-data" => Wav(Fmt(1, 1, 8000, 16), [])[..36],
            "data-first" => [.. "RIFF"u8, 12, 0, 0, 0, .. "WAVEdata"u8, 0, 0, 0, 0],
            "short-fmt" => Wav(new byte[14], []),
            "three-channels" => Wav(Fmt(1, 3, 8000, 16), new byte[6]),
            "zero-rate" => Wav(Fmt(1, 1, 0, 16), new byte[2]),
            "twelve-bit" => Wav(Fmt(1, 1, 8000, 12), new byte[2]),
            _ => Wav(Fmt(3, 1, 8000, 64), new byte[8]),
        };

        HarnessException ex = Assert.Throws<HarnessException>(() => WavFile.Read(wav));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A file on disk is written and read back, and a bad one's message names the file.</summary>
    [Fact]
    public void FilesOnDiskNameThePathWhenBad()
    {
        using TempDirectory temp = new();
        string good = temp.Combine("good.wav");
        WavFile.Write(good, new StereoAudio([0.5f], [0.5f], 8000));
        string bad = temp.Combine("bad.wav");
        File.WriteAllBytes(bad, [1, 2, 3]);

        Assert.Equal([0.5f], WavFile.Read(good).Left.Select(s => MathF.Round(s, 3)));
        HarnessException ex = Assert.Throws<HarnessException>(() => WavFile.Read(bad));
        Assert.StartsWith(bad + ":", ex.Message, StringComparison.Ordinal);
    }
}
