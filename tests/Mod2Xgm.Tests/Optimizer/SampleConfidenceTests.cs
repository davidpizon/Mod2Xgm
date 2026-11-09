using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="SampleConfidence"/> and <see cref="FmConfidenceModel"/>: one FM confidence per sample from its regions (docs/adr/0096-fm-confidence.md).</summary>
public sealed class SampleConfidenceTests
{
    /// <summary>Checks that a fit at the corpus median is an even chance.</summary>
    [Fact]
    public void Probability_AtMidpoint_IsHalf()
    {
        // Arrange
        FmRegionFeatures features = Features(FmConfidenceModel.ProvisionalMidpointDb);

        // Act
        double sut = FmConfidenceModel.Probability(features);

        // Assert
        Assert.Equal(0.5, sut, 6);
    }

    /// <summary>Checks that a closer fit is a better chance than a further one.</summary>
    [Fact]
    public void Probability_WithCloserFit_IsHigher()
    {
        // Arrange
        FmRegionFeatures close = Features(4);
        FmRegionFeatures far = Features(10);

        // Act
        double sut = FmConfidenceModel.Probability(close);

        // Assert
        Assert.True(sut > 0.8);
        Assert.True(FmConfidenceModel.Probability(far) < 0.2);
    }

    /// <summary>Checks that the region heard most counts most: a good low region played three times as much as a bad high one.</summary>
    [Fact]
    public void Of_WithWeightedRegions_FavoursHeardRegion()
    {
        // Arrange
        SampleSounds sounds = Sounds(Region(4), Region(10));
        double low = FmConfidenceModel.Probability(Features(4));
        double high = FmConfidenceModel.Probability(Features(10));

        // Act
        SampleConfidence sut = SampleConfidence.Of(sounds, [3, 1], 0.2, 0, MappingOptions.Default);

        // Assert
        Assert.Equal(((3 * low) + high) / 4, sut.Probability!.Value, 6);
        Assert.Equal(ConfidenceReason.Scored, sut.Reason);
        Assert.Equal(0.2, sut.Exposure);
    }

    /// <summary>Checks that a sample the song never plays weighs its regions alike.</summary>
    [Fact]
    public void Of_WithUnplayedSample_AveragesRegions()
    {
        // Arrange
        SampleSounds sounds = Sounds(Region(4), Region(10));

        // Act
        SampleConfidence sut = SampleConfidence.Of(sounds, [0, 0], 0, 0, MappingOptions.Default);

        // Assert
        Assert.Equal((FmConfidenceModel.Probability(Features(4)) + FmConfidenceModel.Probability(Features(10))) / 2, sut.Probability!.Value, 6);
    }

    /// <summary>Checks that a region without a patch has no chance and takes no part in the sample's.</summary>
    [Fact]
    public void Of_WithUnfittedRegion_SkipsIt()
    {
        // Arrange
        SampleSounds sounds = Sounds(Region(4), new RegionSounds(0, 1e6, null, null, 15, 25));

        // Act
        SampleConfidence sut = SampleConfidence.Of(sounds, [1, 5], 0, 0, MappingOptions.Default);

        // Assert
        Assert.Null(sut.RegionProbability[1]);
        Assert.Equal(FmConfidenceModel.Probability(Features(4)), sut.Probability!.Value, 6);
    }

    /// <summary>Checks why a sample's confidence doesn't decide how it plays.</summary>
    [Theory]
    [InlineData("fm", "RequiredFm")]
    [InlineData("dac", "RequiredDac")]
    [InlineData("psg", "RequiredPsg")]
    [InlineData("unsteady", "Unsteady")]
    [InlineData("nofit", "NoFm")]
    [InlineData("nodac", "NoRecording")]
    [InlineData("norecording", "NoRecording")]
    public void Of_WithCase_GivesReason(string situation, string expected)
    {
        // Arrange
        SampleSounds sounds = situation switch
        {
            "unsteady" => Sounds(Region(4)) with { SteadyPitch = false },
            "nofit" => Sounds(new RegionSounds(0, 1e6, null, null, 15, 25)),
            "norecording" => Sounds(Region(4)) with { RecordingDb = new Dictionary<double, double>() },
            _ => Sounds(Region(4)),
        };
        MappingOptions options = situation switch
        {
            "fm" => MappingOptions.Default with { RequireFm = new HashSet<int> { 0 } },
            "dac" => MappingOptions.Default with { RequireDac = new HashSet<int> { 0 } },
            "psg" => MappingOptions.Default with { RequirePsg = new HashSet<int> { 0 } },
            "nodac" => MappingOptions.Default with { UseDac = false },
            _ => MappingOptions.Default,
        };

        // Act
        SampleConfidence sut = SampleConfidence.Of(sounds, [1], 0, 0, options);

        // Assert
        Assert.Equal(Enum.Parse<ConfidenceReason>(expected), sut.Reason);
    }

    private static FmRegionFeatures Features(double melDb) => new(new NoteScore(melDb, 0, null), 0, 0, 0);

    private static RegionSounds Region(double melDb) => new(0, 1e6, FmPatchLibrary.Sine, melDb, 15, 25, Features: Features(melDb));

    private static SampleSounds Sounds(params RegionSounds[] regions) => new(0, 0.2, regions, new Dictionary<double, double> { [8000] = 2 });
}
