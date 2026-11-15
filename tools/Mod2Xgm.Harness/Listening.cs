using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Output;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>
/// One question of a blind listening round, as a row of <c>tests/golden/listening/answers.tsv</c>
/// (docs/adr/0097-learned-listening-score.md, tests/golden/listening/README.md): which clip of which song, the recipe of each
/// version, the hashes of the clips David heard, and his answer.
/// </summary>
/// <param name="Date">The day of the round, <c>yyyy-MM-dd</c>.</param>
/// <param name="Round">The round's name, e.g. <c>r20</c>.</param>
/// <param name="Question">The question within the round, e.g. <c>q1</c>.</param>
/// <param name="Song">The MOD's file name, found among the harness's inputs.</param>
/// <param name="StartSeconds">Where the clip starts in the song.</param>
/// <param name="Seconds">How long the clip is.</param>
/// <param name="Format">The <c>--format</c> both versions are made for.</param>
/// <param name="RecipeA">Version A's recipe (<see cref="Recipe"/>).</param>
/// <param name="RecipeB">Version B's recipe.</param>
/// <param name="Commit">The commit that made the clips; empty until they are made.</param>
/// <param name="Sha256A">The SHA-256 of version A's clip file; empty until it is made.</param>
/// <param name="Sha256B">The SHA-256 of version B's clip file; empty until it is made.</param>
/// <param name="Pick">
/// <c>a</c>, <c>b</c>, <c>same</c> (can't tell), <c>neither</c> (both unacceptable,
/// docs/adr/0111-neither-and-listening-models.md), or empty while the question waits for an answer.
/// </param>
/// <param name="GradeA">A 1–5 grade of version A, when David gave one; empty otherwise.</param>
/// <param name="GradeB">A 1–5 grade of version B, when David gave one; empty otherwise.</param>
/// <param name="Words">What David said about it, with tabs and line breaks as spaces.</param>
internal sealed record ListeningRow(
    string Date,
    string Round,
    string Question,
    string Song,
    double StartSeconds,
    double Seconds,
    string Format,
    string RecipeA,
    string RecipeB,
    string Commit,
    string Sha256A,
    string Sha256B,
    string Pick,
    string GradeA,
    string GradeB,
    string Words)
{
    /// <summary>The row's name in logs and clip file names: round and question.</summary>
    public string Name => Round + "-" + Question;

    /// <summary>
    /// True when the pick ranks the two versions: <c>a</c>, <c>b</c> or <c>same</c>. A <c>neither</c> says both are
    /// unacceptable, not which is closer, so the fit, the hold-out and the embeddings leave it out (David, plan T9.4s1).
    /// </summary>
    public bool Ranks => Pick is "a" or "b" or "same";
}

/// <summary>Reads and writes the answers file: tab-separated, a header line, invariant numbers (tests/golden/listening/README.md).</summary>
internal static class AnswersFile
{
    /// <summary>The header line, which every file starts with.</summary>
    public const string Header = "date\tround\tquestion\tsong\tstart_s\tseconds\tformat\trecipe_a\trecipe_b\tcommit\tsha256_a\tsha256_b\tpick\tgrade_a\tgrade_b\twords";

    private static readonly string[] Picks = ["", "a", "b", "same", "neither"];
    private static readonly string[] Grades = ["", "1", "2", "3", "4", "5"];

