using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// FM channel 6's DAC as DefleMask plays a DMF (docs/adr/0058-pcm-driver-models.md, docs/formats/dmf.md#dac-samples-on-fm6):
/// one sample at a time, at its own rate, with no volume. The behavior follows Furnace's DMF playback
/// (<c>src/engine/platform/genesis.cpp</c>, with the <c>brokenDACMode</c> and <c>noOPN2Vol</c> flags Furnace sets for
/// every DMF), which matched DefleMask's exports of the DAC probes; no Furnace code is copied.
/// <para>On every YM3438 sample the sample's rate is added to a counter; each time the counter passes the chip's sample
/// rate, the next 16-bit point goes to the DAC as its top 8 bits (<c>&gt;&gt; 8</c>). Between steps, and after the sample
/// ends or is stopped, the DAC keeps its last byte. <c>17 01</c> / <c>17 00</c> turn the DAC on and off; a note turns
/// it on, and a note-off or the sample's end turns it off.</para>
/// </summary>
internal sealed class DmfDac : IDacSource
{
    private const byte DacOn = 0x80;
    private const byte DacOff = 0x00;

    /// <summary>The DAC byte for 0.</summary>
    private const byte DacZero = 0x80;

    /// <summary>The steps a point is counted against: the YM3438's sample rate in whole hertz, as Furnace's <c>chipClock / 144</c>.</summary>
    private readonly int _outputRate;

    private short[]? _sample;
    private int _rate;
    private int _position;
    private int _period;
    private bool _dacMode;
    private byte _data = DacZero;
    private byte _enable = DacOff;

    /// <summary>Creates FM channel 6 in FM mode, silent, for a YM3438 at <paramref name="ymClockHz"/>.</summary>
    public DmfDac(double ymClockHz)
    {
        if (!double.IsFinite(ymClockHz) || ymClockHz < 144)
        {
            throw new ArgumentOutOfRangeException(nameof(ymClockHz), ymClockHz, "The clock must be at least 144 Hz.");
        }

        _outputRate = (int)Ym3438.SampleRateHz(ymClockHz);
    }

    /// <summary>True while a sample is stepping or paused by DAC mode off.</summary>
    public bool IsPlaying => _sample is not null;

    /// <summary>The <c>17xx</c> effect on FM6: turns the DAC on (true) or off; while off, a playing sample pauses.</summary>
    public void SetDacMode(bool on)
    {
        _dacMode = on;
        _enable = on ? DacOn : DacOff;
    }

    /// <summary>
    /// A note on FM6: starts <paramref name="sample"/> (signed 16-bit, as the DMF stores it) from its first point at
    /// <paramref name="rateHz"/> (its rate code's rate) and turns the DAC on. Does nothing in FM mode, where the note
    /// plays FM. True if it started.
    /// </summary>
    public bool Play(short[] sample, int rateHz)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateHz);
        if (!_dacMode)
        {
            return false;
        }

        _sample = sample;
        _rate = rateHz;
        _position = 0;
        _period = 0;
        _enable = DacOn;
        return true;
    }

    /// <summary>A note-off on FM6: the sample stops and the DAC turns off, keeping its last byte.</summary>
    public void Stop()
    {
        _sample = null;
        _enable = DacOff;
    }

    /// <inheritdoc/>
    public void Generate(Span<byte> data, Span<byte> enable)
    {
        if (data.Length != enable.Length)
        {
            throw new ArgumentException("Both registers need the same length.", nameof(enable));
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (_dacMode && _sample is { } sample)
            {
                Step(sample);
            }

            data[i] = _data;
            enable[i] = _enable;
        }
    }

    /// <summary>One YM3438 sample's worth of the sample's rate: zero, one or more points go to the DAC.</summary>
    private void Step(short[] sample)
    {
        _period += _rate;
        while (_period >= _outputRate)
        {
            if (_position >= sample.Length)
            {
                // An empty sample: Furnace stops it without touching $2B.
                _sample = null;
                _period = 0;
                return;
            }

            _data = (byte)((sample[_position] >> 8) + DacZero);
            _position++;
            _period -= _outputRate;
            if (_position >= sample.Length)
            {
                _sample = null;
                _period = 0;
                _enable = DacOff;
                return;
            }
        }
    }
}
