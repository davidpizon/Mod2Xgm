using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Tests.Mod;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="Ch3Fitter"/>: CH3 special-mode candidates for locked pairs (plan T8.5b).</summary>
public sealed class Ch3FitterTests
{
    /// <summary>Small budgets, so the tests run in seconds: the fitting itself is tested with <see cref="FmFitter"/>.</summary>
    private static readonly FmFitOptions Quick = new(MaxRenders: 12, Genetic: false, Lfo: false);

    /// <summary>A looped saw on channel 1 and a looped square an octave up on channel 2, together every 8 rows (eight times, in one region each); the square also plays once alone.</summary>
    private static readonly Lazy<SongAnalysis> SawAndSquare = new(() =>
    {
        ModBuilder mod = new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithSample(2, "square", 64, data: i => (sbyte)(i < 32 ? 80 : -80), loopStart: 0, loopLength: 64);
        for (int row = 0; row < 64; row += 8)
        {
            mod.WithCell(0, row: row, channel: 0, sample: 1, period: 428).WithCell(0, row: row, channel: 1, sample: 2, period: 214);
        }

        mod.WithCell(0, row: 60, channel: 1, sample: 2, period: 214);

        return PcmSongs.Analyze(mod.Build());
    });

    /// <summary>The song's fit, made once for the tests that look at it.</summary>
    private static readonly Lazy<Ch3SongFit> Fitted = new(() => Ch3Fitter.Fit(SawAndSquare.Value, Quick));

    /// <summary>A four-operator patch with every operator told apart by its multiple.</summary>
    private static FmPatch Numbered(int algorithm) =>
        FmPatchLibrary.Lead with { Algorithm = algorithm, Feedback = 5, Operators = [.. FmPatchLibrary.Lead.Operators.Select((o, i) => o with { Mult = i + 1, Am = true })] };

    /// <summary>CH3 special mode is offered for DMF and XGM2, never for XGM v1 (until plan T11.1's probe ROM).</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Dmf), true)]
    [InlineData(nameof(PcmTarget.Xgm2), true)]
    [InlineData(nameof(PcmTarget.Xgm), false)]
    public void Allows_WithTarget_ReturnsWhetherOffered(string target, bool expected)
    {
        // Act
        bool allowed = Ch3Fitter.Allows(Enum.Parse<PcmTarget>(target));

        // Assert
        Assert.Equal(expected, allowed);
    }

    /// <summary>The targets are the ones that allow CH3 special mode.</summary>
    [Fact]
    public void Targets_ForEveryFit_AreDmfAndXgm2()
    {
        // Act
        IEnumerable<PcmTarget> targets = Ch3Fitter.Targets;

        // Assert
        Assert.Equal([PcmTarget.Dmf, PcmTarget.Xgm2], targets);
    }

    /// <summary>
    /// A locked pair gets one candidate: algorithm 4, one voice per half, the closer of every combination tried. It is
    /// scored where both play; the note one plays alone is left to its own half.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithLockedPair_MakesCandidate()
    {
        // Act
        Ch3SongFit fit = Fitted.Value;

        // Assert
        LockedPair pair = Assert.Single(fit.Pairs);
        Assert.Equal((0, 1), (pair.LoneA, pair.LoneB));
        Ch3Candidate c = Assert.Single(fit.Candidates);
        Assert.Equal(8, c.Occurrences);
        Assert.Equal(4, c.Patch.Algorithm);
        Assert.Equal(2, c.HzB / c.HzA, 1);
        Assert.InRange(c.WindowSeconds, 0.1, 1);
        Assert.Equal(c.AOnOps12, c.VoiceA.OnOps12);
        Assert.NotEqual(c.VoiceA.OnOps12, c.VoiceB.OnOps12);
        Assert.True(c.Score.MelDb <= c.Algorithm4Score.MelDb);
        Assert.True(c.Score.MelDb <= c.CutDownScore.MelDb);
        Assert.InRange(c.Score.MelDb, 0, 20);
        Assert.Null(c.Score.HarmonicDb);
        Assert.False(c.Patch.UsesLfo);
        Assert.True(fit.Renders > 0);
    }

    /// <summary>The voices of the candidate keep only their own half sounding.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithPair_SoundsEachVoiceOnItsHalf()
    {
        // Act
        Ch3Candidate c = Assert.Single(Fitted.Value.Candidates);

        // Assert
        Assert.All((Ch3Voice[])[c.VoiceA, c.VoiceB], v =>
        {
            int[] silent = v.OnOps12 ? [2, 3] : [0, 1];
            Assert.All(silent, op => Assert.Equal(127, v.Patch.Operators[op].Tl));
            Assert.Equal(4, v.Patch.Algorithm);
        });
    }

    /// <summary>Same song, same options, same candidate; given the T8.2 fits, the fitter cuts them down instead of fitting again.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_Twice_RepeatsAndReusesFits()
    {
        // Arrange
        SongAnalysis song = SawAndSquare.Value;
        IReadOnlyList<FmSampleFit> fmFits = [.. song.Samples.Select(s => FmFitter.Fit(s, Quick))];
        Ch3SongFit again = Ch3Fitter.Fit(song, Quick);

        // Act
        Ch3SongFit reused = Ch3Fitter.Fit(song, Quick, fmFits);

        // Assert
        Assert.Equal(FmEvaluator.Key(Fitted.Value.Candidates[0].Patch, null), FmEvaluator.Key(again.Candidates[0].Patch, null));
        Assert.Equal(Fitted.Value.Candidates[0].Score, again.Candidates[0].Score);
        Assert.True(reused.Renders < again.Renders, $"{reused.Renders} vs {again.Renders}");
    }

