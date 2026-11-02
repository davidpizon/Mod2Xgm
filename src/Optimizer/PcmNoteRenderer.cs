using Mod2Xgm.Analysis;
using Mod2Xgm.Audio;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;
using Serilog.Core;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Plays one PCM copy through the target's PCM driver model on an NTSC Genesis, alone, and brings what the driver sends
/// to the DAC to <see cref="SampleAnalyzer.AnalysisRateHz"/>, so it can be measured as the sample is
/// (docs/adr/0071-pcm-candidates.md): DMF's DAC with the copy at 16 bits, XGM's and XGM2's drivers with it at 8 bits. The
/// DAC byte is the sound; the YM3438's own DAC path is left out, as it is the same for every copy.
/// </summary>
internal static class PcmNoteRenderer
{
    /// <summary>Taps of the resampling filter, as the FM renderer's at the same rate.</summary>
    private const int Taps = 64;

    /// <summary>The DAC byte for 0 (both drivers and DefleMask's DAC add <c>$80</c>).</summary>
    private const int DacZero = 0x80;

    /// <summary>The DAC enable bit of register <c>$2B</c>.</summary>
    private const byte DacOn = 0x80;

    /// <summary>The YM3438's sample rate on an NTSC Genesis, which the drivers are timed by.</summary>
    private static double ChipRateHz { get; } = Ym3438.SampleRateHz(Ym3438.NtscClockHz);

    /// <summary>Brings the chip's output to the analysis rate.</summary>
    private static readonly AnalysisResampler Resampler = new(ChipRateHz, Taps);

    /// <summary>Conversion notes about clipping go nowhere: a copy is only measured here.</summary>
    private static readonly ConversionNotes Silent = new(Logger.None);

    /// <summary>
    /// <paramref name="copy"/> (points at the target's play rate, −1…+1) played on channel 1 from its start for
    /// <paramref name="seconds"/>, at <see cref="SampleAnalyzer.AnalysisRateHz"/>; silence once it ends.
    /// </summary>
    public static float[] Render(PcmTarget target, float[] copy, double playRateHz, bool halfSpeed, double seconds)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        byte[] data = new byte[frames];
        byte[] enable = new byte[frames];
        if (copy.Length > 0)
        {
            IDacSource source = Start(target, copy, playRateHz, halfSpeed);
            source.Generate(data, enable);
        }

        return Resampler.Resample(ToSound(data, enable));
    }

    /// <summary>
    /// <paramref name="head"/> (points at <paramref name="playRateHz"/>) played on channel 1 from its start, then
    /// <paramref name="tail"/> (points at <paramref name="tailRateHz"/>) started on the same channel at
    /// <paramref name="switchSeconds"/>, as a pre-mixed sample's tail is (docs/adr/0072-pre-mix-candidates.md); the first
    /// <paramref name="seconds"/> at <see cref="SampleAnalyzer.AnalysisRateHz"/>.
    /// </summary>
    public static float[] Render(PcmTarget target, float[] head, double playRateHz, double seconds, float[] tail, double tailRateHz, double switchSeconds)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        byte[] data = new byte[frames];
        byte[] enable = new byte[frames];
        int at = Math.Clamp((int)Math.Round(switchSeconds * ChipRateHz), 0, frames);
        IDacSource source = Start(target, head, playRateHz, false);
        source.Generate(data.AsSpan(0, at), enable.AsSpan(0, at));
        switch (tail.Length > 0 ? source : null)
        {
            case DmfDac dac:
                dac.Play(SampleProcessor.ToSigned16(tail, string.Empty, Silent), (int)Math.Round(tailRateHz));
                break;
            case XgmPcmDriver xgm:
                xgm.Play(0, SampleProcessor.ToSigned8(tail, string.Empty, Silent), 0);
                break;
            case Xgm2PcmDriver xgm2:
                xgm2.Play(0, SampleProcessor.ToSigned8(tail, string.Empty, Silent), Xgm2PcmDriver.LowPriority, false);
                break;
        }

        source.Generate(data.AsSpan(at), enable.AsSpan(at));
        return Resampler.Resample(ToSound(data, enable));
    }

    /// <summary>The DAC's bytes as −1…+1, silent where the DAC is off.</summary>
    private static float[] ToSound(byte[] data, byte[] enable)
    {
        float[] sound = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            sound[i] = (enable[i] & DacOn) != 0 ? (data[i] - DacZero) / 128f : 0;
        }

        return sound;
    }

    /// <summary>The target's driver, playing <paramref name="copy"/> on its first channel.</summary>
    private static IDacSource Start(PcmTarget target, float[] copy, double playRateHz, bool halfSpeed)
    {
        switch (target)
        {
            case PcmTarget.Dmf:
                DmfDac dac = new(Ym3438.NtscClockHz);
                dac.SetDacMode(true);
                dac.Play(SampleProcessor.ToSigned16(copy, string.Empty, Silent), (int)Math.Round(playRateHz));
                return dac;
            case PcmTarget.Xgm:
                XgmPcmDriver xgm = new(buffered: false);
                xgm.Play(0, SampleProcessor.ToSigned8(copy, string.Empty, Silent), 0);
                return xgm;
            default:
                Xgm2PcmDriver xgm2 = new(buffered: false);
                xgm2.Play(0, SampleProcessor.ToSigned8(copy, string.Empty, Silent), Xgm2PcmDriver.LowPriority, halfSpeed);
                return xgm2;
        }
    }
}