    /// <summary>Reads the rows of <paramref name="text"/>; throws <see cref="HarnessException"/> naming the line of any row it can't read.</summary>
    public static IReadOnlyList<ListeningRow> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0] != Header)
        {
            throw new HarnessException("line 1: not an answers file (the header line differs; tests/golden/listening/README.md)");
        }

        List<ListeningRow> rows = [];
        HashSet<string> names = [];
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            string[] cells = lines[i].Split('\t');
            int line = i + 1;
            if (cells.Length != 16)
            {
                throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"line {line}: {cells.Length} columns, 16 expected"));
            }

            ListeningRow row = new(cells[0], cells[1], cells[2], cells[3], Number(cells[4], line, "start_s"), Number(cells[5], line, "seconds"), cells[6], cells[7], cells[8], cells[9], cells[10], cells[11], cells[12], cells[13], cells[14], cells[15]);
            Check(row, line);
            if (!names.Add(row.Name))
            {
                throw new HarnessException($"line {line}: {row.Name} is in the file twice");
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The file holding <paramref name="rows"/>, in order, with the header and a line break after each row.</summary>
    public static string Format(IEnumerable<ListeningRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        StringBuilder text = new(Header + "\n");
        foreach (ListeningRow r in rows)
        {
            string[] cells =
            [
                r.Date, r.Round, r.Question, r.Song, Number(r.StartSeconds), Number(r.Seconds), r.Format, r.RecipeA, r.RecipeB,
                r.Commit, r.Sha256A, r.Sha256B, r.Pick, r.GradeA, r.GradeB, OneLine(r.Words),
            ];
            text.Append(string.Join('\t', cells)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A number as the file writes it: invariant, as short as it reads back exactly.</summary>
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double Number(string cell, int line, string column) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0
            ? value
            : throw new HarnessException($"line {line}: {column} \"{cell}\" is not a number of seconds");

    private static string OneLine(string words) => words.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Throws when a cell holds a value the file never has.</summary>
    private static void Check(ListeningRow row, int line)
    {
        if (row.Round.Length == 0 || row.Question.Length == 0 || row.Song.Length == 0)
        {
            throw new HarnessException($"line {line}: round, question and song can't be empty");
        }

        if (row.Seconds <= 0)
        {
            throw new HarnessException($"line {line}: seconds must be more than 0");
        }

        if (!Picks.Contains(row.Pick, StringComparer.Ordinal))
        {
            throw new HarnessException($"line {line}: pick \"{row.Pick}\" is not a, b, same, neither or empty");
        }

        if (!Grades.Contains(row.GradeA, StringComparer.Ordinal) || !Grades.Contains(row.GradeB, StringComparer.Ordinal))
        {
            throw new HarnessException($"line {line}: grades are 1 to 5, or empty");
        }

        if ((row.Sha256A.Length == 0) != (row.Sha256B.Length == 0) || (row.Sha256A.Length == 0 && row.Pick.Length > 0))
        {
            throw new HarnessException($"line {line}: an answered row needs both clips' hashes, so it can be checked");
        }
    }
}

/// <summary>
/// How one version of a clip is made (docs/adr/0097-learned-listening-score.md): the converter's own options, plus rules no
/// option sets, written <c>rule:name=value</c>. The harness adds <c>--format</c>, the output paths and <c>--progress</c>.
/// </summary>
/// <param name="Arguments">Converter options, as on its command line.</param>
/// <param name="HalfSpeed">Samples (1-based, as on the command line) XGM2 stores at half speed: <c>rule:half-speed=4,7</c>.</param>
/// <param name="Layers">False with <c>rule:layers=off</c>: no note gets a second voice (docs/adr/0088-layering.md).</param>
/// <param name="PreMix">False with <c>rule:pre-mix=off</c>: no recordings are pre-mixed (docs/adr/0094-pre-mixed-voice-steals.md).</param>
/// <remarks>Level rules (<c>rule:quieter</c>, <c>rule:song-quieter</c>) are in <see cref="Levels"/>: a retry's fixes (docs/adr/0112-retry-list.md).</remarks>
/// <remarks>Squeeze rules (<c>rule:shrink=off</c>, <c>rule:keep</c>) are in <see cref="Shrink"/> and <see cref="Keep"/>: a squeeze round's ways (docs/adr/0113-squeeze-rounds.md).</remarks>
internal sealed record Recipe(IReadOnlyList<string> Arguments, IReadOnlySet<int> HalfSpeed, bool Layers = true, bool PreMix = true)
{
    /// <summary>The rules a recipe can give, for the usage message.</summary>
    private const string KnownRules = "rule:half-speed=<samples>, rule:layers=off, rule:pre-mix=off, rule:pan=today|short|never, rule:pan-free=<seconds>,<volume>,<distance>, "
        + "rule:pan-span=song|order, rule:pan-fix=<middle+fm+unmix or none>, rule:pan-cost=<dB>, rule:dac-loser=noise|fm|noise-all-song, "
        + "rule:quieter=<sample>:<dB>[,…], rule:song-quieter=<dB>, rule:half-speed=all, rule:shrink=off, rule:keep=importance|drums|tonal";

    /// <summary>The most dB a level rule turns a sound down: past it a recording is all but silent on XGM2's 3 dB steps.</summary>
    private const double MaxQuieterDb = 24;

    /// <summary>The highest sample number a MOD has (docs/formats/mod.md#sample-header).</summary>
    private const int MaxSample = 31;

    /// <summary>
    /// How much quieter the plan plays with <c>rule:quieter=…</c> and <c>rule:song-quieter=…</c> (docs/adr/0112-retry-list.md),
    /// or <c>null</c> for its own levels.
    /// </summary>
    public LevelRules? Levels { get; private init; }

    /// <summary>False with <c>rule:shrink=off</c>: recordings aren't shrunk to fit, so notes give them up to FM/PSG instead (docs/adr/0113-squeeze-rounds.md).</summary>
    public bool Shrink { get; private init; } = true;

    /// <summary>Which tracks keep a recording first with <c>rule:keep=…</c> (docs/adr/0113-squeeze-rounds.md).</summary>
    public KeepFirst Keep { get; private init; }

    /// <summary>
    /// The optimizer's pan rules with <c>rule:pan…</c> (docs/adr/0109-pan-swap-cost.md), or <c>null</c> for the converter's own.
    /// <c>rule:pan=short</c> alone lets stand a swap off side for 0.1 s or less, at half volume or less, to or from the middle.
    /// </summary>
    public PanRules? Pan { get; private init; }

    /// <summary>The simple mapper's choice for a drum that loses the DAC with <c>rule:dac-loser=…</c>, or <c>null</c> for its own.</summary>
    public Mapping.DacLoser? Loser { get; private init; }

    /// <summary>The swap <c>rule:pan=short</c> lets stand when <c>rule:pan-free</c> doesn't say: 0.1 s off side, half volume, to or from the middle.</summary>
    private static readonly PanRules ShortQuiet = PanRules.Today with { Allow = PanSwapAllowance.ShortQuiet, FreeSeconds = 0.1, FreeVolume = 0.5, FreeDistance = 1 };

    /// <summary>Options the harness sets itself, which a recipe can't.</summary>
    private static readonly string[] Reserved = ["--format", "-f", "--output", "-o", "--render-wav", "--progress", "--force", "--info"];

    /// <summary>Reads a recipe cell; throws a usage <see cref="HarnessException"/> for a rule it doesn't know or an option the harness sets.</summary>
    public static Recipe Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> arguments = [];
        HashSet<int> halfSpeed = [];
        bool layers = true;
        bool preMix = true;
        PanRules? pan = null;
        Mapping.DacLoser? loser = null;
        LevelRules? levels = null;
        bool shrink = true;
        KeepFirst keep = KeepFirst.Importance;
        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("rule:", StringComparison.Ordinal))
            {
                string[] parts = token["rule:".Length..].Split('=', 2);
                switch (parts)
                {
                    case ["layers", "off"]:
                        layers = false;
                        continue;
                    case ["pre-mix", "off"]:
                        preMix = false;
                        continue;
                    case ["pan", "today"]:
                        pan = (pan ?? PanRules.Today) with { Allow = PanSwapAllowance.All };
                        continue;
                    case ["pan", "never"]:
                        pan = (pan ?? PanRules.Today) with { Allow = PanSwapAllowance.None };
                        continue;
                    case ["pan", "short"]:
                        pan = pan is null ? ShortQuiet : pan with { Allow = PanSwapAllowance.ShortQuiet };
                        continue;
                    case ["pan-free", { } free]:
                        pan = Free(pan ?? ShortQuiet, free, text);
                        continue;
                    case ["pan-span", "song"]:
                        pan = (pan ?? PanRules.Today) with { Span = SteadyMiddleSpan.Song };
                        continue;
                    case ["pan-span", "order"]:
                        pan = (pan ?? PanRules.Today) with { Span = SteadyMiddleSpan.Order };
                        continue;
                    case ["pan-fix", { } fixes]:
                        pan = (pan ?? PanRules.Today) with { Fixes = Fixes(fixes, text) };
                        continue;
                    case ["pan-cost", { } db] when double.TryParse(db, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost) && cost >= 0:
                        pan = (pan ?? PanRules.Today) with { OffSideDb = cost };
                        continue;
                    case ["dac-loser", "noise"]:
                        loser = Mapping.DacLoser.Noise;
                        continue;
                    case ["dac-loser", "fm"]:
                        loser = Mapping.DacLoser.Fm;
                        continue;
                    case ["dac-loser", "noise-all-song"]:
                        loser = Mapping.DacLoser.NoiseAllSong;
                        continue;
                    case ["quieter", { } list]:
                        levels = Quieter(levels ?? LevelRules.None, list, text);
                        continue;
                    case ["song-quieter", { } db]:
                        levels = (levels ?? LevelRules.None) with { SongDb = Decibels(db, "song-quieter", text) };
                        continue;
                    case ["half-speed", "all"]:
                        halfSpeed.UnionWith(Enumerable.Range(1, MaxSample));
                        continue;
                    case ["shrink", "off"]:
                        shrink = false;
                        continue;
                    case ["keep", "importance"]:
                        keep = KeepFirst.Importance;
                        continue;
                    case ["keep", "drums"]:
                        keep = KeepFirst.Drums;
                        continue;
                    case ["keep", "tonal"]:
                        keep = KeepFirst.Tonal;
                        continue;
                    case ["half-speed", _]:
                        break;
                    default:
                        throw HarnessException.Usage($"recipe \"{text}\": unknown rule {token} (rules: {KnownRules})");
                }

                foreach (string sample in parts[1].Split(','))
                {
                    halfSpeed.Add(int.TryParse(sample, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= MaxSample
                        ? n
                        : throw HarnessException.Usage($"recipe \"{text}\": {sample} is not a sample number from 1 to 31"));
                }
            }
            else if (Reserved.Contains(token.Split('=')[0], StringComparer.Ordinal))
            {
                throw HarnessException.Usage($"recipe \"{text}\": {token} is set by the harness, not the recipe");
            }
            else
            {
                arguments.Add(token);
            }
        }

        return new Recipe(arguments, halfSpeed, layers, preMix) { Pan = pan, Loser = loser, Levels = levels, Shrink = shrink, Keep = keep };
    }

    /// <summary>True when the recipe has a rule only <c>--mapping optimizer</c> reads.</summary>
    public bool HasRules => HalfSpeed.Count > 0 || !Layers || !PreMix || Pan is not null || Levels is not null || !Shrink || Keep != KeepFirst.Importance;

    /// <summary><c>rule:quieter=3:6,7:3</c>: samples (1-based) and how many dB quieter each plays as a recording.</summary>
    private static LevelRules Quieter(LevelRules levels, string list, string text)
    {
        Dictionary<int, double> quieter = new(levels.RecordingDb);
        foreach (string item in list.Split(','))
        {
            string[] parts = item.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int sample) || sample is < 1 or > 31)
            {
                throw HarnessException.Usage($"recipe \"{text}\": rule:quieter={list}: {item} is not <sample 1–31>:<dB>, e.g. 3:6");
            }

            quieter[sample - 1] = Decibels(parts[1], "quieter", text);
        }

        return levels with { RecordingDb = quieter };
    }

    /// <summary>A level rule's dB: more than 0, at most <see cref="MaxQuieterDb"/>.</summary>
    private static double Decibels(string value, string rule, string text) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && db is > 0 and <= MaxQuieterDb
            ? db
            : throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"recipe \"{text}\": rule:{rule}: {value} is not a number of dB above 0 and at most {MaxQuieterDb}"));

    /// <summary><c>rule:pan-free=seconds,volume,distance</c>: the swaps <see cref="PanSwapAllowance.ShortQuiet"/> lets stand.</summary>
    private static PanRules Free(PanRules pan, string value, string text)
    {
        string[] parts = value.Split(',');
        return parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double volume) && volume is >= 0 and <= 1
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int distance) && distance is 1 or 2
            ? pan with { Allow = PanSwapAllowance.ShortQuiet, FreeSeconds = seconds, FreeVolume = volume, FreeDistance = distance }
            : throw HarnessException.Usage($"recipe \"{text}\": rule:pan-free={value} needs seconds, a volume from 0 to 1 and a distance of 1 or 2, e.g. 0.1,0.5,1");
    }

    /// <summary><c>rule:pan-fix=middle+fm+unmix</c>, or <c>none</c>: what the allocator may do in place of a swap.</summary>
    private static PanFixes Fixes(string value, string text)
    {
        PanFixes fixes = PanFixes.None;
        foreach (string name in value.Split('+'))
        {
            fixes |= name switch
            {
                "none" => PanFixes.None,
                "middle" => PanFixes.SteadyMiddle,
                "fm" => PanFixes.MoveToFm,
                "unmix" => PanFixes.SkipPreMix,
                _ => throw HarnessException.Usage($"recipe \"{text}\": rule:pan-fix={value}: {name} is not middle, fm, unmix or none"),
            };
        }

        return fixes;
    }

    /// <summary>The shrink rules the recipe asks for, or <c>null</c> for the converter's own.</summary>
    public ShrinkRules? Rules => (Shrink, HalfSpeed.Count) switch
    {
        (true, 0) => null,
        (true, _) => ShrinkRules.Default with { ForcedHalfSpeed = HalfSpeed.Select(s => s - 1).ToHashSet() },
        _ => ShrinkRules.None with { ForcedHalfSpeed = HalfSpeed.Select(s => s - 1).ToHashSet() },
    };
}