    /// <summary>A sample that must stay off FM leaves no pair and no candidate.</summary>
    [Fact]
    public void Fit_WithKeptOutSamples_MakesNoCandidates()
    {
        // Act
        Ch3SongFit fit = Ch3Fitter.Fit(SawAndSquare.Value, Quick, keepOut: new HashSet<int> { 0 });

        // Assert
        Assert.Empty(fit.Pairs);
        Assert.Empty(fit.Candidates);
    }

    /// <summary>
    /// Cutting down keeps each carrier with each operator feeding it, or alone, on the asked half; OP1's feedback only stays
    /// on OP1. Multiples left sharing a factor are divided down to play at the note (a carrier alone becomes 1).
    /// </summary>
    [Fact]
    public void CutDowns_WithPatch_KeepsCarrierAndModulators()
    {
        // Arrange
        FmPatch[] on12 = [.. Ch3Fitter.CutDowns(Numbered(2), true)];

        // Act
        FmPatch[] on34 = [.. Ch3Fitter.CutDowns(Numbered(4), false)];

        // Assert
        // Algorithm 2: OP4 is fed by OP1 and OP3.
        Assert.Equal([(1, 4, 5), (3, 4, 0), (1, 1, 0)], on12.Select(p => (p.Operators[0].Mult, p.Operators[1].Mult, p.Feedback)));
        Assert.Equal(127, on12[2].Operators[0].Tl);
        Assert.All(on12, p => Assert.Equal((4, 127, 127), (p.Algorithm, p.Operators[2].Tl, p.Operators[3].Tl)));
        Assert.All(on12, p => Assert.All(p.Operators, o => Assert.False(o.Am)));
        // Algorithm 4: OP1→OP2 and OP3→OP4, each also alone; on OP3→OP4 there is no feedback.
        Assert.Equal([(1, 2), (1, 1), (3, 4), (1, 1)], on34.Select(p => (p.Operators[2].Mult, p.Operators[3].Mult)));
        Assert.All(on34, p => Assert.Equal(0, p.Feedback));
        Assert.Equal(4, Ch3Fitter.CutDowns(Numbered(7), true).Count());
        Assert.Equal(6, Ch3Fitter.CutDowns(Numbered(5), true).Count());
    }

    /// <summary>A half keeps its operators and silences the others; two voices combine into one channel's patch.</summary>
    [Fact]
    public void Half_WithPatch_SilencesRestAndCombines()
    {
        // Arrange
        FmPatch lead = Numbered(0);
        Ch3Voice v12 = new(Ch3VoiceSource.Algorithm4, true, Ch3Fitter.Half(lead, true), new NoteScore(1, 1, null));

        // Act
        Ch3Voice v34 = new(Ch3VoiceSource.CutDown, false, Ch3Fitter.Half(lead, false), new NoteScore(2, 2, null));

        // Assert
        FmPatch both = Ch3Fitter.Combine(v12, v34, "both");
        Assert.Equal((4, 5), (v12.Patch.Algorithm, v12.Patch.Feedback));
        Assert.Equal(0, v34.Patch.Feedback);
        Assert.Equal([1, 2, 1, 1], v12.Patch.Operators.Select(o => o.Mult));
        Assert.Equal([127, 127], v12.Patch.Operators.Skip(2).Select(o => o.Tl));
        Assert.Equal([1, 2, 3, 4], both.Operators.Select(o => o.Mult));
        Assert.Equal(("both", 4, 5), (both.Name, both.Algorithm, both.Feedback));
    }

    /// <summary>A rate inside a region gives that region; outside every region, the nearest by pitch.</summary>
    [Fact]
    public void RegionOf_WithRate_FindsRegion()
    {
        // Arrange
        SampleAnalysis saw = SawAndSquare.Value.Samples[0];

        // Act
        double rate = saw.Regions[0].RateHz;

        // Assert
        Assert.Equal(0, Ch3Fitter.RegionOf(saw, rate));
        Assert.Equal(0, Ch3Fitter.RegionOf(saw, rate * 8));
    }

    /// <summary>The report logs a line per candidate and a summary at Debug level.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Log_WithCandidates_LogsEach()
    {
        // Arrange
        CollectingSink sink = new();

        // Act
        Ch3FitReport.Log(sink.CreateLogger(), Fitted.Value);

        // Assert
        string[] lines = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("CH3 1.1 (ch 1) + 2.1 (ch 2): 8 onsets, ", lines[0]);
        Assert.Contains("MULT", lines[0]);
        Assert.StartsWith("1 locked pairs, 1 CH3 candidates", lines[1]);
        Assert.Equal("algorithm4", Ch3FitReport.SourceName(Ch3VoiceSource.Algorithm4));
        Assert.Equal("cut-down", Ch3FitReport.SourceName(Ch3VoiceSource.CutDown));
    }
}
