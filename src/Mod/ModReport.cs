using System.Globalization;
using Serilog;

namespace Mod2Xgm.Mod;

/// <summary>The song's speed and tempo when it starts playing.</summary>
/// <param name="Speed">Ticks per row.</param>
/// <param name="Bpm">Beats per minute (the tick rate is BPM × 2 / 5 Hz); fractional for Ultimate Soundtracker's tempo byte.</param>
internal readonly record struct InitialTempo(int Speed, double Bpm);

/// <summary>
/// Console report sections 1 (MOD metadata) and 2 (the full sample table), printed on every conversion right after
/// the MOD is read (docs/cli.md#console-report, plan T2.5). Section 3 (output instruments) comes with the mapper.
/// </summary>
internal static class ModReport
{
    // docs/formats/mod.md#effects-protracker: Fxx below 0x20 sets the speed, from 0x20 up the BPM; F00 stops the song.
    private const int SetSpeed = 0xF;
    private const int FirstTempoValue = 0x20;

    /// <summary>ProTracker's speed and BPM before any <c>Fxx</c> (docs/formats/mod.md#effects-protracker).</summary>
    private static readonly InitialTempo DefaultTempo = new(6, 125);

    // Widest name: 22 bytes (docs/formats/mod.md#sample-header) plus the two quotes.
    private const int NameColumnWidth = 22 + 2;

    private const string NoLoop = "—";

    /// <summary>Logs section 1, the metadata the file embeds, at <c>Information</c>.</summary>
    public static void LogMetadata(ILogger logger, ModModule module, string fileName) =>
        WriteLines(logger, MetadataLines(module, fileName));

    /// <summary>Logs section 2, every sample slot with its play count and status, at <c>Information</c>.</summary>
    public static void LogSamples(ILogger logger, ModModule module, SampleUsage usage, IReadOnlySet<int> ignoredSamples) =>
        WriteLines(logger, SampleLines(module, usage, ignoredSamples));

    /// <summary>The lines of section 1, without the level prefix.</summary>
    public static IReadOnlyList<string> MetadataLines(ModModule module, string fileName)
    {
        InitialTempo tempo = FindInitialTempo(module);
        return
        [
            Invariant($"File        : {fileName} ({module.FileSize} bytes)"),
            Invariant($"Title       : {Quoted(module.Title)}"),
            Invariant($"Format      : {FormatName(module)} — {module.ChannelCount} channels, {module.Samples.Count} sample slots"),
            Invariant($"Song length : {module.SongLength} {(module.SongLength == 1 ? "order" : "orders")}, {RestartText(module)}"),
            Invariant($"Patterns    : {module.Patterns.Count}"),
            Invariant($"Tempo       : speed {tempo.Speed}, {tempo.Bpm:0.##} BPM (initial)"),
        ];
    }

    /// <summary>The lines of section 2 (a header, then one line per slot, empty slots included), without the level prefix.</summary>
    private static IReadOnlyList<string> SampleLines(ModModule module, SampleUsage usage, IReadOnlySet<int> ignoredSamples)
    {
        List<string> lines = [SampleRow("#", "Name", "Length", "Loop start+len", "Vol", "Fine", "Notes", "Status")];
        foreach (ModSample sample in module.Samples)
        {
            SampleUse use = usage.Samples[sample.Number - 1];
            lines.Add(SampleRow(
                sample.Number.ToString("D2", CultureInfo.InvariantCulture),
                Quoted(sample.Name),
                Number(sample.Length),
                sample.Loops ? Invariant($"{sample.LoopStart}+{sample.LoopLength}") : NoLoop,
                Number(sample.Volume),
                Number(sample.FineTune),
                Number(use.Notes),
                ignoredSamples.Contains(sample.Number) ? "ignored" : StatusText(use.Status)));
        }

        return lines;
    }

    /// <summary>
    /// The speed and BPM set by <c>Fxx</c> on the first row the song plays, or <see cref="DefaultTempo"/> where that row
    /// sets none. Channels are read left to right, so the rightmost command of each kind wins, as in ProTracker.
    /// Under Ultimate Soundtracker rules, which have no <c>Fxx</c>, the speed is 6 and the BPM comes from byte 471
    /// (<see cref="ModPlayers.UltimateSoundtrackerBpm"/>). Later changes are the song model's job (plan M3).
    /// </summary>
    public static InitialTempo FindInitialTempo(ModModule module)
    {
        InitialTempo tempo = DefaultTempo;
        if (module.Player == ModPlayer.UltimateSoundtracker)
        {
            return tempo with { Bpm = ModPlayers.UltimateSoundtrackerBpm(module.RestartPosition) ?? DefaultTempo.Bpm };
        }

        int firstPattern = module.OrderTable[0];
        if (firstPattern >= module.Patterns.Count)
        {
            return tempo;
        }

        ModPattern pattern = module.Patterns[firstPattern];
        for (int channel = 0; channel < pattern.ChannelCount; channel++)
        {
            ModCell cell = pattern[0, channel];
            if (cell.Effect != SetSpeed || cell.EffectParameter == 0)
            {
                continue;
            }

            tempo = cell.EffectParameter < FirstTempoValue
                ? tempo with { Speed = cell.EffectParameter }
                : tempo with { Bpm = cell.EffectParameter };
        }

        return tempo;
    }

    /// <summary>
    /// Names are printed in quotes so leading and trailing spaces show; control characters become <c>·</c>.
    /// </summary>
    public static string Quoted(string text) => "\"" + string.Concat(text.Select(c => char.IsControl(c) ? '·' : c)) + "\"";

    private static void WriteLines(ILogger logger, IEnumerable<string> lines)
    {
        // The line is a property, not the template: names may hold '{' and '}', which a template would read as holes.
        foreach (string line in lines)
        {
            logger.Information("{ReportLine:l}", line);
        }
    }

    private static string SampleRow(string number, string name, string length, string loop, string volume, string fineTune, string notes, string status) =>
        Invariant($" {number,-2}  {name,-NameColumnWidth} {length,6}  {loop,-14}  {volume,3}  {fineTune,4}  {notes,5}  {status}");

    private static string FormatName(ModModule module) => module.Format switch
    {
        ModFormat.Soundtracker15 => "Soundtracker (no tag)",
        _ => module.Tag switch
        {
            "M.K." or "M!K!" => $"ProTracker {module.Tag}",
            "FLT4" => $"StarTrekker {module.Tag}",
            "4CHN" => $"FastTracker {module.Tag}",
            _ => $"tag {module.Tag}",
        },
    };

    // A 15-sample file's byte 471 is ambiguous (docs/formats/mod.md#variant-detection); M3 decides which reading applies.
    private static string RestartText(ModModule module) => module.Format == ModFormat.Soundtracker15
        ? Invariant($"restart position {module.RestartPosition} (Ultimate Soundtracker reads this byte as the tempo)")
        : Invariant($"restart position {module.RestartPosition}");

    private static string StatusText(SampleStatus status) => status switch
    {
        SampleStatus.Used => "used",
        SampleStatus.Unused => "unused",
        _ => "empty",
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
