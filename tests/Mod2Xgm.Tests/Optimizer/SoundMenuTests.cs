using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="SoundMenu"/>: how a fitted patch is made ready for the plan.</summary>
public sealed class SoundMenuTests
{
    /// <summary>Checks that a chord is known by its loud upper notes over a root that doesn't sound, as kc's SynthFMaj is.</summary>
    [Fact]
    public void IsChord_WithSilentRoot_IsTrue()
    {
        // Arrange
        float[][] harmonics = [[-60, -60, -13, -20, -14, -24]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.True(sut);
    }

    /// <summary>Checks that a bright note isn't taken for a chord: cancan's Banjo has its loudest harmonic high, but its second is only 10 dB down.</summary>
    [Fact]
    public void IsChord_WithBrightNote_IsFalse()
    {
        // Arrange
        float[][] harmonics = [[-40, -23, -21, -17, -13, -15]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that a note whose fundamental is loudest isn't taken for a chord.</summary>
    [Fact]
    public void IsChord_WithLoudFundamental_IsFalse()
    {
        // Arrange
        float[][] harmonics = [[-13, -40, -40, -40]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that no harmonics to go by say it isn't a chord.</summary>
    [Fact]
    public void IsChord_WithNoHarmonics_IsFalse()
    {
        // Arrange
        float[][] harmonics = [];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that a chord's notes are its loud harmonics that aren't harmonics of a lower note: C-F-A over a silent root is 3, 4 and 5, and 6, 8 and 10 are their own.</summary>
    [Fact]
    public void ChordTones_WithThreeNoteChord_ReturnsItsNotes()
    {
        // Arrange
        float[][] harmonics = [[-60, -60, -13, -15, -14, -18, -50, -20, -50, -22]];

        // Act
        IReadOnlyList<int> sut = SoundMenu.ChordTones(harmonics);

        // Assert
        Assert.Equal([3, 4, 5], sut);
    }

    /// <summary>Checks that a sample that isn't a chord has no chord notes.</summary>
    [Fact]
    public void ChordTones_WithSingleNote_ReturnsNone()
    {
        // Arrange
        float[][] harmonics = [[-13, -40, -40, -40]];

        // Act
        IReadOnlyList<int> sut = SoundMenu.ChordTones(harmonics);

        // Assert
        Assert.Empty(sut);
    }

    /// <summary>Checks that a chord with a loud second harmonic is still found, as EM2's MELLO chords are: 2, 3, 4 and 5, the 4 louder than the 2 it doubles.</summary>
    [Fact]
    public void ChordTones_WithLoudSecondHarmonic_ReturnsItsNotes()
    {
        // Arrange
        float[][] harmonics = [[-26, -14, -21, -12, -20, -20, -53, -20, -55, -28, -58, -29, -65, -63, -33, -33]];

        // Act
        IReadOnlyList<int> sut = SoundMenu.ChordTones(harmonics);

        // Assert
        Assert.Equal([2, 3, 4, 5], sut);
    }

    /// <summary>Checks that one note whose overtones fall away isn't taken for a chord, though its overtones are within reach of its level.</summary>
    [Fact]
    public void ChordTones_WithFallingOvertones_ReturnsNone()
    {
        // Arrange
        float[][] harmonics = [[-10, -14, -18, -22]];

        // Act
        IReadOnlyList<int> sut = SoundMenu.ChordTones(harmonics);

        // Assert
        Assert.Empty(sut);
    }

    /// <summary>Checks that of five chord notes only the four loudest are kept, low to high.</summary>
    [Fact]
    public void ChordTones_WithFiveNotes_KeepsTheLoudestFour()
    {
        // Arrange
        float[][] harmonics = [[-60, -60, -13, -60, -14, -60, -20, -60, -60, -60, -16, -60, -15]];

        // Act
        IReadOnlyList<int> sut = SoundMenu.ChordTones(harmonics);

        // Assert
        Assert.Equal([3, 5, 11, 13], sut);
    }

    /// <summary>Checks that a chord note's target is the chord's harmonics that are the note's own, with the note as the fundamental.</summary>
    [Fact]
    public void ToneTarget_WithThirdHarmonic_TakesItsOwnHarmonics()
    {
        // Arrange
        NoteTarget chord = new(100, 100, new RegionSpectrum(0.01, [[-20f]], [[-60, -60, -13, -15, -14, -18, -50]]));

        // Act
        NoteTarget sut = SoundMenu.ToneTarget(chord, 3);

        // Assert
        Assert.Equal((300.0, (double?)300.0), (sut.NoteHz, sut.FundamentalHz));
        Assert.Equal([-13f, -18f], Assert.Single(sut.Spectrum.HarmonicsDb!)); // ToneTarget always gives harmonics.
    }

    /// <summary>Checks that a chord without a pitch can't give a note's target.</summary>
    [Fact]
    public void ToneTarget_WithoutPitch_Throws()
    {
        // Arrange
        NoteTarget chord = new(100, null, new RegionSpectrum(0.01, [[-20f]], null));

        // Act and Assert
        Assert.Throws<ArgumentException>(() => SoundMenu.ToneTarget(chord, 3));
    }

    /// <summary>Checks that the patch is fitted to the note sharing the fewest harmonics: of C-E-G as 4, 5 and 6, the 5 shares only 20.</summary>
    [Fact]
    public void ReferenceTone_WithMajorChord_TakesTheOneSharingLeast()
    {
        // Act
        int sut = SoundMenu.ReferenceTone([4, 5, 6], 24);

        // Assert
        Assert.Equal(5, sut);
    }

    /// <summary>Checks that a chord's render is its notes' voices added, each at its level.</summary>
    [Fact]
    public void RenderChord_WithTwoNotes_AddsTheirVoices()
    {
        // Arrange
        float[] low = FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 200, SampleAnalyzer.RegionSeconds);
        float[] high = FmNoteRenderer.Render(FmPatchLibrary.Sine, null, 300, SampleAnalyzer.RegionSeconds);

        // Act
        float[] sut = SoundMenu.RenderChord(FmPatchLibrary.Sine, 100, [new ChordTone(2, 1), new ChordTone(3, 0.5)]);

        // Assert
        Assert.Equal(low.Length, sut.Length);
        Assert.All(Enumerable.Range(0, sut.Length), i => Assert.Equal(low[i] + (0.5f * high[i]), sut[i], 1e-5f));
    }

    /// <summary>Checks that a chord note sits its harmonic's interval over the root: the fourth harmonic two octaves up.</summary>
    [Fact]
    public void Cents_WithFourthHarmonic_IsTwoOctaves()
    {
        // Act
        double sut = new ChordTone(4, 1).Cents;

        // Assert
        Assert.Equal(2400, sut);
    }

    /// <summary>Checks that a chord's second voice is a copy of its patch, spread by 22 cents, with the first turned down at least the 3 dB the pair sits under the sample.</summary>
    [Fact]
    public void SecondVoice_WithChord_ReturnsDetunedCopy()
    {
        // Arrange
        FmPatch first = FmPatchLibrary.Sine with { HeardRms = 0.5 };
        PitchRegion region = RegionOf([-60, -60, -13, -20, -14, -24]);

        // Act
        (FmPatch? second, double firstLevel, double spread) = SoundMenu.SecondVoice(region, first, true, "second");

        // Assert
        Assert.Equal("second", second?.Name);
        Assert.Equal(first.Operators, second?.Operators);
        Assert.Equal(22, spread);
        Assert.InRange(firstLevel, 0.01, 0.71);
    }

    /// <summary>Checks that a sample that isn't a chord gets no second voice.</summary>
    [Fact]
    public void SecondVoice_WithSingleNote_ReturnsNone()
    {
        // Arrange
        FmPatch first = FmPatchLibrary.Sine with { HeardRms = 0.5 };
        PitchRegion region = RegionOf([-13, -40, -40, -40]);

        // Act
        (FmPatch? second, double firstLevel, double spread) = SoundMenu.SecondVoice(region, first, true, "second");

        // Assert
        Assert.Null(second);
        Assert.Equal(1, firstLevel);
        Assert.Equal(0, spread);
    }

    /// <summary>A pitch region whose one frame has these harmonic levels.</summary>
    private static PitchRegion RegionOf(float[] harmonics) =>
        new(8000, 8000, 8000, 1, 100, new RegionSpectrum(0.01, [[-20f]], [harmonics]));

    /// <summary>Checks that a quiet fit is lifted until its loudest carrier is at TL 0, with the other carrier as far below as it was.</summary>
    [Fact]
    public void AtFullLevel_WithQuietCarriers_LiftsThemTogether()
    {
        // Arrange: algorithm 4 has carriers OP2 and OP4.
        FmPatch patch = Patch(algorithm: 4, tl1: 30, tl2: 20, tl3: 40, tl4: 25);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Equal([30, 0, 40, 5], sut.Operators.Select(o => o.Tl));
    }

    /// <summary>Checks that a patch already at full level comes back as it was.</summary>
    [Fact]
    public void AtFullLevel_WithCarrierAtTlZero_ReturnsSamePatch()
    {
        // Arrange
        FmPatch patch = Patch(algorithm: 0, tl1: 30, tl2: 20, tl3: 40, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Same(patch, sut);
    }

    /// <summary>Checks that a muted carrier stays muted and doesn't count as the loudest.</summary>
    [Fact]
    public void AtFullLevel_WithMutedCarrier_LeavesItMuted()
    {
        // Arrange: algorithm 4 has carriers OP2 and OP4.
        FmPatch patch = Patch(algorithm: 4, tl1: 30, tl2: 127, tl3: 40, tl4: 25);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Equal([30, 127, 40, 0], sut.Operators.Select(o => o.Tl));
    }

    /// <summary>Checks that a held sine has a steady pitch.</summary>
    [Fact]
    public void HasSteadyPitch_WithHeldSine_ReturnsTrue()
    {
        // Arrange
        float[] data = [.. Enumerable.Range(0, 64).Select(i => (float)Math.Sin(2 * Math.PI * i / 64))];
        SampleAnalysis analysis = SampleAnalyzer.Analyze(0, new Sample("sine", data, new SampleLoop(0, 64)), [8287]);

        // Act
        bool steady = SoundMenu.HasSteadyPitch(analysis);

        // Assert
        Assert.True(steady);
    }

    /// <summary>Checks that hiss has no steady pitch for a tuned voice to hold.</summary>
    [Fact]
    public void HasSteadyPitch_WithHiss_ReturnsFalse()
    {
        // Arrange
        Random random = new(1);
        float[] data = [.. Enumerable.Range(0, 8000).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        SampleAnalysis analysis = SampleAnalyzer.Analyze(0, new Sample("hiss", data, null), [8287]);

        // Act
        bool steady = SoundMenu.HasSteadyPitch(analysis);

        // Assert
        Assert.False(steady);
    }

    /// <summary>Checks that a held TL-0 sine carrier measures as a full-scale sine's RMS.</summary>
    [Fact]
    public void Measured_WithHeldSineCarrier_ReturnsSineRms()
    {
        // Arrange: algorithm 0's only carrier is OP4; the others are muted.
        FmPatch patch = Patch(algorithm: 0, tl1: 127, tl2: 127, tl3: 127, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.Measured(patch, 440, loops: true);

        // Assert
        Assert.NotNull(sut.HeardRms);
        Assert.Equal(Math.Sqrt(0.5), sut.HeardRms.Value, 1);
    }

    /// <summary>Checks that a patch with no pitch to render at keeps the level worked out from its registers.</summary>
    [Fact]
    public void Measured_WithNoPitch_ReturnsSamePatch()
    {
        // Arrange
        FmPatch patch = Patch(algorithm: 0, tl1: 127, tl2: 127, tl3: 127, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.Measured(patch, null, loops: true);

        // Assert
        Assert.Same(patch, sut);
    }

    /// <summary>Makes a patch with the given operator levels.</summary>
    private static FmPatch Patch(int algorithm, int tl1, int tl2, int tl3, int tl4) => new(
        "test",
        algorithm,
        0,
        [.. new[] { tl1, tl2, tl3, tl4 }.Select(tl => new FmOperator(1, 0, tl, 0, 31, false, 0, 0, 0, 15))]);
}
