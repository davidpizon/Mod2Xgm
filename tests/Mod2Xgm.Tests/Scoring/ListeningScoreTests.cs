using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for <see cref="ListeningScore"/> and <see cref="ListeningFeatures"/> (plan T9.4k2).</summary>
public sealed class ListeningScoreTests
{
    private static readonly ScoreReport WithPerceptual = new(4.5, 30, 0.9, 0.8, 0.75, null, 0, 0, 4.5, new PerceptualReport(-12, 0.25, 3.5, 7));

    private static readonly ScoreReport WithoutPerceptual = WithPerceptual with { Perceptual = null };

    /// <summary>The features come from the report in the order of their names, with onset F1 turned into a distance.</summary>
    [Fact]
    public void From_WithPerceptual_ListsEveryFeatureInOrder()
    {
        // Act
        ListeningFeatures? sut = ListeningFeatures.From(WithPerceptual);

        // Assert
        Assert.NotNull(sut);
        Assert.Equal([4.5, 30, 0.25, -12, 0.25, 3.5, 7], sut.Values);
        Assert.Equal(ListeningFeatures.Names.Count, sut.Values.Count);
    }

    /// <summary>A report made without the perceptual features has no listening features.</summary>
    [Fact]
    public void From_WithoutPerceptual_ReturnsNull()
    {
        // Act
        ListeningFeatures? sut = ListeningFeatures.From(WithoutPerceptual);

        // Assert
        Assert.Null(sut);
    }

    /// <summary>The score is the weights' dot product with the features.</summary>
    [Fact]
    public void Of_WithWeights_IsTheDotProduct()
    {
        // Arrange
        double[] weights = [1, 0, 2, 0, 0, 0, -1];

        // Act
        double? sut = ListeningScore.Of(WithPerceptual, weights);

        // Assert
        Assert.NotNull(sut);
        Assert.Equal(4.5 + 0.5 - 7, sut.Value, 9);
    }

    /// <summary>Empty weights mean no fit, so there is no score.</summary>
    [Fact]
    public void Of_WithEmptyWeights_ReturnsNull()
    {
        // Act
        double? sut = ListeningScore.Of(WithPerceptual, []);

        // Assert
        Assert.Null(sut);
    }

    /// <summary>Without perceptual features or without a report there is nothing to score.</summary>
    [Fact]
    public void Of_WithoutFeatures_ReturnsNull()
    {
        // Arrange
        double[] weights = [1, 1, 1, 1, 1, 1, 1];

        // Act
        double? withoutPerceptual = ListeningScore.Of(WithoutPerceptual, weights);
        double? withoutReport = ListeningScore.Of((ScoreReport?)null, weights);

        // Assert
        Assert.Null(withoutPerceptual);
        Assert.Null(withoutReport);
    }

    /// <summary>Weights that aren't one per feature are a bug, not a score.</summary>
    [Fact]
    public void Of_WithWrongWeightCount_Throws()
    {
        // Arrange
        ListeningFeatures features = new(1, 2, 3, 4, 5, 6, 7);

        // Act
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ListeningScore.Of(features, [1, 2]));

        // Assert
        Assert.Equal("weights", ex.ParamName);
    }

    /// <summary>The fitted weights, when there are any, are one per feature, and the default score uses them.</summary>
    [Fact]
    public void Weights_AsWritten_AreEmptyOrOnePerFeature()
    {
        // Act
        int count = ListeningWeights.Weights.Count;
        double? sut = ListeningScore.Of(WithPerceptual);

        // Assert
        Assert.Contains(count, new[] { 0, ListeningFeatures.Names.Count });
        Assert.Equal(ListeningScore.Fitted, sut is not null);
    }
}
