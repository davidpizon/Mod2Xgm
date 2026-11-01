using Mod2Xgm.Analysis;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Tests.Mod;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PcmFitter"/>: PCM copies of made-up samples, with their bytes, savings, scores and baked notes.</summary>
public sealed class PcmFitterTests
{
    /// <summary>A looped saw played at C-2 and C-3, one note each: a looped melodic sample.</summary>
    private static readonly Lazy<SongAnalysis> TwoPitches = new(() => PcmSongs.Analyze(new ModBuilder()
        .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
        .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
        .WithCell(0, row: 8, channel: 0, sample: 1, period: 214)
        .WithCell(0, row: 16, channel: 0, sample: 1, period: 0, effect: 0xC, parameter: 0)
        .Build()));

    /// <summary>The fit of the two-pitch saw for <paramref name="target"/>.</summary>
    private static PcmSampleFit FitSaw(PcmTarget target) => PcmFitter.Fit(TwoPitches.Value, TwoPitches.Value.Samples[0], target);

    /// <summary>The plain copies: one per pitch played, close to the sample, in 256-byte blocks for XGM.</summary>
    [Fact]
    public void EachPitchGetsACloseCopy()
    {
        PcmSampleFit fit = FitSaw(PcmTarget.Xgm);
        PcmOption full = fit.Full!;

        Assert.Equal(PcmSavings.None, full.Savings);
        Assert.Equal(2, full.Slots);
        Assert.Equal([1, 1], full.Pitches.Select(p => p.Notes));
        Assert.All(full.Copies, c => Assert.Equal(0, c.Bytes % 256));
        Assert.All(full.Copies, c => Assert.True(c.Bytes >= c.Points));
        Assert.All(full.Pitches, p => Assert.Equal(0, p.CentsOff, 6));
        Assert.InRange(full.MelDb, 0, 12);
        Assert.NotNull(full.HarmonicDb);
        Assert.Equal(PcmRoom.Fits, full.Room);
        Assert.True(fit.Renders >= 2);
    }

    /// <summary>XGM2 tries half speed, a looped sample short loops, and every combination of them is made, the plain one first.</summary>
    [Fact]
    public void EveryCombinationOfTheSavingsIsTried()
    {
        PcmSampleFit fit = FitSaw(PcmTarget.Xgm2);

        Assert.Equal(
            [PcmSavings.None, PcmSavings.HalfSpeed, PcmSavings.ShortLoops, PcmSavings.HalfSpeed | PcmSavings.ShortLoops],
            fit.Options.Select(o => o.Savings));
        PcmOption half = fit.Options[1];
        Assert.All(half.Copies, c => Assert.True(c.HalfSpeed));
        Assert.InRange(half.Bytes, (fit.Full!.Bytes / 2) - 512, (fit.Full.Bytes / 2) + 512);
        Assert.True(fit.Options[2].Bytes < fit.Full.Bytes);
        Assert.Same(fit.Options[3], fit.Smallest);
    }