/// <summary>Cuts a listening clip out of a 16-bit stereo WAV, byte for byte, so a clip made again from the same render is the same file.</summary>
internal static class ClipWav
{
    private const int HeaderBytes = 44;
    private const int FrameBytes = 4;

    /// <summary>
    /// The frames from <paramref name="startSeconds"/> for <paramref name="seconds"/> of <paramref name="wav"/> (16-bit
    /// stereo PCM), as a WAV file with a plain 44-byte header; shorter when the song ends first.
    /// </summary>
    public static byte[] Cut(ReadOnlySpan<byte> wav, double startSeconds, double seconds)
    {
        (int rate, int dataStart, int dataLength) = Layout(wav);
        long first = (long)Math.Round(startSeconds * rate) * FrameBytes;
        long count = (long)Math.Round(seconds * rate) * FrameBytes;
        int from = (int)Math.Min(first, dataLength);
        int length = (int)Math.Min(count, dataLength - from);
        byte[] clip = new byte[HeaderBytes + length];
        Span<byte> h = clip;
        Encoding.ASCII.GetBytes("RIFF", h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(clip.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ", h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(rate * FrameBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], FrameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        Encoding.ASCII.GetBytes("data", h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)length);
        wav.Slice(dataStart + from, length).CopyTo(h[HeaderBytes..]);
        return clip;
    }

    /// <summary>The rate and where the samples are; throws <see cref="HarnessException"/> unless it is 16-bit stereo PCM.</summary>
    private static (int Rate, int DataStart, int DataLength) Layout(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav[..4]) != "RIFF" || Encoding.ASCII.GetString(wav.Slice(8, 4)) != "WAVE")
        {
            throw new HarnessException("not a WAV file (no RIFF/WAVE header at byte offset 0)");
        }

        int rate = 0;
        bool stereo16 = false;
        for (int offset = 12; offset + 8 <= wav.Length;)
        {
            string id = Encoding.ASCII.GetString(wav.Slice(offset, 4));
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..]), wav.Length - offset - 8);
            if (id == "fmt " && size >= 16)
            {
                stereo16 = BinaryPrimitives.ReadUInt16LittleEndian(wav[(offset + 10)..]) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(wav[(offset + 22)..]) == 16;
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 12)..]);
            }
            else if (id == "data")
            {
                return stereo16 && rate > 0
                    ? (rate, offset + 8, size / FrameBytes * FrameBytes)
                    : throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"byte offset {offset}: samples before a 16-bit stereo format chunk"));
            }

            offset += 8 + size + (size & 1);
        }

        throw new HarnessException("no data chunk");
    }

    /// <summary>The SHA-256 of <paramref name="bytes"/> in lower-case hex, as the answers file stores it.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>One row after <see cref="ListeningRemake.Remake"/>.</summary>
