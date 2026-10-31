using System.Buffers.Binary;
using System.Text;

namespace Mod2Xgm.Render;

/// <summary>
/// Writes audio as a RIFF WAVE file, little-endian throughout (docs/cli.md, <c>--render-wav</c>): 16-bit stereo PCM, the
/// plainest format every player reads. The accuracy harness writes its renders with it too.
/// </summary>
internal static class WavEncoder
{
    /// <summary>WAVE format tag of integer PCM (mmreg.h).</summary>
    private const int FormatPcm = 1;

    /// <summary>A plain fmt chunk is 16 bytes.</summary>
    private const int FmtBytes = 16;

    /// <summary>The RIFF header, the fmt chunk and the data chunk's header.</summary>
    private const int HeaderBytes = 44;

    private const int Channels = 2;
    private const int BytesPerSample = 2;

    /// <summary>The WAV file of <paramref name="audio"/>: 16-bit stereo PCM; anything outside −1…+1 is clipped.</summary>
    public static byte[] Encode(StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        int dataBytes = audio.Length * Channels * BytesPerSample;
        byte[] bytes = new byte[HeaderBytes + dataBytes];
        Span<byte> span = bytes;
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(HeaderBytes - 8 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ", span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], FmtBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(audio.SampleRate * Channels * BytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], Channels * BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], BytesPerSample * 8);
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (int i = 0; i < audio.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(HeaderBytes + (i * 4))..], ToInt16(audio.Left[i]));
            BinaryPrimitives.WriteInt16LittleEndian(span[(HeaderBytes + 2 + (i * 4))..], ToInt16(audio.Right[i]));
        }

        return bytes;
    }

    /// <summary>How many samples of <paramref name="audio"/>, on either channel, lie outside −1…+1 and are clipped by <see cref="Encode"/>.</summary>
    public static int ClippedSamples(StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        int clipped = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            clipped += (Math.Abs(audio.Left[i]) > 1 ? 1 : 0) + (Math.Abs(audio.Right[i]) > 1 ? 1 : 0);
        }

        return clipped;
    }

    private static short ToInt16(float sample) => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
}
