using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Plays one held FM note on the YM3438 model, alone on channel 1 of an NTSC chip, and brings it to
/// <see cref="SampleAnalyzer.AnalysisRateHz"/>, so it can be measured as the sample's regions are
/// (docs/adr/0069-fm-patch-fitting.md). The key stays on: the plan holds an FM note until the next one, so a fitted
/// patch has to fade by its own decay, as the sample does.
/// </summary>
internal static class FmNoteRenderer
{
    /// <summary>One channel's output for a TL-0 sine carrier at its peak, so such a carrier renders at ±1, as a full-scale sample does (docs/adr/0050-voice-levels.md).</summary>
    private const double CarrierFullScale = Ym3438.FullScale / 6.0;

    // YM3438 registers (docs/genesis-audio.md).
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegKeyOn = 0x28;
    private const byte RegFnumLow = 0xa0;
    private const byte RegBlockFnumHigh = 0xa4;
    private const byte RegPanLfo = 0xb4;

    /// <summary>Register <c>$22</c>'s enable bit; the low 3 bits are the rate.</summary>
    private const byte LfoEnable = 0x08;

    /// <summary>Both outputs on, in register <c>$B4</c>.</summary>
    private const byte PanBoth = 0xc0;

    /// <summary>Register <c>$28</c>: all four operators of channel 1 on.</summary>
    private const byte KeyOnAll = 0xf0;

    // CH3 special mode (docs/genesis-audio.md, docs/adr/0073-ch3-locked-pairs.md).
    private const byte RegCh3Op1High = 0xad;
    private const byte RegCh3Op2High = 0xae;
    private const byte RegCh3Op3High = 0xac;

    /// <summary>A CH3 frequency's low byte is 4 registers below its high byte.</summary>
    private const byte Ch3LowOffset = 4;

    /// <summary>Register <c>$27</c> bit 6: channel 3's operators take their own frequencies.</summary>
    private const byte Ch3Special = 0x40;

    /// <summary>Channel 3, 0-based, as <see cref="FmVoice"/> and register <c>$28</c> number it.</summary>
    private const int Channel3 = 2;

    /// <summary>Register <c>$28</c>: OP1 and OP2 on.</summary>
    private const byte KeyOnOps12 = 0x30;

    /// <summary>Register <c>$28</c>: OP3 and OP4 on.</summary>
    private const byte KeyOnOps34 = 0xc0;

    /// <summary>Taps of the resampling filter: wide enough to fall from 19 kHz to silence before the analysis rate's 22 kHz.</summary>
    private const int Taps = 64;

    /// <summary>The chip's sample rate: an NTSC Genesis's.</summary>
    public static double ChipRateHz { get; } = Ym3438.SampleRateHz(Ym3438.NtscClockHz);

    /// <summary>Brings the chip's output to the analysis rate.</summary>
    private static readonly AnalysisResampler Resampler = new(ChipRateHz, Taps);

    /// <summary>
    /// <paramref name="patch"/> held at <paramref name="hz"/> for <paramref name="seconds"/>, with the LFO at
    /// <paramref name="lfoRate"/> (0–7) or off (<c>null</c>), at <see cref="SampleAnalyzer.AnalysisRateHz"/>.
    /// </summary>
    public static float[] Render(FmPatch patch, int? lfoRate, double hz, double seconds)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        Ym3438 chip = new();
        chip.WriteRegister(0, RegLfo, lfoRate is { } rate ? (byte)(LfoEnable | (rate & 7)) : (byte)0);
        chip.WriteRegister(0, RegCh3Mode, 0);
        FmVoice.LoadPatch(chip, 0, patch);
        FmVoice.Write(chip, 0, RegPanLfo, (byte)(PanBoth | (patch.Ams << 4) | patch.Fms));
        int blockFnum = FmVoice.BlockFnum(hz, ChipRateHz);
        FmVoice.Write(chip, 0, RegBlockFnumHigh, (byte)(blockFnum >> 8));
        FmVoice.Write(chip, 0, RegFnumLow, (byte)blockFnum);
        chip.WriteRegister(0, RegKeyOn, KeyOnAll);

        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        int[] left = new int[frames];
        int[] right = new int[frames];
        chip.Generate(left, right);
        float[] mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            mono[i] = (float)(left[i] / CarrierFullScale);
        }

        return ToAnalysisRate(mono);
    }

    /// <summary>
    /// Two 2-operator voices of the algorithm-4 <paramref name="patch"/> held together on channel 3 in CH3 special mode
    /// (docs/adr/0073-ch3-locked-pairs.md): OP1→OP2 at <paramref name="hz12"/>, OP3→OP4 at <paramref name="hz34"/>, each
    /// keyed on only when asked, for <paramref name="seconds"/>, without the LFO, at <see cref="SampleAnalyzer.AnalysisRateHz"/>.
    /// </summary>
    public static float[] RenderCh3(FmPatch patch, double hz12, double hz34, double seconds, bool key12 = true, bool key34 = true)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz12);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz34);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        Ym3438 chip = new();
        chip.WriteRegister(0, RegLfo, 0);
        chip.WriteRegister(0, RegCh3Mode, Ch3Special);
        FmVoice.LoadPatch(chip, Channel3, patch);
        FmVoice.Write(chip, Channel3, RegPanLfo, PanBoth);
        int blockFnum12 = FmVoice.BlockFnum(hz12, ChipRateHz);
        int blockFnum34 = FmVoice.BlockFnum(hz34, ChipRateHz);

        // Each operator's frequency: OP1 $AD/$A9, OP2 $AE/$AA, OP3 $AC/$A8, OP4 the channel's own (docs/genesis-audio.md).
        foreach ((byte high, int blockFnum) in (ReadOnlySpan<(byte, int)>)[(RegCh3Op1High, blockFnum12), (RegCh3Op2High, blockFnum12), (RegCh3Op3High, blockFnum34)])
        {
            chip.WriteRegister(0, high, (byte)(blockFnum >> 8));
            chip.WriteRegister(0, (byte)(high - Ch3LowOffset), (byte)blockFnum);
        }

        FmVoice.Write(chip, Channel3, RegBlockFnumHigh, (byte)(blockFnum34 >> 8));
        FmVoice.Write(chip, Channel3, RegFnumLow, (byte)blockFnum34);
        chip.WriteRegister(0, RegKeyOn, (byte)((key12 ? KeyOnOps12 : 0) | (key34 ? KeyOnOps34 : 0) | Channel3));

        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        int[] left = new int[frames];
        int[] right = new int[frames];
        chip.Generate(left, right);
        float[] mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            mono[i] = (float)(left[i] / CarrierFullScale);
        }

        return ToAnalysisRate(mono);
    }

    /// <summary><paramref name="input"/>, at <see cref="ChipRateHz"/>, at <see cref="SampleAnalyzer.AnalysisRateHz"/> (see <see cref="AnalysisResampler"/>).</summary>
    internal static float[] ToAnalysisRate(float[] input) => Resampler.Resample(input);
}