/// <param name="Row">The row as the answers file has it.</param>
/// <param name="Heard">True when the row has hashes and both clips came out byte for byte as David heard them.</param>
/// <param name="A">Version A's features against the original's clip; <c>null</c> without a reference player.</param>
/// <param name="B">Version B's features; <c>null</c> without a reference player.</param>
/// <param name="BytesA">The size of version A's output file, or <c>null</c> when the converter wrote none (XGM has no writer yet).</param>
/// <param name="BytesB">The size of version B's output file, or <c>null</c>.</param>
internal sealed record RemadeRow(ListeningRow Row, bool Heard, ListeningFeatures? A, ListeningFeatures? B, long? BytesA = null, long? BytesB = null);

/// <summary>What <see cref="ListeningRemake.Remake"/> did.</summary>
/// <param name="ExitCode">0 when every row with hashes remade byte for byte, 1 when one didn't.</param>
/// <param name="Rows">Every row, in the file's order.</param>
internal sealed record RemakeResult(int ExitCode, IReadOnlyList<RemadeRow> Rows);

/// <summary>
/// <c>--remake</c>: makes every row's clips again from their recipes and checks them against the hashes of the clips David
/// heard (docs/harness.md#listening-answers). Writes the clips, the original's clip for listening, and <c>remade.tsv</c>:
/// the file with the commit and hashes filled in for rows that had none (a new round's questions).
/// </summary>
/// <param name="songs">The MODs the rows may name, by file name.</param>
/// <param name="outputDirectory">Where <c>listening/</c> goes.</param>
/// <param name="reference">Renders the original's clip, or <c>null</c> to skip it.</param>
/// <param name="logger">Where each row's result is logged.</param>
internal sealed class ListeningRemake(IReadOnlyList<string> songs, string outputDirectory, IReferenceRenderer? reference, ILogger logger)
{
    /// <summary>The rate the converter's <c>--render-wav</c> writes, and the original's clip is rendered at.</summary>
    private const int ClipRate = 44_100;

