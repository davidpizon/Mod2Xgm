using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Tests.Mod;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PreMixer"/>: overlaps of made-up notes found, mixed, scored and counted against the voices.</summary>
public sealed class PreMixerTests
{
    /// <summary>A short kick and a long bass that start together on rows 0, 8, 16 and 24; each bass is cut by the next, the last rings out.</summary>
    private static readonly Lazy<SongAnalysis> KickAndBass = new(() => PcmSongs.Analyze(Rows(
        new ModBuilder()
            .WithSample(1, "kick", 1024, data: Kick)
            .WithSample(2, "bass", 16384, data: i => (sbyte)(Math.Sin(i * 2 * Math.PI / 64) * 25)),
        [0, 8, 16, 24],
        (0, 1),
        (1, 2)).Build()));

    /// <summary>A kick: a falling, fading tone.</summary>
    private static sbyte Kick(int i) => (sbyte)(Math.Sin(i * 0.002 * (300 - (i / 4.0))) * 100 * Math.Exp(-i / 400.0));

    /// <summary>Puts <paramref name="notes"/> (channel, sample) at C-2 on each of <paramref name="rows"/> of pattern 0.</summary>
    private static ModBuilder Rows(ModBuilder mod, int[] rows, params (int Channel, int Sample)[] notes)
    {
        foreach (int row in rows)
        {
            foreach ((int channel, int sample) in notes)
            {
                mod.WithCell(0, row: row, channel: channel, sample: sample, period: 428);
            }
        }

        return mod;
    }

    /// <summary>The kick over the bass is found each time; the bass goes on as a tail, or is baked to where the next cuts it.</summary>
    [Fact]
    public void ARepeatedOverlapIsOfferedWithATailAndBaked()
    {
        PreMixSongFit fit = PreMixer.Fit(KickAndBass.Value, PcmTarget.Xgm);

        PreMixCandidate tail = Assert.Single(fit.Candidates, c => c.End == PreMixEnd.Tail);
        Assert.Equal([0, 1], tail.Parts.Select(p => p.Sample));
        Assert.Equal([0, 0], tail.Parts.Select(p => p.OffsetTicks));
        Assert.Equal(4, tail.Occurrences);
        Assert.False(tail.SavesSteal);
        Assert.Equal(1, tail.VoicesFreed);
        Assert.Equal(2, tail.Slots);
        Assert.Equal(0, tail.HeadBytes % 256);
        Assert.Equal(1, tail.Tail!.Sample);
        Assert.True(tail.Tail.StartFrame > 900);
        Assert.Equal(tail.Bytes, tail.HeadBytes + tail.Tail.Bytes);
        Assert.True(tail.SwitchSeconds >= tail.WindowSeconds);
        Assert.Equal(Math.Round(tail.SwitchSeconds * 60), tail.SwitchSeconds * 60, 6);
        Assert.InRange(tail.Score.MelDb, 0, 6);
        Assert.NotNull(tail.Seam);
        Assert.Equal(0, tail.ClipDb);

        PreMixCandidate baked = Assert.Single(fit.Candidates, c => c.End == PreMixEnd.Baked);
        Assert.Equal(3, baked.Occurrences);
        Assert.Null(baked.Tail);
        Assert.Equal(1, baked.Slots);
        Assert.True(baked.HeadBytes > tail.HeadBytes);
        Assert.NotNull(baked.Seam);
        Assert.Equal(8, fit.PcmNotes);
        Assert.Equal(0, fit.Steals);
        Assert.Equal(0, fit.StealsRemoved);
    }

    /// <summary>DMF's one DAC steals each bass from its kick; the pre-mixes give every note a voice, and the tail starts on the window's tick.</summary>
    [Fact]
    public void OnDmfThePreMixesRemoveTheSteals()
    {
        PreMixSongFit fit = PreMixer.Fit(KickAndBass.Value, PcmTarget.Dmf);

        Assert.Equal(1, fit.Voices);
        Assert.Equal(4, fit.Steals);
        Assert.Equal(0, fit.StealsWithPreMix);
        Assert.Equal(4, fit.StealsRemoved);
        Assert.All(fit.Candidates, c => Assert.True(c.SavesSteal));
        PreMixCandidate tail = Assert.Single(fit.Candidates, c => c.End == PreMixEnd.Tail);
        Assert.Equal(tail.WindowSeconds, tail.SwitchSeconds);
        Assert.Equal(0, tail.HeadBytes % 2);
        Assert.Equal(2, fit.Candidates.Count(c => c.End == PreMixEnd.Baked));
    }

    /// <summary>Three notes that sound together give every group of two and the three, on every target.</summary>
    [Fact]
    public void EveryGroupOfOverlappingNotesIsOffered()
    {
        SongAnalysis song = PcmSongs.Analyze(Rows(
            new ModBuilder()
                .WithSample(1, "a", 1024, data: Kick)
                .WithSample(2, "b", 1024, data: i => (sbyte)(i * 7 % 128 - 64))
                .WithSample(3, "c", 1024, data: i => (sbyte)(Math.Sin(i * 0.2) * 80)),
            [0, 16],
            (0, 1),
            (1, 2),
            (2, 3)).Build());

        foreach (PreMixSongFit fit in PreMixer.FitSong(song))
        {
            Assert.Equal(
                ["1+2", "1+2+3", "1+3", "2+3"],
                fit.Candidates.Select(PreMixReport.Samples).Order(StringComparer.Ordinal));
            Assert.All(fit.Candidates, c => Assert.Equal(PreMixEnd.None, c.End));
            Assert.All(fit.Candidates, c => Assert.Equal(2, c.Occurrences));
            Assert.Equal(4, fit.Overlaps / 2);
        }
    }

