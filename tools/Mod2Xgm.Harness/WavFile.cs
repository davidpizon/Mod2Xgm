using System.Buffers.Binary;
using System.Text;

namespace Mod2Xgm.Harness;

/// <summary>
/// Reads and writes RIFF WAVE files, little-endian throughout: what DefleMask, Furnace and emulators export (8-, 16-,
/// 24- and 32-bit integer PCM, 32-bit float, mono or stereo, plain or <c>WAVE_FORMAT_EXTENSIBLE</c>), and 16-bit stereo
/// for the harness's own renders.
/// </summary>
internal static class WavFile
{
    // WAVE format tags (mmreg.h).
    private const int FormatPcm = 1;
    private const int FormatFloat = 3;
    private const int FormatExtensible = 0xFFFE;

    // A plain fmt chunk is 16 bytes; the extensible one carries the real format tag in the first two bytes of its
    // sub-format GUID, at offset 24.
    private const int FmtMinBytes = 16;
    private const int ExtensibleSubFormatOffset = 24;

    /// <summary>Reads <paramref name="path"/>; throws <see cref="HarnessException"/> naming the file and byte offset when it can't.</summary>
    public static StereoAudio Read(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (HarnessException ex)
        {
            throw new HarnessException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Reads a WAV file from <paramref name="data"/>.</summary>
    public static StereoAudio Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || Tag(data, 0) != "RIFF" || Tag(data, 8) != "WAVE")
        {
            throw new HarnessException("not a WAV file (no RIFF/WAVE header at byte offset 0)");
        }

        int format = -1;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            string id = Tag(data, offset);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            int start = offset + 8;

            // A writer that never went back to fill in the size (or got it wrong) is common: take what is there.
            int length = (int)Math.Min(size, data.Length - start);
            ReadOnlySpan<byte> body = data.Slice(start, length);
            if (id == "fmt ")
            {
                if (body.Length < FmtMinBytes)
                {
                    throw new HarnessException($"fmt chunk at byte offset {offset} is {body.Length} bytes; at least {FmtMinBytes} expected");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == FormatExtensible && body.Length >= ExtensibleSubFormatOffset + 2)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[ExtensibleSubFormatOffset..]);
                }
            }
            else if (id == "data")
            {
                if (format < 0)
                {
                    throw new HarnessException($"data chunk at byte offset {offset} comes before any fmt chunk");
                }

                return Decode(body, format, channels, rate, bits, start);
            }

            // Chunks are padded to an even length.
            offset = start + (int)Math.Min(size + (size & 1), data.Length - start);
        }

        throw new HarnessException("no data chunk");
    }

    /// <summary>Writes <paramref name="audio"/> as 16-bit stereo PCM; anything outside −1…+1 is clipped.</summary>
    public static void Write(string path, StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        File.WriteAllBytes(path, ToBytes(audio));
    }

    /// <summary>The bytes <see cref="Write"/> writes.</summary>
    public static byte[] ToBytes(StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        const int channels = 2;
        const int bytesPerSample = 2;
        int dataBytes = audio.Length * channels * bytesPerSample;
        byte[] bytes = new byte[44 + dataBytes];
        Span<byte> span = bytes;
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ", span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], FmtMinBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(audio.SampleRate * channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], channels * bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], bytesPerSample * 8);
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (int i = 0; i < audio.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 4))..], ToInt16(audio.Left[i]));
            BinaryPrimitives.WriteInt16LittleEndian(span[(46 + (i * 4))..], ToInt16(audio.Right[i]));
        }

        return bytes;
    }

    private static short ToInt16(float sample) => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    private static StereoAudio Decode(ReadOnlySpan<byte> body, int format, int channels, int rate, int bits, int offset)
    {
        if (channels is < 1 or > 2)
        {
            throw new HarnessException($"{channels} channels; only mono and stereo are read (data at byte offset {offset})");
        }

        if (rate <= 0)
        {
            throw new HarnessException($"sample rate {rate} Hz");
        }

        bool supported = (format == FormatPcm && bits is 8 or 16 or 24 or 32) || (format == FormatFloat && bits == 32);
        if (!supported)
        {
            throw new HarnessException($"format {format} with {bits} bits per sample isn't read; 8/16/24/32-bit PCM or 32-bit float expected");
        }

        int bytesPerSample = bits / 8;
        int frames = body.Length / (bytesPerSample * channels);
        float[] left = new float[frames];
        float[] right = channels == 2 ? new float[frames] : left;
        for (int i = 0; i < frames; i++)
        {
            int at = i * bytesPerSample * channels;
            left[i] = Sample(body[at..], format, bits);
            if (channels == 2)
            {
                right[i] = Sample(body[(at + bytesPerSample)..], format, bits);
            }
        }

        return new StereoAudio(left, right, rate);
    }

    private static float Sample(ReadOnlySpan<byte> at, int format, int bits) => (format, bits) switch
    {
        (FormatFloat, _) => BinaryPrimitives.ReadSingleLittleEndian(at),
        (_, 8) => (at[0] - 128) / 128f,
        (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
        (_, 24) => (at[0] | (at[1] << 8) | ((sbyte)at[2] << 16)) / 8388608f,
        _ => BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648f,
    };

    private static string Tag(ReadOnlySpan<byte> data, int offset) => Encoding.ASCII.GetString(data.Slice(offset, 4));
}