    /// <summary>Header of <c>features.tsv</c>: each clip's distances from the original's clip (docs/harness.md#listening-answers).</summary>
    public const string FeaturesHeader = "row\tversion\tmel_db\tmfcc\tonset_f1\tnmr_db\tnsim_distance\tmodulation_db\tharmonicity_db\tlearned";

    /// <summary>Remakes every row of <paramref name="answersPath"/>; 0 when every hash matched, 1 when one didn't.</summary>
    public int Run(string answersPath, IConversionProgress progress) => Remake(answersPath, progress).ExitCode;

    /// <summary>
    /// Remakes every row of <paramref name="answersPath"/> that <paramref name="include"/> takes (every row when it is
    /// <c>null</c>), writes the clips, <c>remade.tsv</c> and (with a reference player) <c>features.tsv</c>, and returns each
    /// remade row's result for the fit. A row left out is copied to <c>remade.tsv</c> as it is and isn't in the result.
    /// </summary>
    public RemakeResult Remake(string answersPath, IConversionProgress progress, Func<ListeningRow, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(answersPath);
        ArgumentNullException.ThrowIfNull(progress);
        IReadOnlyList<ListeningRow> all = AnswersFile.Parse(File.ReadAllText(answersPath));
        HashSet<ListeningRow> rows = [.. include is null ? all : all.Where(include)];
        progress.Begin(new RunInfo(answersPath, RunInfo.HarnessFormat, "normal", [.. all.Where(rows.Contains).Select((r, i) => new PhaseInfo(i + 1, "row:" + r.Name, r.Name, string.Empty))]));
        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(folder);
        string commit = Commit();
        List<ListeningRow> remade = [];
        List<RemadeRow> results = [];
        StringBuilder features = new StringBuilder(FeaturesHeader).Append('\n');
        int mismatches = 0;
        foreach (ListeningRow row in all)
        {
            if (!rows.Contains(row))
            {
                remade.Add(row);
                continue;
            }

            IPhase phase = progress.Start("row:" + row.Name);
            (byte[] clipA, long? bytesA) = Version(row, row.RecipeA, Path.Combine(folder, row.Name + "-a.wav"));
            (byte[] clipB, long? bytesB) = Version(row, row.RecipeB, Path.Combine(folder, row.Name + "-b.wav"));
            string a = ClipWav.Sha256(clipA);
            string b = ClipWav.Sha256(clipB);
            ListeningFeatures? featuresA = null;
            ListeningFeatures? featuresB = null;
            if (Original(row, Path.Combine(folder, row.Name + "-o.wav")) is { } original)
            {
                featuresA = Features(features, row.Name, "a", original, clipA);
                featuresB = Features(features, row.Name, "b", original, clipB);
            }

            results.Add(new RemadeRow(row, row.Sha256A.Length > 0 && a == row.Sha256A && b == row.Sha256B, featuresA, featuresB, bytesA, bytesB));
            if (row.Sha256A.Length == 0)
            {
                logger.Information("{Row:l}: made, {Song:l} from {Start} s", row.Name, row.Song, row.StartSeconds);
                remade.Add(row with { Commit = commit, Sha256A = a, Sha256B = b });
            }
            else
            {
                bool same = a == row.Sha256A && b == row.Sha256B;
                mismatches += same ? 0 : 1;
                if (same)
                {
                    logger.Information("{Row:l}: both clips remade byte for byte", row.Name);
                }
                else
                {
                    logger.Warning("{Row:l}: remade clips differ from those heard (A {A:l}, B {B:l})", row.Name, a == row.Sha256A ? "same" : "differs", b == row.Sha256B ? "same" : "differs");
                }

                remade.Add(row);
            }

            phase.Done();
        }

        File.WriteAllText(Path.Combine(folder, "remade.tsv"), AnswersFile.Format(remade));
        if (reference is not null)
        {
            File.WriteAllText(Path.Combine(folder, "features.tsv"), features.ToString());
        }

        logger.ForContext(new ReportRow("result", ("output", folder), ("failed", mismatches), ("rows", rows.Count))).Information(
            "Listening clips: {Folder} ({Failed} of {Count} rows differ from the clips heard)",
            folder,
            mismatches,
            rows.Count);
        if (rows.Count < all.Count)
        {
            logger.Information("{Skipped} rows left out of this run, copied to remade.tsv as they are", all.Count - rows.Count);
        }
        return new RemakeResult(mismatches == 0 ? 0 : 1, results);
    }