    /// <summary>Two drums that each overlap a long note but not each other make two windows, never one with a gap.</summary>
    [Fact]
    public void AGapMakesTwoWindows()
    {
        ModBuilder mod = new ModBuilder()
            .WithSample(1, "kick", 512, data: Kick)
            .WithSample(2, "pad", 64, loopStart: 0, loopLength: 64, data: i => (sbyte)(Math.Sin(i * 2 * Math.PI / 64) * 50))
            .WithSample(3, "snare", 512, data: i => (sbyte)((i * 37 % 255) - 127));
        foreach (int row in new[] { 0, 16 })
        {
            mod.WithCell(0, row: row, channel: 0, sample: 1, period: 428)
                .WithCell(0, row: row, channel: 1, sample: 2, period: 428)
                .WithCell(0, row: row + 4, channel: 2, sample: 3, period: 428);
        }

        SongAnalysis song = PcmSongs.Analyze(mod.WithCell(0, row: 32, channel: 1, sample: 2, period: 0, effect: 0xC, parameter: 0).Build());
        PreMixSongFit fit = PreMixer.Fit(song, PcmTarget.Xgm2);

        Assert.DoesNotContain(fit.Candidates, c => c.Parts.Count > 2);
        Assert.DoesNotContain(fit.Candidates, c => PreMixReport.Samples(c) == "1+3");
        PreMixCandidate snare = Assert.Single(fit.Candidates, c => PreMixReport.Samples(c) == "2+3");
        Assert.Equal("0+24", PreMixReport.Offsets(snare));
        Assert.Equal(PreMixEnd.Tail, snare.End);
        Assert.DoesNotContain(fit.Candidates, c => c.End == PreMixEnd.Baked);
    }

    /// <summary>A one-shot that outlasts the window's tick but runs out before the next frame has no tail: the pre-mix holds it to its end.</summary>
    [Fact]
    public void ANoteThatRunsOutBeforeTheSwitchHasNoTail()
    {
        // At C-2 (8287 Hz) the kick ends in tick 3 (0.058 s); the bass outlasts that tick but runs out at 0.065 s, before the 0.067 s frame.
        SongAnalysis song = PcmSongs.Analyze(Rows(
            new ModBuilder()
                .WithSample(1, "kick", 480, data: Kick)
                .WithSample(2, "bass", 540, data: i => (sbyte)(Math.Sin(i * 2 * Math.PI / 64) * 25)),
            [0, 16],
            (0, 1),
            (1, 2)).Build());

        PreMixSongFit fit = PreMixer.Fit(song, PcmTarget.Xgm);

        PreMixCandidate mix = Assert.Single(fit.Candidates);
        Assert.Equal(PreMixEnd.None, mix.End);
        Assert.Null(mix.Tail);
        Assert.Equal(2, mix.Occurrences);
    }

    /// <summary>A loud mix is turned down so it doesn't clip, by how much it went over: two full squares are at least twice full scale, more where the resampler rings.</summary>
    [Fact]
    public void ALoudMixIsTurnedDown()
    {
        SongAnalysis song = PcmSongs.Analyze(Rows(
            new ModBuilder()
                .WithSample(1, "square", 1024, data: i => (sbyte)(i % 64 < 32 ? 127 : -128))
                .WithSample(2, "square too", 1024, data: i => (sbyte)(i % 64 < 32 ? 127 : -128)),
            [0, 16],
            (0, 1),
            (1, 2)).Build());

        PreMixCandidate mix = Assert.Single(PreMixer.Fit(song, PcmTarget.Xgm).Candidates);

        Assert.InRange(mix.ClipDb, 6, 9);
    }

    /// <summary>An overlap played once is offered only where it saves a voice steal, and samples kept out never go into one.</summary>
    [Fact]
    public void OnceOnlyWhereItSavesAStealAndKeptOutSamplesNever()
    {
        SongAnalysis song = PcmSongs.Analyze(Rows(
            new ModBuilder()
                .WithSample(1, "kick", 1024, data: Kick)
                .WithSample(2, "hat", 1024, data: i => (sbyte)((i * 37 % 255) - 127)),
            [0],
            (0, 1),
            (1, 2)).Build());

        Assert.Empty(PreMixer.Fit(song, PcmTarget.Xgm).Candidates);
        Assert.Single(PreMixer.Fit(song, PcmTarget.Dmf).Candidates);
        PreMixSongFit kept = PreMixer.Fit(song, PcmTarget.Dmf, new HashSet<int> { 1 });
        Assert.Empty(kept.Candidates);
        Assert.Equal(1, kept.PcmNotes);
        Assert.Equal(0, kept.Steals);
    }

    /// <summary>The report logs a line per target and one per candidate.</summary>
    [Fact]
    public void TheReportLogsEveryTargetAndCandidate()
    {
        CollectingSink sink = new();
        PreMixSongFit fit = PreMixer.Fit(KickAndBass.Value, PcmTarget.Xgm);

        PreMixReport.Log(sink.CreateLogger(), [fit]);

        Assert.Equal(1 + fit.Candidates.Count, sink.Events.Count(e => e.Level == LogEventLevel.Debug));
        Assert.Contains(sink.Messages(LogEventLevel.Debug), m => m.StartsWith("Pre-mix, xgm: 2 candidates (1 with a tail, 1 baked) from 4 overlaps of 8 PCM notes", StringComparison.Ordinal));
        Assert.Contains(sink.Messages(LogEventLevel.Debug), m => m.Contains("samples 1+2 (ticks 0+0) 4 times, tail", StringComparison.Ordinal));
        Assert.Equal("none", PreMixReport.EndName(PreMixEnd.None));
    }
}
