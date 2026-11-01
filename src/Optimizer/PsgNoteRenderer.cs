using Mod2Xgm.Analysis;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Plays one PSG try on the SN76489 model of an NTSC Genesis, alone, and brings it to
/// <see cref="SampleAnalyzer.AnalysisRateHz"/>, so it can be measured as the sample's regions are
/// (docs/adr/0070-psg-candidates.md). A tone plays on tone channel 1; noise on the noise channel, with tone channel 3
/// silent but counting at rate 3. The DC is taken off as the Genesis renderer does.
/// </summary>
internal static class PsgNoteRenderer
{
    /// <summary>The PSG's DC is taken off by a first-order high-pass at this frequency, as <c>GenesisChips</c> does.</summary>
    private const double DcBlockHz = 5;

    /// <summary>A square at the loudest volume swings this far either way once its DC is gone: half a channel's level, so it renders at ±1.</summary>
    private const double SquareAmplitude = Sn76489.FullScale / 8.0;

    /// <summary>
    /// Taps of the resampling filter: the PSG runs about 4.2 times as fast as the YM3438, so 4.2 times the FM renderer's
    /// 64 taps, for the same fall from 19.5 kHz to silence before 22 kHz.
    /// </summary>
    private const int Taps = 272;

    // SN76489 port bytes (docs/genesis-audio.md): bit 7 latches a register (bits 6–4) and writes its low 4 bits.
    private const byte LatchTone1 = 0x80;
    private const byte LatchVolume1 = 0x90;
    private const byte LatchTone3 = 0xc0;
    private const byte LatchNoise = 0xe0;
    private const byte LatchNoiseVolume = 0xf0;

    /// <summary>The noise register's white-noise bit.</summary>
    private const byte NoiseWhite = 0x04;

    /// <summary>The chip's sample rate: an NTSC Genesis's.</summary>
    private static double ChipRateHz { get; } = Sn76489.SampleRateHz(Sn76489.NtscClockHz);

    /// <summary>Brings the chip's output to the analysis rate.</summary>
    private static readonly AnalysisResampler Resampler = new(ChipRateHz, Taps);

    /// <summary><paramref name="setting"/> played for <paramref name="seconds"/>, at <see cref="SampleAnalyzer.AnalysisRateHz"/>.</summary>
    public static float[] Render(PsgSetting setting, double seconds)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        Sn76489 chip = new();
        if (setting.Sound == PsgSound.Tone)
        {
            WritePeriod(chip, LatchTone1, setting.Period);
        }
        else
        {
            if (setting.NoiseRate == PsgSetting.Tone3Rate)
            {
                WritePeriod(chip, LatchTone3, setting.Period);
            }

            chip.Write((byte)(LatchNoise | (setting.Sound == PsgSound.WhiteNoise ? NoiseWhite : 0) | (setting.NoiseRate & 3)));
        }

        byte latchVolume = setting.Sound == PsgSound.Tone ? LatchVolume1 : LatchNoiseVolume;
        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        int[] output = new int[frames];
        int start = 0;
        int? written = null;
        for (int step = 0; start < frames; step++)
        {
            int volume = setting.Volumes.Count == 0 ? PsgLevels.Off : setting.Volumes[Math.Min(step, setting.Volumes.Count - 1)];
            if (volume != written)
            {
                chip.Write((byte)(latchVolume | (volume & 0x0f)));
                written = volume;
            }

            int end = Math.Min(frames, (int)Math.Round((step + 1) * ChipRateHz / PsgSetting.StepsPerSecond));
            chip.Generate(output.AsSpan(start, end - start));
            start = end;
        }

        return Resampler.Resample(WithoutDc(output));
    }

    /// <summary>Writes a 10-bit tone period: the latch byte with its low 4 bits, then a data byte with its high 6.</summary>
    private static void WritePeriod(Sn76489 chip, byte latch, int period)
    {
        int p = Math.Clamp(period, 1, PsgSetting.MaxPeriod);
        chip.Write((byte)(latch | (p & 0x0f)));
        chip.Write((byte)((p >> 4) & 0x3f));
    }

    /// <summary>The chip's output in units of <see cref="SquareAmplitude"/>, through the DC-blocking high-pass.</summary>
    private static float[] WithoutDc(int[] output)
    {
        double pole = 1 - (2 * Math.PI * DcBlockHz / ChipRateHz);
        float[] sound = new float[output.Length];
        double input = 0;
        double filtered = 0;
        for (int i = 0; i < output.Length; i++)
        {
            double x = output[i] / SquareAmplitude;
            filtered = x - input + (pole * filtered);
            input = x;
            sound[i] = (float)filtered;
        }

        return sound;
    }
}