    /// <summary>
    /// Converts the row's song with <paramref name="recipeText"/>, cuts the clip, writes it to <paramref name="clipPath"/> and
    /// returns its bytes and the size of the output file, <c>null</c> when the converter wrote none.
    /// </summary>
    private (byte[] Clip, long? OutputBytes) Version(ListeningRow row, string recipeText, string clipPath)
    {
        string work = Path.Combine(Path.GetDirectoryName(clipPath)!, "work"); // clipPath is always inside the listening folder
        Directory.CreateDirectory(work);
        string stem = Path.Combine(work, Path.GetFileNameWithoutExtension(clipPath));
        string output = stem + ".out";

        // An output left by an earlier run must never stand in for this one's size.
        File.Delete(output);
        string wav = Render(SongPath(row.Song), row.Format, recipeText, stem, null, row.Name);
        byte[] clip = ClipWav.Cut(File.ReadAllBytes(wav), row.StartSeconds, row.Seconds);
        File.WriteAllBytes(clipPath, clip);
        return (clip, File.Exists(output) ? new FileInfo(output).Length : null);
    }

    /// <summary>
    /// Converts <paramref name="songPath"/> for <paramref name="format"/> with <paramref name="recipeText"/>, writing the
    /// output to <c>stem.out</c> and the render to <c>stem.genesis.wav</c>, whose path it returns. <paramref name="analyzed"/>
    /// gets the conversion's song analysis when given, and <paramref name="pans"/> the render's sides (docs/adr/0108-pan-swap-count.md);
    /// <paramref name="name"/> names the version in an error.
    /// </summary>
    internal static string Render(string songPath, string format, string recipeText, string stem, Action<SongAnalysis>? analyzed, string name, PanLog? pans = null)
    {
        Recipe recipe = Recipe.Parse(recipeText);
        string wav = stem + ".genesis.wav";
        string[] args = [songPath, "--format", format, .. recipe.Arguments, "--output", stem + ".out", "--render-wav", wav, "--force", "--progress", "silent"];

        // A render left by an earlier run must never stand in for this one (a recipe of --help converts nothing).
        File.Delete(wav);
        ExitCode code = CommandLineDefinition.Invoke(
            args,
            options => Convert(options with { PanLog = pans }, recipe, recipeText, wav, analyzed),
            TextWriter.Null,
            TextWriter.Null);
        return File.Exists(wav)
            ? wav
            : throw new HarnessException($"{name}: the converter made no render for \"{recipeText}\" (exit {(int)code})");
    }

