using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="LoopSpectrum"/>: the power of a repeating loop near a frequency, and the bottom note of a chord or
/// chorused tone (docs/adr/0125-chord-samples.md).
/// </summary>
public sealed class LoopSpectrumTests
{
    /// <summary>A sample without a loop, or with a silent loop, has no spectrum.</summary>
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 0.0)]
    public void Of_WithoutLoopOrSound_ReturnsNull(bool loops, double amplitude)
    {
        // Arrange
        Sample sample = new("tone", Partials(64, (1, amplitude)), loops ? new SampleLoop(0, 64) : null);

        // Act
        LoopSpectrum? sut = LoopSpectrum.Of(sample);

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A sine's power lies all in the band around its frequency, none an octave up, and nothing is above Nyquist.</summary>
    [Fact]
    public void Share_WithSine_HoldsItsPowerAtItsFrequency()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("sine", Partials(64, (1, 1.0)), new SampleLoop(0, 64)));

        // Act
        double at = sut?.Share(1 / 64.0, SamplePitch.ChordCents) ?? double.NaN;
        double octave = sut?.Share(1 / 32.0, SamplePitch.ChordCents) ?? double.NaN;
        double beyond = sut?.Share(0.6, SamplePitch.ChordCents) ?? double.NaN;

        // Assert
        Assert.InRange(at, 0.999, 1.001);
        Assert.InRange(octave, 0, 1e-6);
        Assert.Equal(0, beyond);
    }

    /// <summary>The bottom note skips a partial more than 20 dB below the loudest (here 34 dB): it isn't heard as a note.</summary>
    [Fact]
    public void BottomNote_WithQuietLowPartial_SkipsIt()
    {
        // Arrange
        float[] frames = Partials(6400, (50, 0.02), (100, 1.0));
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("tone", frames, new SampleLoop(0, 6400)));

        // Act
        double? bottom = sut?.BottomNote(SamplePitch.ChordCents, SamplePitch.StrongDb, 1 / 1024.0);

        // Assert
        Assert.Equal(100 / 6400.0, bottom ?? double.NaN, 1e-5);
    }

    /// <summary>A chorused partial (two copies 17 cents apart) is one note, at its middle.</summary>
    [Fact]
    public void BottomNote_WithChorusedPartial_ReturnsItsMiddle()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("tone", Partials(16384, (100, 0.5), (101, 0.5)), new SampleLoop(0, 16384)));

        // Act
        double? bottom = sut?.BottomNote(SamplePitch.ChordCents, SamplePitch.StrongDb, 1 / 1024.0);

        // Assert
        Assert.InRange(bottom ?? double.NaN, 100 / 16384.0, 101 / 16384.0);
    }

    /// <summary>
    /// A C major triad (C4, E4, G4 at C-2, roots 517, 651 and 775 cycles over the loop, each with its second and third
    /// harmonic) is three notes in three series holding all of its power: a chord (docs/adr/0135-loop-chords.md).
    /// </summary>
    [Fact]
    public void Chord_WithTriad_FindsItsThreeNotes()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("triad", Triad(517, 651, 775), new SampleLoop(0, 16384)));

        // Act
        LoopChord? chord = sut?.Chord(SamplePitch.ChordCents, 1 / 1024.0);

        // Assert
        Assert.NotNull(chord);
        Assert.Equal([517, 651, 775], chord.Notes.Select(n => Math.Round(n.CyclesPerFrame * 16384)));
        Assert.Equal((3, 3, true), (chord.Series, chord.PitchClasses, chord.IsChord));
        Assert.InRange(chord.Share, 0.95, 1.0);
    }

    /// <summary>A fifth (C4 and G4) is two notes, not a triad: no chord yet (David, 2026-11-21: triads first).</summary>
    [Fact]
    public void Chord_WithFifth_IsNoChord()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("fifth", Triad(517, 775), new SampleLoop(0, 16384)));

        // Act
        LoopChord? chord = sut?.Chord(SamplePitch.ChordCents, 1 / 1024.0);

        // Assert
        Assert.Equal((2, false), (chord?.Notes.Count, chord?.IsChord));
    }

    /// <summary>A chorused tone (each harmonic doubled a few cents apart) is one note, not a chord.</summary>
    [Fact]
    public void Chord_WithChorusedTone_FindsOneNote()
    {
        // Arrange
        float[] frames = Partials(16384, (517, 0.3), (518, 0.3), (1034, 0.15), (1036, 0.15), (1551, 0.1), (1554, 0.1));
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("chorus", frames, new SampleLoop(0, 16384)));

        // Act
        LoopChord? chord = sut?.Chord(SamplePitch.ChordCents, 1 / 1024.0);

        // Assert
        Assert.Equal((1, false), (chord?.Notes.Count, chord?.IsChord));
    }

    /// <summary>White noise makes many series and no notes that hold most of it: no chord.</summary>
    [Fact]
    public void Chord_WithNoise_IsNoChord()
    {
        // Arrange
        float[] frames = [.. new Random(3).GetItems([-1f, 1f], 16384)];
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("noise", frames, new SampleLoop(0, 16384)));

        // Act
        LoopChord? chord = sut?.Chord(SamplePitch.ChordCents, 1 / 1024.0);

        // Assert
        Assert.False(chord?.IsChord ?? true);
    }

    /// <summary>Three notes that are a fifth and its octave (C#, G#, C#) fall on two pitch classes: not a triad.</summary>
    [Fact]
    public void IsChord_WithFifthAndOctave_IsFalse()
    {
        // Arrange
        LoopChord sut = new([new LoopNote(0.01, 0.3), new LoopNote(0.015, 0.3), new LoopNote(0.04, 0.3)], 0.9, 3);

        // Act
        bool isChord = sut.IsChord;

        // Assert
        Assert.Equal((2, false), (sut.PitchClasses, isChord));
    }

    /// <summary>16384 frames of notes at <paramref name="roots"/> cycles over them, each with its second and third harmonic.</summary>
    private static float[] Triad(params int[] roots) =>
        Partials(16384, [.. roots.SelectMany(r => new[] { (r, 0.3), (2 * r, 0.15), (3 * r, 0.1) })]);

    /// <summary><paramref name="frames"/> frames of sines of whole numbers of cycles over them, at the given amplitudes.</summary>
    private static float[] Partials(int frames, params (int Cycles, double Amplitude)[] partials) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)partials.Sum(p => p.Amplitude * Math.Sin(2 * Math.PI * p.Cycles * i / frames)))];
}
