using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The DMF DAC model against how Furnace plays a DMF's FM6 samples (docs/adr/0058-pcm-driver-models.md): the expected
/// values come from Furnace's <c>genesis.cpp</c>, not from a capture.
/// </summary>
public sealed class DmfDacTests
{
    /// <summary>The chip's rate in whole hertz on NTSC, which the points are counted against.</summary>
    private const int NtscRate = 53_267;

    private static DmfDac InDacMode()
    {
        DmfDac dac = new(Ym3438.NtscClockHz);
        dac.SetDacMode(true);
        return dac;
    }

    /// <summary>A fresh FM6 is in FM mode: the DAC is off at 0, and a note plays no sample.</summary>
    [Fact]
    public void FmModePlaysNoSample()
    {
        DmfDac dac = new(Ym3438.NtscClockHz);
        Assert.False(dac.Play([0x4000], 8000));
        DacCapture capture = DacCapture.Of(dac, 100);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.False(dac.IsPlaying);
    }

    /// <summary><c>17 01</c> turns the DAC on and <c>17 00</c> off.</summary>
    [Fact]
    public void DacModeSwitchesTheDac()
    {
        DmfDac dac = InDacMode();
        Assert.Equal(0x80, DacCapture.Of(dac, 1).Enable[0]);
        dac.SetDacMode(false);
        Assert.Equal(0, DacCapture.Of(dac, 1).Enable[0]);
    }

    /// <summary>
    /// At 8 kHz the counter passes 53 267 first on the 7th sample (7 × 8000), then every 6 or 7 samples: 8000 points a
    /// second, held in between, so a 2000-point sample plays in a quarter of a second.
    /// </summary>
    [Fact]
    public void PointsStepAtTheSamplesRate()
    {
        DmfDac dac = InDacMode();
        short[] sample = [.. Enumerable.Range(1, 2000).Select(i => (short)(((i % 120) + 1) << 8))];
        Assert.True(dac.Play(sample, 8000));
        DacCapture capture = DacCapture.Of(dac, NtscRate);
        Assert.Equal(0x80, capture.Data[5]);
        Assert.Equal(0x80 + 2, capture.Data[6]);
        List<int> changes = capture.Changes();
        Assert.Equal(6, changes[0]);
        Assert.All(changes.Zip(changes.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap, 6, 7));
        Assert.Equal(2000, changes.Count);
    }

    /// <summary>Each 16-bit point goes to the DAC as its top 8 bits, rounded down: 255 is 0, −1 is −1.</summary>
    [Fact]
    public void PointsAreCutTo8BitsRoundingDown()
    {
        DmfDac dac = InDacMode();
        dac.Play([short.MaxValue, short.MinValue, 255, -1, 0x1234, 0], NtscRate);
        DacCapture capture = DacCapture.Of(dac, 5);
        Assert.Equal([0xff, 0x00, 0x80, 0x7f, 0x80 + 0x12], capture.Data);
    }

    /// <summary>After the last point the DAC turns off (in the same sample) and keeps that last byte.</summary>
    [Fact]
    public void TheSamplesEndTurnsTheDacOff()
    {
        DmfDac dac = InDacMode();
        dac.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture capture = DacCapture.Of(dac, 6);
        Assert.Equal([0x90, 0xa0, 0xb0, 0xb0, 0xb0, 0xb0], capture.Data);
        Assert.Equal([0x80, 0x80, 0, 0, 0, 0], capture.Enable);
        Assert.False(dac.IsPlaying);
    }

    /// <summary>A note-off stops the sample and turns the DAC off; the DAC keeps its byte; the next note turns it on.</summary>
    [Fact]
    public void ANoteOffStopsTheSample()
    {
        DmfDac dac = InDacMode();
        dac.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(dac, 1);
        dac.Stop();
        DacCapture capture = DacCapture.Of(dac, 3);
        Assert.All(capture.Data, b => Assert.Equal(0x90, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));

        dac.Play([0x4000, 0x4000], NtscRate);
        Assert.Equal(0x80, DacCapture.Of(dac, 1).Enable[0]);
    }

    /// <summary>A new note restarts from the first point.</summary>
    [Fact]
    public void ANewNoteRestarts()
    {
        DmfDac dac = InDacMode();
        short[] sample = [0x1000, 0x2000, 0x3000];
        dac.Play(sample, NtscRate);
        DacCapture.Of(dac, 2);
        dac.Play(sample, NtscRate);
        Assert.Equal(0x90, DacCapture.Of(dac, 1).Data[0]);
    }

    /// <summary>Turning DAC mode off pauses the sample where it is; turning it back on carries on from there.</summary>
    [Fact]
    public void DacModeOffPausesTheSample()
    {
        DmfDac dac = InDacMode();
        dac.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(dac, 1);
        dac.SetDacMode(false);
        Assert.All(DacCapture.Of(dac, 4).Data, b => Assert.Equal(0x90, b));
        Assert.True(dac.IsPlaying);
        dac.SetDacMode(true);
        Assert.Equal([0xa0, 0xb0], DacCapture.Of(dac, 2).Data);
    }

    /// <summary>An empty sample stops at once and, as in Furnace, leaves the DAC on.</summary>
    [Fact]
    public void AnEmptySampleStopsAtOnce()
    {
        DmfDac dac = InDacMode();
        Assert.True(dac.Play([], 8000));
        DacCapture capture = DacCapture.Of(dac, 20);
        Assert.False(dac.IsPlaying);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0x80, b));
    }

    /// <summary>Bad clocks, rates and buffers are refused.</summary>
    [Fact]
    public void BadArgumentsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmfDac(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmfDac(double.NaN));
        DmfDac dac = InDacMode();
        Assert.Throws<ArgumentOutOfRangeException>(() => dac.Play([1], 0));
        Assert.Throws<ArgumentNullException>(() => dac.Play(null!, 8000)); // null on purpose, past the annotation
        Assert.Throws<ArgumentException>(() => dac.Generate(new byte[1], new byte[2]));
    }
}
