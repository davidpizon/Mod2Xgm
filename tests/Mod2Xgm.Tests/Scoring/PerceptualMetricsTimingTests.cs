using System.Diagnostics;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>
/// The perceptual features' time budget (plan T9.4k1, docs/adr/0100-perceptual-features.md). Runs alone, after the
/// parallel tests, so other tests don't share the machine while it measures; the coverage job skips it
/// (<c>Category=Performance</c>), since instrumentation slows code about 8 times.
/// </summary>
[Collection(TimingCollection.Name)]
[Trait("Category", "Performance")]
public sealed class PerceptualMetricsTimingTests
{
    /// <summary>The rate of the speed test: the analysis rate of a pitch region.</summary>
    private const int RegionRate = 44_100;

    /// <summary>All four features of one pitch region (1 s at 44.1 kHz) take at most 20 ms (plan T9.4k1's budget).</summary>
    [Fact]
    public void Compare_OnePitchRegion_TakesAtMost20Ms()
    {
        // Arrange
        AudioSignal reference = PerceptualMetricsTests.Mix(TestSignals.Sine(220, 1, rate: RegionRate), TestSignals.Noise(1, 0.05, rate: RegionRate));
        AudioSignal candidate = TestSignals.Sine(220, 1, rate: RegionRate);
        PerceptualMetrics.Compare(reference, candidate);

        // Act
        double fastest = FastestMilliseconds(() => PerceptualMetrics.Compare(reference, candidate), runs: 9);

        // Assert
        Assert.True(fastest <= 20, $"fastest {fastest:0.0} ms");
    }

    /// <summary>
    /// The fastest of <paramref name="runs"/> runs of <paramref name="action"/>, in milliseconds: the code's own cost,
    /// since a shared CI runner only ever adds time (a 23 ms median on macOS against 8.7 ms on a quiet machine).
    /// </summary>
    private static double FastestMilliseconds(Action action, int runs)
    {
        double[] times = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            action();
            times[i] = watch.Elapsed.TotalMilliseconds;
        }

        return times.Min();
    }
}

/// <summary>Tests that measure time: they run one at a time, with nothing else running.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Timing";
}