    /// <summary>DMF copies are 16-bit at a rate code, and DMF's single DAC voice can't play a sample on two channels at once.</summary>
    [Fact]
    public void DmfCopiesAreSixteenBitAndHaveOneVoice()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 428)
            .Build());

        PcmSampleFit dmf = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Dmf);
        PcmSampleFit xgm = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Xgm);

        PcmCopy copy = Assert.Single(dmf.Full!.Copies);
        Assert.Equal(2 * copy.Points, copy.Bytes);
        Assert.Equal(11025, copy.PlayRateHz);
        Assert.Equal(2, dmf.MostAtOnce);
        Assert.Equal(PcmRoom.SampleTooOften, dmf.Full.Room);
        Assert.Equal(PcmRoom.Fits, xgm.Full!.Room);
    }

    /// <summary>Pitches in the same semitone share the most played one's copy, a few cents off for the others.</summary>
    [Fact]
    public void NearPitchesCanShareACopy()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 4, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 8, channel: 0, sample: 1, period: 428, effect: 0xE, parameter: 0x51)
            .Build());

        PcmSampleFit fit = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Xgm);
        PcmOption shared = fit.Options.Single(o => o.Savings == PcmSavings.Semitones);

        Assert.Equal(2, fit.Full!.Slots);
        Assert.Equal(1, shared.Slots);
        Assert.True(shared.Bytes < fit.Full.Bytes);
        PcmPitch moved = shared.Pitches.Single(p => p.CentsOff != 0);
        Assert.Equal(1, moved.Notes);
        Assert.InRange(Math.Abs(moved.CentsOff), 5, 20);
    }

    /// <summary>The semitone a pitch belongs to is the one nearest the pitch heard from the sample's cycle, not the note's.</summary>
    [Fact]
    public void SemitonesFollowTheMeasuredPitch()
    {
        // 8287 Hz over a 64-frame cycle is 129.5 Hz, nearest C3 (−21 semitones from A4); without a cycle, the rate itself.
        Assert.Equal(-21, PcmFitter.Semitone(FmTargets.C2, 64));
        Assert.Equal(-20, PcmFitter.Semitone(FmTargets.C2, 64 / Math.Pow(2, 0.8 / 12)));
        Assert.Equal(51, PcmFitter.Semitone(FmTargets.C2, null));
    }

    /// <summary>A one-shot's quiet tail can be cut, for fewer bytes.</summary>
    [Fact]
    public void AQuietTailCanBeTrimmed()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "pluck", 8192, data: i => (sbyte)(Math.Sin(i * 0.3) * 127 * Math.Exp(-i / 300.0)))
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .Build());

        PcmSampleFit fit = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Xgm);
        PcmOption trimmed = fit.Options.Single(o => o.Savings == PcmSavings.TrimTail);

        Assert.Equal([PcmSavings.None, PcmSavings.TrimTail], fit.Options.Select(o => o.Savings));
        Assert.True(trimmed.Copies[0].Trimmed);
        Assert.True(trimmed.Bytes < fit.Full!.Bytes / 2);
        Assert.InRange(trimmed.MelDb - fit.Full.MelDb, -1, 3);
        Assert.Equal(PcmSavings.TrimTail, fit.Smallest!.Savings);
    }

    /// <summary>A sample the song never plays still gets one copy, at C-2.</summary>
    [Fact]
    public void ASampleNeverPlayedGetsOneCopy()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithSample(2, "unused", 512, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .Build());

        PcmSampleFit fit = PcmFitter.Fit(song, song.Samples[1], PcmTarget.Xgm);

        PcmPitch pitch = Assert.Single(fit.Full!.Pitches);
        Assert.Equal(0, pitch.Notes);
        Assert.Equal(FmTargets.C2, pitch.RateHz, 6);
        Assert.InRange(fit.Full.MelDb, 0, 12);
    }

    /// <summary>A note with vibrato played twice the same way is baked into one sample, closer than the plain copy; one played once is not.</summary>
    [Fact]
    public void RepeatedBentNotesAreBaked()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428, effect: 0x4, parameter: 0x8C)
            .WithCell(0, row: 16, channel: 0, sample: 1, period: 428, effect: 0x4, parameter: 0x8C)
            .WithCell(0, row: 32, channel: 0, sample: 1, period: 214, effect: 0x4, parameter: 0x8C)
            .WithCell(0, row: 48, channel: 0, sample: 1, period: 0, effect: 0xC, parameter: 0)
            .Build());

        PcmSampleFit fit = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Xgm);

        Assert.Equal(3, fit.BentNotes);
        PcmBaked baked = Assert.Single(fit.Baked);
        Assert.Equal(2, baked.Notes);
        Assert.Equal(16 * 6 * PcmSongs.TickSeconds, baked.Seconds, 2);
        Assert.Equal(0, baked.Bytes % 256);
        Assert.True(baked.Score.MelDb < baked.PlainScore.MelDb);
    }

    /// <summary>A silent sample gets no copies.</summary>
    [Fact]
    public void ASilentSampleGetsNothing()
    {
        SongAnalysis song = PcmSongs.Analyze(new ModBuilder()
            .WithSample(1, "silence", 512, data: _ => 0)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .Build());

        PcmSampleFit fit = PcmFitter.Fit(song, song.Samples[0], PcmTarget.Xgm2);

        Assert.Empty(fit.Options);
        Assert.Null(fit.Full);
        Assert.Null(fit.Smallest);
        Assert.Equal(0, fit.Renders);
    }

    /// <summary>Every target's whole song is fitted, in sample order.</summary>
    [Fact]
    public void AWholeSongIsFitted()
    {
        IReadOnlyList<PcmSampleFit> fits = PcmFitter.FitSong(TwoPitches.Value, PcmTarget.Dmf);

        PcmSampleFit fit = Assert.Single(fits);
        Assert.Equal(PcmTarget.Dmf, fit.Target);
        Assert.Equal("saw", fit.Name);
    }

    /// <summary>The combinations come plain first, then by how many savings they use.</summary>
    [Fact]
    public void CombinationsComeSmallestFirst()
    {
        Assert.Equal([PcmSavings.None], PcmFitter.Combinations(PcmSavings.None));
        Assert.Equal(
            [PcmSavings.None, PcmSavings.HalfSpeed, PcmSavings.TrimTail, PcmSavings.HalfSpeed | PcmSavings.TrimTail],
            PcmFitter.Combinations(PcmSavings.HalfSpeed | PcmSavings.TrimTail));
        Assert.Equal(16, PcmFitter.Combinations(PcmSavings.HalfSpeed | PcmSavings.Semitones | PcmSavings.ShortLoops | PcmSavings.TrimTail).Count());
    }

    /// <summary>A trimmed one-shot keeps everything up to its last point within 48 dB of its peak.</summary>
    [Fact]
    public void TrimmingStopsAtTheLastLoudPoint()
    {
        float[] data = [1, 0.5f, 0.01f, 0.001f, 0.0001f];

        Assert.Equal(3, PcmFitter.TrimmedLength(new Sample("s", data, null)));
        Assert.Equal(0, PcmFitter.TrimmedLength(new Sample("empty", [], null)));
    }

    /// <summary>The report logs one Debug line per sample and target with copies, bytes and the smallest option; none for a silent sample.</summary>
    [Fact]
    public void TheReportLogsEveryFit()
    {
        CollectingSink sink = new();
        PcmSampleFit silent = new(4, "empty", PcmTarget.Xgm, 0, [], [], 0, 0, TimeSpan.Zero);

        PcmFitReport.Log(sink.CreateLogger(), [FitSaw(PcmTarget.Xgm2), silent]);

        string line = Assert.Single(sink.Messages(LogEventLevel.Debug));
        Assert.StartsWith("Sample 1 \"saw\", xgm2: 2 copies, ", line, StringComparison.Ordinal);
        Assert.Contains("; smallest half speed+short loops: 2 copies, ", line, StringComparison.Ordinal);
        Assert.Contains(" baked notes of ", line, StringComparison.Ordinal);
        Assert.Equal("too many copies", PcmFitReport.RoomName(PcmRoom.TooManyCopies));
        Assert.Equal("sample too often", PcmFitReport.RoomName(PcmRoom.SampleTooOften));
    }

    /// <summary>A sample the song never plays has one pitch at C-2, held as long as it lasts.</summary>
    [Fact]
    public void PitchesOfAnUnplayedSample()
    {
        PcmFitter.Played played = Assert.Single(PcmFitter.Pitches([], new Sample("s", new float[8287], null)));

        Assert.Equal(0, played.Notes);
        Assert.Equal(1, played.HoldSeconds, 3);
    }
}
