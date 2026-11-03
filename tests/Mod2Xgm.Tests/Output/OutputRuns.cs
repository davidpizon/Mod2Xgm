using System.Text;
using System.Text.RegularExpressions;
using Mod2Xgm.Cli;
using Mod2Xgm.Output;
using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Output;

/// <summary>
/// Whole runs of the command line in one <c>--progress</c> mode, for the golden output tests (tests/golden/output/):
/// a song made in code, written to a temporary folder, converted with <see cref="Program.Run(string[], Func{string[], ExitCode}, TextWriter, TextWriter, TerminalProbe)"/>.
/// Times and the temporary folder are replaced by fixed text, so the files don't change from run to run.
/// </summary>
internal static partial class OutputRuns
{
    private const int Period = 428; // C-2 (docs/formats/mod.md#pitch)
    private const int SetSpeed = 0xF;

    /// <summary>A song with every kind of sample slot, as in the report's golden test.</summary>
    public static byte[] Song() => new ModBuilder { Title = "space {debris}" }
        .WithSample(1, "bassdrum", 4532)
        .WithSample(2, "snare [2]", 3870, fineTune: -2, volume: 48)
        .WithSample(3, "  by Jester", 0, volume: 0)
        .WithSample(4, "strings", 8000, loopStart: 2000, loopLength: 6000)
        .WithSample(5, "never played", 100)
        .WithSample(7, "vocal", 2104, fineTune: 7)
        .WithSample(8, string.Empty, 64, rawName: Encoding.Latin1.GetBytes("bad\u0007name"))
        .WithOrders(0, 1)
        .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 4)
        .WithCell(0, row: 0, channel: 1, sample: 2, period: Period, effect: SetSpeed, parameter: 0x8C)
        .WithCell(0, row: 0, channel: 2, sample: 4, period: Period)
        .WithCell(0, row: 0, channel: 3, sample: 7, period: Period)
        .WithCell(1, row: 0, channel: 0, sample: 1, period: Period)
        .WithCell(1, row: 0, channel: 1, sample: 2, period: Period)
        .WithCell(1, row: 16, channel: 0, sample: 1, period: 0, effect: 0x7, parameter: 0x44)
        .Build();

    /// <summary>
    /// Converts <see cref="Song"/> with <paramref name="args"/> and <c>--progress <paramref name="mode"/></c> (and
    /// <c>--no-cache</c>, so no timing history is read); the exit code and both streams, made stable.
    /// </summary>
    public static (int Code, string Output, string Error) Convert(string mode, params string[] args)
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod", Song());
        using StringWriter output = new();
        using StringWriter error = new();
        int code = Program.Run(
            [input, "--progress", mode, "--no-cache", .. args],
            static a => CommandLineDefinition.Invoke(a, Program.ConvertMod, TextWriter.Null, TextWriter.Null),
            output,
            error,
            TerminalProbe.From(true, true, null, _ => null));
        return (code, Stable(output.ToString(), temp.Path), Stable(error.ToString(), temp.Path));
    }

    /// <summary><paramref name="text"/> with the temporary folder as <c>TEMP</c>, the version as <c>VERSION</c> and every time as <c>#</c> (<c>0</c> in JSON, so it still parses).</summary>
    public static string Stable(string text, string folder)
    {
        string escaped = folder.Replace("\\", "\\\\", StringComparison.Ordinal);
        text = text.Replace(escaped, "TEMP", StringComparison.Ordinal).Replace(folder, "TEMP", StringComparison.Ordinal);
        text = text.Replace("TEMP\\\\", "TEMP/", StringComparison.Ordinal).Replace("TEMP\\", "TEMP/", StringComparison.Ordinal);
        text = text.Replace("mod2xgm=" + RunInfo.Version, "mod2xgm=VERSION", StringComparison.Ordinal)
            .Replace("\"version\":\"" + RunInfo.Version + "\"", "\"version\":\"VERSION\"", StringComparison.Ordinal);
        text = Seconds().Replace(text, "#s");
        text = JsonSeconds().Replace(text, "\"${key}\":0");
        return text;
    }

    /// <summary>A time in the text and fancy modes: <c>4.0s</c>, <c>12s+</c>, <c>0.1 s</c>.</summary>
    [GeneratedRegex(@"\d+(\.\d+)? ?s\+?(?![a-z])")]
    private static partial Regex Seconds();

    /// <summary>A time in json: <c>"elapsed_s":0.123</c>, <c>"seconds":4</c>.</summary>
    [GeneratedRegex(@"""(?<key>[a-z_]*(seconds|_s))"":(\d+(\.\d+)?|null)")]
    private static partial Regex JsonSeconds();
}