    /// <summary>
    /// Runs the conversion with the recipe's rules. A rule needs <c>--mapping optimizer</c>, the only mapper that reads it, so
    /// it is refused without, rather than making a version the recipe misdescribes. XGM has no writer yet: the converter
    /// writes the render and then fails for the missing file, which is the render this needs.
    /// </summary>
    private static ExitCode Convert(ConversionOptions options, Recipe recipe, string recipeText, string wav, Action<SongAnalysis>? analyzed)
    {
        if (recipe.HasRules && !options.UsesOptimizer)
        {
            throw HarnessException.Usage($"recipe \"{recipeText}\": its rules need --mapping optimizer");
        }

        if (recipe.Levels is not null && options.Format == OutputFormat.Dmf)
        {
            throw HarnessException.Usage($"recipe \"{recipeText}\": rule:quieter and rule:song-quieter are for XGM and XGM2; a DMF plays its own levels");
        }

        if (recipe.Loser is not null && options.UsesOptimizer)
        {
            throw HarnessException.Usage($"recipe \"{recipeText}\": rule:dac-loser is for the simple mapper, not --mapping optimizer");
        }

        try
        {
            ConversionOptions ruled = options with { ShrinkRules = recipe.Rules, PreMixRules = recipe.PreMix ? null : PreMixRules.None, Layers = recipe.Layers, PanRules = recipe.Pan, DacLoser = recipe.Loser, LevelRules = recipe.Levels, KeepFirst = recipe.Keep };
            return ConversionCommand.Execute(ruled, Serilog.Core.Logger.None, analyzed, NoProgress.Instance);
        }
        catch (ConversionException ex) when (ex.Code == ExitCode.UsageError && options.Format == OutputFormat.Xgm && File.Exists(wav))
        {
            return ExitCode.Success;
        }
    }

