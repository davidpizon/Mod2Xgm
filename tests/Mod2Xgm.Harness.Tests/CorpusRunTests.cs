using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>
/// Tests for <see cref="CorpusRun"/> with stand-in renderers, so they run anywhere: the real conversion and tick player
/// run on a corpus song; the reference and candidate renders are tones.
/// </summary>
public sealed class CorpusRunTests
{
    private const int Rate = 22_050;

    /// <summary>
    /// A reference player that plays a tone and reports a fixed song length. It reaches each row when OpenMPT would
    /// (the tick player's rows timed OpenMPT's way), or every row at time 0 when <paramref name="onTime"/> is false.
    /// </summary>
    private sealed class ToneReference(double durationSeconds, bool onTime = true) : IReferenceRenderer
    {
        public string Name => "tone reference";

        public ReferenceRender Render(byte[] mod, int sampleRate) => new(TestFiles.Tone(2.0, sampleRate), durationSeconds);

        public IReadOnlyList<double> RowTimes(byte[] mod, IReadOnlyList<(int Order, int Row)> rows)
        {
            if (!onTime)
            {
                return [.. rows.Select(_ => 0.0)];
            }

            ConversionNotes silent = new(Logger.None);
            Model.Playback playback = Mod.ModTickPlayer.Play(Mod.ModReader.Read(mod, silent), Model.AmigaPitch.PalClockHz, 1.0, silent);
            Dictionary<(int, int), double> times = DurationCheck.Rows(playback, r => [.. r.Select(_ => 0.0)])
                .ToDictionary(r => (r.Order, r.Row), r => r.OpenMptRulesSeconds);
            return [.. rows.Select(r => times[r])];
        }
    }

    /// <summary>A candidate renderer that returns <paramref name="render"/>, nothing, or fails.</summary>
    private sealed class Candidate(string name, Func<SongFiles, CandidateRender?> render) : ICandidateRenderer
    {
        public string Name => name;

        public List<SongFiles> Seen { get; } = [];

        public CandidateRender? Render(SongFiles song)
        {
            Seen.Add(song);
            return render(song);
        }
    }

    /// <summary>
    /// A song is converted (DMF and log written), its length checked against the reference's, and scored for every
    /// renderer that has a rendering; one with nothing is left out, one that fails is listed with its error.
    /// </summary>
    [Fact]
    public void ASongIsConvertedCheckedAndScored()
    {
        using TempDirectory temp = new();
        Candidate same = new("same", _ => new CandidateRender(TestFiles.Tone(2.0), "a remark"));
        Candidate nothing = new("nothing", _ => null);
        Candidate failing = new("failing", _ => throw new HarnessException("broken renderer"));
        CorpusRun run = new(temp.Path, Rate, saveReferenceWavs: true, new ToneReference(30.72), [same, nothing, failing], Logger.None);

        SongResult result = Assert.Single(run.Run([TestFiles.ShortSong]));

        Assert.Equal("fx-poly1", result.Song);
        Assert.Null(result.Error);
        Assert.True(result.ConversionNotes > 0);
        Assert.NotNull(result.Duration);
        Assert.True(result.Duration.Within);
        Assert.Equal(30.72, result.Duration.TickPlayerSeconds, 0.01);
        Assert.Equal(["same", "failing"], result.Scores.Select(s => s.Renderer));

        CandidateScore score = result.Scores[0];
        Assert.NotNull(score.Score);
        Assert.Equal("a remark", score.Remark);
        Assert.True(score.Score.MelDistanceDb < 1, "an 8 kHz copy of the tone, resampled, is close to the original");
        Assert.Null(result.Scores[1].Score);
        Assert.Equal("broken renderer", result.Scores[1].Remark);

        SongFiles files = Assert.Single(same.Seen);
        Assert.Equal(temp.Combine("dmf", "fx-poly1.dmf"), files.DmfPath);
        Assert.True(File.Exists(files.DmfPath));
        Assert.Contains("Emulating ProTracker", File.ReadAllText(temp.Combine("logs", "fx-poly1.log")), StringComparison.Ordinal);
        Assert.Equal(2.0, WavFile.Read(temp.Combine("wav", "fx-poly1.reference.wav")).Seconds, 0.01);
    }

