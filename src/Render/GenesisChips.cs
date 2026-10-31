using Mod2Xgm.Cli;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Render;

/// <summary>Which player puts PCM on the YM3438's DAC (docs/adr/0058-pcm-driver-models.md).</summary>
internal enum PcmDriver
{
    /// <summary>DefleMask's FM6 DAC, as a DMF plays (<see cref="DmfDac"/>).</summary>
    Dmf,

    /// <summary>SGDK's XGM (v1) driver: four channels at about 14 kHz (<see cref="XgmPcmDriver"/>).</summary>
    Xgm,

    /// <summary>SGDK's XGM2 driver: three channels at about 13.3 kHz (<see cref="Xgm2PcmDriver"/>).</summary>
    Xgm2,
}

/// <summary>
/// The Genesis's sound hardware (plan T7.5, docs/adr/0059-genesis-renderer.md): the YM3438, the SN76489 and a PCM
/// driver model, run together at the YM3438's sample rate and mixed to stereo floats at Furnace's levels. Register
/// writes go straight to <see cref="Fm"/> and <see cref="Psg"/>; the PCM driver's <c>$2A</c> / <c>$2B</c> bytes are
/// written into the YM3438 sample by sample as <see cref="Render"/> plays.
/// </summary>
internal sealed class GenesisChips
{
    /// <summary>
    /// Where a full-scale DAC (and a TL-0 sine carrier at full volume) peaks in the mix: 0.381, measured from Furnace's
    /// render of the <c>levels</c> probe (tests/golden/dmf-probes; −11.42 dBFS RMS, as ADR 0050's table).
    /// </summary>
    public const double DacPeak = 0.381;

    /// <summary>
    /// The RMS of a PSG square at the loudest volume in the mix: −20.68 dBFS, measured from the same Furnace render, which
    /// has no DC on the PSG.
    /// </summary>
    public const double PsgSquareRms = 0.0925;

    /// <summary>The PSG's DC is taken off by a first-order high-pass at this frequency, like the coupling capacitors of a real console.</summary>
    public const double DcBlockHz = 5;

    /// <summary>The DAC data register: offset binary, <c>$80</c> = 0.</summary>
    private const byte RegDacData = 0x2a;

    /// <summary>The DAC enable register: bit 7 puts the DAC in FM channel 6's place.</summary>
    private const byte RegDacEnable = 0x2b;

    /// <summary>The YM3438's output for a full-scale DAC: the 9-bit DAC is one of six channels (<see cref="Ym3438.FullScale"/>).</summary>
    private const double YmDacFullScale = Ym3438.FullScale / 6.0;

    /// <summary>The SN76489's AC amplitude for a square at the loudest volume: one channel's level (a quarter of full scale), half of it either way.</summary>
    private const double PsgSquareAmplitude = Sn76489.FullScale / 8.0;

    private readonly double _psgPerSample;
    private readonly double _dcPole;
    private int[] _left = [];
    private int[] _right = [];
    private int[] _psg = [];
    private byte[] _dacData = [];
    private byte[] _dacEnable = [];
    private byte _writtenData;
    private byte _writtenEnable;
    private long _samples;
    private long _psgSamples;
    private double _dcIn;
    private double _dcOut;

    /// <summary>Creates the chips in their reset state for a Genesis of <paramref name="region"/>, with PCM played by <paramref name="driver"/>.</summary>
    public GenesisChips(Region region, PcmDriver driver)
    {
        double ymClock = region == Region.Ntsc ? Ym3438.NtscClockHz : Ym3438.PalClockHz;
        double psgClock = region == Region.Ntsc ? Sn76489.NtscClockHz : Sn76489.PalClockHz;
        SampleRateHz = Ym3438.SampleRateHz(ymClock);
        YmClockHz = ymClock;
        _psgPerSample = Sn76489.SampleRateHz(psgClock) / SampleRateHz;
        _dcPole = 1 - (2 * Math.PI * DcBlockHz / SampleRateHz);
        Driver = driver;
        Dac = driver switch
        {
            PcmDriver.Xgm => new XgmPcmDriver(),
            PcmDriver.Xgm2 => new Xgm2PcmDriver(),
            _ => new DmfDac(ymClock),
        };

        // The registers as they are after reset: DAC off at 0 ($80), which Ym3438 starts from too.
        _writtenData = 0x80;
        _writtenEnable = 0;
        Fm.WriteRegister(0, RegDacData, _writtenData);
        Fm.WriteRegister(0, RegDacEnable, _writtenEnable);
    }

