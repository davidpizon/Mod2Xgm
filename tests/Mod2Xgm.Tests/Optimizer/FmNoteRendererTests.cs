using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmNoteRenderer"/>: one held FM note at the analysis rate.</summary>
public sealed class FmNoteRendererTests
{
    /// <summary>A sine at <paramref name="hz"/>, at the chip's rate, of amplitude <paramref name="amplitude"/>.</summary>
    private static float[] ChipSine(double hz, double amplitude, double seconds = 0.5) =>
        [.. Enumerable.Range(0, (int)(seconds * FmNoteRenderer.ChipRateHz)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / FmNoteRenderer.ChipRateHz)))];

    /// <summary>The peak of <paramref name="data"/> from <paramref name="from"/> on, leaving out as much at the end, where the filter runs off the signal.</summary>
    private static double Peak(float[] data, int from) => data.Skip(from).Take(data.Length - (2 * from)).Max(Math.Abs);

    /// <summary>The peak of the last tenth of a second.</summary>
    private static double Tail(float[] data) => data.Skip(data.Length - 4410).Max(Math.Abs);

    /// <summary>Upward zero crossings per second after the first tenth of a second: the pitch of a clean tone.</summary>
    private static double Hz(float[] data)
    {
        int from = SampleAnalyzer.AnalysisRateHz / 10;
        int crossings = 0;
        for (int i = from + 1; i < data.Length; i++)
        {
            crossings += data[i - 1] < 0 && data[i] >= 0 ? 1 : 0;
        }

        return crossings * (double)SampleAnalyzer.AnalysisRateHz / (data.Length - from);
    }

    /// <summary>A TL-0 sine carrier renders at ±1 (a full-scale sample's level), at the asked pitch, for the asked time at 44.1 kHz.</summary>
    [Fact]
    public void ASineCarrierPlaysAtFullScaleAndPitch()
    {
        float[] note = FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 440, 0.5);

        Assert.InRange(note.Length, (SampleAnalyzer.AnalysisRateHz / 2) - 1, (SampleAnalyzer.AnalysisRateHz / 2) + 1);
        Assert.Equal(1, Peak(note, 2000), 0.03);
        Assert.Equal(440, Hz(note), 3);
    }

    /// <summary>The resampler keeps a 1 kHz tone's level and removes a 25 kHz tone that would fold back to 19 kHz.</summary>
    [Fact]
    public void TheResamplerKeepsTheBandAndRemovesWhatWouldAlias()
    {
        float[] kept = FmNoteRenderer.ToAnalysisRate(ChipSine(1000, 0.5));
        float[] removed = FmNoteRenderer.ToAnalysisRate(ChipSine(25_000, 0.5));

        Assert.Equal(0.5, Peak(kept, 200), 0.005);
        Assert.True(Peak(removed, 200) < 0.5 * 0.003, $"peak {Peak(removed, 200)}");
    }

    /// <summary>Vibrato needs both the LFO and the patch's depth: a deep FMS with the LFO on changes the note; with it off it changes nothing.</summary>
    [Fact]
    public void VibratoNeedsTheLfoAndADepth()
    {
        FmPatch deep = FmPatchLibrary.Sine with { Fms = 7 };
        float[] plain = FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 440, 0.5);

        Assert.Equal(plain, FmNoteRenderer.Render(deep, null, 440, 0.5));
        Assert.Equal(plain, FmNoteRenderer.Render(FmPatchLibrary.Sine, 7, 440, 0.5));
        Assert.NotEqual(plain, FmNoteRenderer.Render(deep, 7, 440, 0.5));
    }

    /// <summary>SSG-EG reaches the chip: a repeating shape on a decaying carrier keeps the note from fading.</summary>
    [Fact]
    public void SsgEgReachesTheChip()
    {
        FmPatch fading = FmPatchLibrary.Sine with { Operators = [FmPatchLibrary.Sine.Operators[0] with { Dr = 20, Sl = 15 }, .. FmPatchLibrary.Sine.Operators.Skip(1)] };
        FmPatch repeating = fading with { Operators = [fading.Operators[0] with { SsgEg = 8 }, .. fading.Operators.Skip(1)] };

        float[] once = FmNoteRenderer.Render(fading, null, 440, 1);
        float[] again = FmNoteRenderer.Render(repeating, null, 440, 1);

        Assert.True(Tail(once) < 0.05, $"fading peak {Tail(once)}");
        Assert.True(Tail(again) > 0.2, $"repeating peak {Tail(again)}");
    }

    /// <summary>Bad arguments are refused.</summary>
    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => FmNoteRenderer.Render(null!, null, 440, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 440, 0));
        Assert.Throws<ArgumentNullException>(() => FmNoteRenderer.ToAnalysisRate(null!));
    }
}
