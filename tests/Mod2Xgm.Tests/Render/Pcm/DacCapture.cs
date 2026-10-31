using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>What a DAC source puts in <c>$2A</c> and <c>$2B</c> over a run of YM3438 samples.</summary>
internal sealed record DacCapture(byte[] Data, byte[] Enable)
{
    /// <summary>Plays <paramref name="samples"/> YM3438 samples of <paramref name="source"/>.</summary>
    public static DacCapture Of(IDacSource source, int samples)
    {
        byte[] data = new byte[samples];
        byte[] enable = new byte[samples];
        source.Generate(data, enable);
        return new DacCapture(data, enable);
    }

    /// <summary>A sample of distinct non-zero values, 1, 2, … 127, 1, 2, …, so every new byte shows as a change.</summary>
    public static sbyte[] Ramp(int length) => [.. Enumerable.Range(0, length).Select(i => (sbyte)((i % 127) + 1))];

    /// <summary>The signed value the DAC byte at <paramref name="index"/> stands for.</summary>
    public int Value(int index) => Data[index] - 0x80;

    /// <summary>The sample indexes where the DAC byte changes from the one before.</summary>
    public List<int> Changes() => [.. Enumerable.Range(1, Data.Length - 1).Where(i => Data[i] != Data[i - 1])];

    /// <summary>The first sample index where the DAC is off, or −1.</summary>
    public int FirstOff() => Array.IndexOf(Enable, (byte)0);
}