    /// <summary>
    /// With a clip length, the first seconds of the reference and of each rendering are written for listening and
    /// scored on their own, next to the whole-song scores.
    /// </summary>
    [Fact]
    public void AClipIsWrittenAndScored()
    {
        using TempDirectory temp = new();
        Candidate same = new("same", _ => new CandidateRender(TestFiles.Tone(2.0), null));
        CorpusRun run = new(temp.Path, Rate, saveReferenceWavs: false, new ToneReference(30.72), [same], Logger.None, clipSeconds: 1.5);

        SongResult result = Assert.Single(run.Run([TestFiles.ShortSong]));

        CandidateScore score = Assert.Single(result.Scores);
        Assert.NotNull(score.Score);
        Assert.NotNull(score.Clip);
        Assert.True(score.Clip.MelDistanceDb < 1, "the clip of a copy of the tone is close to the clip of the original");
        Assert.Equal(1.5, WavFile.Read(temp.Combine("wav", "fx-poly1.reference.clip.wav")).Seconds, 0.01);
        Assert.Equal(1.5, WavFile.Read(temp.Combine("wav", "fx-poly1.same.clip.wav")).Seconds, 0.01);
    }

    /// <summary>An end more than a tick away from the reference's is flagged even when every row is on time.</summary>
    [Fact]
    public void AnEndOffByMoreThanATickIsFlagged()
    {
        using TempDirectory temp = new();
        CorpusRun run = new(temp.Path, Rate, saveReferenceWavs: false, new ToneReference(31.0), [], Logger.None);

        SongResult result = Assert.Single(run.Run([TestFiles.ShortSong]));

        Assert.False(result.Duration?.Within);
        Assert.Null(result.Duration?.FirstRowOff);
        Assert.Empty(result.Scores);
        Assert.False(File.Exists(temp.Combine("wav", "fx-poly1.reference.wav")));
    }

    /// <summary>
    /// A row the players start more than a tick apart is flagged, and every row's times are in logs/song.rows.csv. (The
    /// stand-in reaches every row at 0 here, so row 1 is the first off.)
    /// </summary>
    [Fact]
    public void ARowOffByMoreThanATickIsFlagged()
    {
        using TempDirectory temp = new();
        CorpusRun run = new(temp.Path, Rate, saveReferenceWavs: false, new ToneReference(30.72, onTime: false), [], Logger.None);

        SongResult result = Assert.Single(run.Run([TestFiles.ShortSong]));

        Assert.False(result.Duration?.Within);
        Assert.Equal((0, 1), (result.Duration?.FirstRowOff?.Order, result.Duration?.FirstRowOff?.Row));
        string[] rows = File.ReadAllLines(temp.Combine("logs", "fx-poly1.rows.csv"));
        Assert.Equal("order,row,tick_player_s,openmpt_rules_s,reference_s,difference_s", rows[0]);
        Assert.Equal("0,0,0,0,0,0", rows[1]);
    }

    /// <summary>A file that isn't a MOD fails its own song, with the reader's message, and the run goes on.</summary>
    [Fact]
    public void ABadSongIsReportedAndTheRunGoesOn()
    {
        using TempDirectory temp = new();
        string bad = temp.Combine("bad.mod");
        File.WriteAllBytes(bad, [1, 2, 3]);
        CorpusRun run = new(temp.Combine("out"), Rate, saveReferenceWavs: false, new ToneReference(30.72), [], Logger.None);

        IReadOnlyList<SongResult> results = run.Run([bad, TestFiles.ShortSong], jobs: 2);

        Assert.Equal(["bad", "fx-poly1"], results.Select(r => r.Song));
        Assert.Contains("Not a supported MOD file", results[0].Error, StringComparison.Ordinal);
        Assert.Null(results[0].Duration);
        Assert.Null(results[1].Error);
    }

    /// <summary>Two runs over the same song give the same report files (plan T6.3: stable scores on reruns).</summary>
    [Fact]
    public void RerunsGiveTheSameReport()
    {
        using TempDirectory temp = new();
        string Report()
        {
            Candidate copy = new("copy", _ => new CandidateRender(TestFiles.Tone(2.1, Rate, hz: 445), null));
            IReadOnlyList<SongResult> results = new CorpusRun(temp.Path, Rate, false, new ToneReference(30.72), [copy], Logger.None).Run([TestFiles.ShortSong]);
            return HarnessReport.ScoresCsv(results) + HarnessReport.DurationsCsv(results) + HarnessReport.Markdown(results, "r", "v", Rate);
        }

        Assert.Equal(Report(), Report());
    }
}