    /// <summary>Output samples per second: the YM3438's (about 53 267 Hz on NTSC, 52 781 Hz on PAL).</summary>
    public double SampleRateHz { get; }

    /// <summary>The YM3438's master clock.</summary>
    public double YmClockHz { get; }

    /// <summary>The PCM driver model in use.</summary>
    public PcmDriver Driver { get; }

    /// <summary>The FM chip; write its registers directly.</summary>
    public Ym3438 Fm { get; } = new();

    /// <summary>The PSG; write its port directly.</summary>
    public Sn76489 Psg { get; } = new();

    /// <summary>The PCM driver: a <see cref="DmfDac"/>, <see cref="XgmPcmDriver"/> or <see cref="Xgm2PcmDriver"/> by <see cref="Driver"/>.</summary>
    public IDacSource Dac { get; }

    /// <summary>
    /// Plays one sample per element of <paramref name="left"/> and <paramref name="right"/> (the same length): the
    /// YM3438 with the PCM driver's DAC bytes, plus the PSG averaged over each sample, in −1…+1 at Furnace's levels.
    /// </summary>
    public void Render(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Both channels need the same length.", nameof(right));
        }

        int n = left.Length;
        if (n == 0)
        {
            return;
        }

        Grow(ref _left, n);
        Grow(ref _right, n);
        Grow(ref _dacData, n);
        Grow(ref _dacEnable, n);
        Dac.Generate(_dacData.AsSpan(0, n), _dacEnable.AsSpan(0, n));
        for (int i = 0; i < n;)
        {
            if (_dacData[i] != _writtenData)
            {
                _writtenData = _dacData[i];
                Fm.WriteRegister(0, RegDacData, _writtenData);
            }

            if (_dacEnable[i] != _writtenEnable)
            {
                _writtenEnable = _dacEnable[i];
                Fm.WriteRegister(0, RegDacEnable, _writtenEnable);
            }

            int end = i + 1;
            while (end < n && _dacData[end] == _writtenData && _dacEnable[end] == _writtenEnable)
            {
                end++;
            }

            Fm.Generate(_left.AsSpan(i, end - i), _right.AsSpan(i, end - i));
            i = end;
        }

        long psgEnd = PsgSampleAt(_samples + n);
        int psgCount = (int)(psgEnd - _psgSamples);
        Grow(ref _psg, psgCount);
        Psg.Generate(_psg.AsSpan(0, psgCount));
        const double fmScale = DacPeak / YmDacFullScale;
        const double psgScale = PsgSquareRms / PsgSquareAmplitude;
        int from = 0;
        for (int i = 0; i < n; i++)
        {
            int to = (int)(PsgSampleAt(_samples + i + 1) - _psgSamples);
            double sum = 0;
            for (int k = from; k < to; k++)
            {
                sum += _psg[k];
            }

            double psg = to > from ? sum / (to - from) : _dcIn;
            from = to;
            _dcOut = psg - _dcIn + (_dcPole * _dcOut);
            _dcIn = psg;
            double mono = _dcOut * psgScale;
            left[i] = (float)((_left[i] * fmScale) + mono);
            right[i] = (float)((_right[i] * fmScale) + mono);
        }

        _samples += n;
        _psgSamples = psgEnd;
    }

    /// <summary>The PSG sample the YM3438's sample <paramref name="sample"/> starts on.</summary>
    private long PsgSampleAt(long sample) => (long)Math.Floor(sample * _psgPerSample);

    private static void Grow<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new T[Math.Max(length, buffer.Length * 2)];
        }
    }
}
