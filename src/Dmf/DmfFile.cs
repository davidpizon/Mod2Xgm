namespace Mod2Xgm.Dmf;

/// <summary>
/// A Genesis DMF file as stored (docs/formats/dmf.md), read by the test-only <c>DmfReader</c> (plan T4.2) so
/// tests can check what <see cref="DmfWriter"/> (plan T5.2) wrote, field by field. Values are kept as the file stores them:
/// −1 for empty pattern fields, operators in file order, sample data as 16-bit values.
/// </summary>
/// <param name="Version">File version: 24 (what the writer makes) to 27 (newer DefleMask saves).</param>
/// <param name="System">Genesis, or Genesis with extended channel 3.</param>
/// <param name="Name">Song name.</param>
/// <param name="Author">Song author.</param>
/// <param name="HighlightA">First pattern highlight.</param>
/// <param name="HighlightB">Second pattern highlight.</param>
/// <param name="Timing">Time base, tick times and tick rate.</param>
/// <param name="RowsPerPattern">Rows in every pattern.</param>
/// <param name="Channels">One per channel, in file order (<see cref="DmfSystems.ChannelCount"/>).</param>
/// <param name="Instruments">Instruments, numbered from 0 as the pattern instrument column refers to them.</param>
/// <param name="Wavetables">Wavetables: each a list of values (none for Genesis).</param>
/// <param name="Samples">PCM samples, numbered from 0 as DAC notes pick them.</param>
/// <param name="TrailingBytes">Bytes left after the last sample; the writer leaves none.</param>
internal sealed record DmfFile(
    int Version,
    DmfSystem System,
    string Name,
    string Author,
    int HighlightA,
    int HighlightB,
    DmfTiming Timing,
    int RowsPerPattern,
    IReadOnlyList<DmfChannel> Channels,
    IReadOnlyList<DmfInstrument> Instruments,
    IReadOnlyList<IReadOnlyList<int>> Wavetables,
    IReadOnlyList<DmfSample> Samples,
    int TrailingBytes)
{
    /// <summary>Rows in the pattern matrix (the song length in orders).</summary>
    public int MatrixRows => Channels[0].Orders.Count;
}

/// <summary>The two Genesis systems a DMF can hold (docs/formats/dmf.md#2-system).</summary>
internal enum DmfSystem
{
    /// <summary>Genesis: FM1–6, PSG1–4 (10 channels).</summary>
    Genesis = 0x02,

    /// <summary>Genesis with extended channel 3: FM1, FM2, FM3 OP1–4, FM4–6, PSG1–4 (13 channels).</summary>
    GenesisExtendedChannel3 = 0x42,
}

/// <summary>Facts about each <see cref="DmfSystem"/>.</summary>
internal static class DmfSystems
{
    /// <summary>The PSG is always the last 4 channels.</summary>
    private const int PsgChannels = 4;

    /// <summary>Number of channels in the matrix and patterns.</summary>
    public static int ChannelCount(DmfSystem system) => system == DmfSystem.GenesisExtendedChannel3 ? 13 : 10;

    /// <summary>True for the PSG channels (volume 0–F); the others are FM (volume 0–7F).</summary>
    public static bool IsPsg(DmfSystem system, int channel) => channel >= ChannelCount(system) - PsgChannels;
}

/// <summary>Module timing (docs/formats/dmf.md#4-module-information).</summary>
/// <param name="TimeBase">Multiplies the tick times and speed effects by time base + 1; the writer uses 0.</param>
/// <param name="TickTime1">Ticks per row on rows 0, 2, 4, ….</param>
/// <param name="TickTime2">Ticks per row on rows 1, 3, 5, ….</param>
/// <param name="Ntsc">Frames mode: false = PAL (50 Hz), true = NTSC (60 Hz).</param>
/// <param name="UsingCustomHz">True when <see cref="CustomHz"/> replaces the PAL/NTSC rate.</param>
/// <param name="CustomHzBytes">The 3 custom Hz bytes as stored (ASCII digits, padded with 0).</param>
/// <param name="CustomHz">The custom Hz value the bytes spell, or <c>null</c> when they don't spell one (allowed only while custom Hz is off).</param>
internal sealed record DmfTiming(int TimeBase, int TickTime1, int TickTime2, bool Ntsc, bool UsingCustomHz, IReadOnlyList<byte> CustomHzBytes, int? CustomHz)
{
    /// <summary>The tick rate DefleMask plays at, in Hz (the reader requires <see cref="CustomHz"/> while custom Hz is on).</summary>
    public int TickRateHz => UsingCustomHz && CustomHz is { } hz ? hz : Ntsc ? 60 : 50;
}

/// <summary>One channel's matrix column and patterns (docs/formats/dmf.md#5-pattern-matrix).</summary>
/// <param name="Orders">Pattern index for each matrix row.</param>
/// <param name="PatternNames">Pattern name for each matrix row (file version 25 and later), or <c>null</c>.</param>
/// <param name="EffectColumns">Effect columns in this channel's patterns (1–4).</param>
/// <param name="Patterns">The pattern for each matrix row (stored once per matrix row, not per pattern index).</param>
internal sealed record DmfChannel(IReadOnlyList<int> Orders, IReadOnlyList<string>? PatternNames, int EffectColumns, IReadOnlyList<IReadOnlyList<DmfCell>> Patterns);