    /// <summary>
    /// The original's clip through the reference renderer, for listening and for the features; not hashed (it depends on
    /// libopenmpt's version). <c>null</c> without a reference renderer.
    /// </summary>
    private StereoAudio? Original(ListeningRow row, string path)
    {
        if (reference is null)
        {
            return null;
        }

        StereoAudio audio = reference.Render(File.ReadAllBytes(SongPath(row.Song)), ClipRate).Audio;
        int start = Math.Min(audio.Left.Length, (int)Math.Round(row.StartSeconds * ClipRate));
        int length = Math.Min(audio.Left.Length - start, (int)Math.Round(row.Seconds * ClipRate));
        StereoAudio clip = new(audio.Left.AsSpan(start, length).ToArray(), audio.Right.AsSpan(start, length).ToArray(), ClipRate);
        WavFile.Write(path, clip);
        return clip;
    }

    /// <summary>
    /// Appends one <c>features.tsv</c> line to <paramref name="table"/>: <paramref name="clip"/> scored against the original's
    /// clip, every measure the learned listening score may weigh (docs/adr/0097-learned-listening-score.md,
    /// docs/adr/0100-perceptual-features.md) and the learned score itself, empty until it is fitted. Returns the features.
    /// </summary>
    private static ListeningFeatures? Features(StringBuilder table, string row, string version, StereoAudio original, ReadOnlySpan<byte> clip)
    {
        ScoreReport score = Measure(original, clip);
        PerceptualReport? p = score.Perceptual;
        table.AppendJoin(
            '\t',
            row,
            version,
            Number(score.MelDistanceDb),
            Number(score.MfccDistance),
            Number(score.OnsetF1),
            Number(p?.NoiseToMaskDb),
            Number(p?.NsimDistance),
            Number(p?.ModulationDistanceDb),
            Number(p?.HarmonicityDistanceDb),
            Number(ListeningScore.Of(score)));
        table.Append('\n');
        return ListeningFeatures.From(score);
    }

    /// <summary><paramref name="clip"/> (a WAV file) scored against the original's clip, with every measure the learned score may weigh.</summary>
    internal static ScoreReport Measure(StereoAudio original, ReadOnlySpan<byte> clip) =>
        SongScorer.Compare(original.ToMono(), WavFile.Read(clip).ToMono(), ScoreOptions.Default, Serilog.Core.Logger.None);

    private static string Number(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    private string SongPath(string song) =>
        songs.FirstOrDefault(s => string.Equals(Path.GetFileName(s), song, StringComparison.OrdinalIgnoreCase))
        ?? throw new HarnessException($"{song}: not among the harness's inputs");

    /// <summary>The commit the harness was built from, as the converter's version stamps it, or <c>unknown</c>.</summary>
    private static string Commit()
    {
        string? version = typeof(Mod2Xgm.Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version is not null && version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0 ? version[(plus + 1)..] : "unknown";
    }
}