/// <summary>One row of one channel (docs/formats/dmf.md#8-patterns); −1 means empty.</summary>
/// <param name="Note">1 = C# … 11 = B, 12 = C (of the octave above), 0 = empty, 100 = note off.</param>
/// <param name="Octave">Octave (C is stored one octave down, with note 12).</param>
/// <param name="Volume">FM 0–7F, PSG 0–F, or −1.</param>
/// <param name="Effects">One per effect column.</param>
/// <param name="Instrument">Instrument number, or −1.</param>
internal sealed record DmfCell(int Note, int Octave, int Volume, IReadOnlyList<DmfEffect> Effects, int Instrument)
{
    /// <summary>True when nothing is set.</summary>
    public bool IsEmpty => Note == 0 && Octave == 0 && Volume == -1 && Instrument == -1 && Effects.All(e => e.IsEmpty);
}

/// <summary>One effect column of a cell; −1 means empty.</summary>
/// <param name="Code">Effect code (0–FF), or −1.</param>
/// <param name="Value">Effect value (0–FF), or −1.</param>
internal readonly record struct DmfEffect(int Code, int Value)
{
    /// <summary>An empty effect column.</summary>
    public static DmfEffect Empty => new(-1, -1);

    /// <summary>True when neither code nor value is set.</summary>
    public bool IsEmpty => Code == -1 && Value == -1;
}

/// <summary>An instrument: FM (FM channels) or STD (PSG channels); exactly one of the two is set.</summary>
/// <param name="Name">Instrument name.</param>
/// <param name="Fm">The FM patch, when the mode byte is 1.</param>
/// <param name="Std">The STD macros, when the mode byte is 0.</param>
internal sealed record DmfInstrument(string Name, DmfFmInstrument? Fm, DmfStdInstrument? Std);

/// <summary>An FM patch (docs/formats/dmf.md#6-instruments).</summary>
/// <param name="Algorithm">ALG 0–7.</param>
/// <param name="Feedback">FB 0–7.</param>
/// <param name="Fms">LFO frequency modulation sensitivity.</param>
/// <param name="Ams">LFO amplitude modulation sensitivity.</param>
/// <param name="Operators">4 operators in <b>file order</b>, which is register order: Yamaha OP1, OP3, OP2, OP4.</param>
internal sealed record DmfFmInstrument(int Algorithm, int Feedback, int Fms, int Ams, IReadOnlyList<DmfOperator> Operators);

/// <summary>One FM operator's 12 bytes, in file order.</summary>
/// <param name="Am">Amplitude modulation on/off.</param>
/// <param name="Ar">Attack rate.</param>
/// <param name="Dr">Decay rate.</param>
/// <param name="Mult">Frequency multiplier.</param>
/// <param name="Rr">Release rate.</param>
/// <param name="Sl">Sustain level.</param>
/// <param name="Tl">Total level.</param>
/// <param name="Dt2">Detune 2 (unused on the YM2612).</param>
/// <param name="Rs">Rate scaling.</param>
/// <param name="Dt">Detune, 0–7 with 3 = none.</param>
/// <param name="D2R">Second decay rate.</param>
/// <param name="SsgMode">SSG-EG: bit 3 enables, bits 0–2 are the mode.</param>
internal sealed record DmfOperator(int Am, int Ar, int Dr, int Mult, int Rr, int Sl, int Tl, int Dt2, int Rs, int Dt, int D2R, int SsgMode);

/// <summary>A STD instrument's macros (docs/formats/dmf.md#6-instruments).</summary>
/// <param name="Volume">Volume macro.</param>
/// <param name="Arpeggio">Arpeggio macro (values offset by 12 in normal mode).</param>
/// <param name="ArpeggioMode">0 = normal, 1 = fixed.</param>
/// <param name="DutyNoise">Duty / noise macro.</param>
/// <param name="Wavetable">Wavetable macro.</param>
internal sealed record DmfStdInstrument(DmfMacro Volume, DmfMacro Arpeggio, int ArpeggioMode, DmfMacro DutyNoise, DmfMacro Wavetable);

/// <summary>A macro: its values and loop position.</summary>
/// <param name="Values">0–127 values.</param>
/// <param name="Loop">Loop position, or −1 for none (an empty macro stores no loop byte, and reads as −1).</param>
internal sealed record DmfMacro(IReadOnlyList<int> Values, int Loop);

/// <summary>A PCM sample (docs/formats/dmf.md#9-pcm-samples).</summary>
/// <param name="Name">Sample name.</param>
/// <param name="RateCode">0–5 = 4000, 8000, 11025, 16000, 22050, 32000 Hz.</param>
/// <param name="Pitch">0–10, 5 = played as stored.</param>
/// <param name="Amp">Gain × 50; 50 = unchanged.</param>
/// <param name="Bits">8 or 16.</param>
/// <param name="CutStart">First sample kept (file version 27 and later), or <c>null</c>.</param>
/// <param name="CutEnd">End of the samples kept (file version 27 and later), or <c>null</c>.</param>
/// <param name="Data">The stored 2-byte values (signed 16-bit; for 8-bit samples each holds 0–255).</param>
internal sealed record DmfSample(string Name, int RateCode, int Pitch, int Amp, int Bits, int? CutStart, int? CutEnd, IReadOnlyList<short> Data)
{
    /// <summary>The rate each <see cref="RateCode"/> stands for, in Hz.</summary>
    private static readonly int[] RatesHz = [4000, 8000, 11025, 16000, 22050, 32000];

    /// <summary>The playback rate, in Hz.</summary>
    public int RateHz => RatesHz[RateCode];
}
