using System.Globalization;
using Mod2Xgm.Model.Window;
using Serilog;

namespace Mod2Xgm.Output;

/// <summary>
/// The report line for the part of the song converted (<c>--start</c>, <c>--end</c>, <c>--loop-from</c>, <c>--fade-out</c>;
/// docs/cli-output.md#window): where it starts and ends in the source, how it ends and how long it is. It carries a
/// <c>window</c> <see cref="ReportRow"/>.
/// </summary>
internal static class WindowReport
{
    /// <summary>Logs <paramref name="window"/> at <c>Information</c>.</summary>
    public static void Log(ILogger logger, WindowSummary window)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(window);
        ReportRow.Write(logger, Line(window), Row(window));
    }

    /// <summary>
    /// <c>Window: 0:30.000 (order 2 row 0) to 2:05.040 (order 11 row 25), 1:35.040 long, stops</c>, then the loop or fade.
    /// </summary>
    internal static string Line(WindowSummary window)
    {
        ArgumentNullException.ThrowIfNull(window);
        string end = window.End is { } e ? Point(e) : SongTime.Format(window.EndSeconds) + " (the song's end)";
        string ending = window.Loop is { } loop
            ? string.Create(CultureInfo.InvariantCulture, $"loops to {Point(loop)}, {SongTime.Format(window.LoopSeconds ?? 0)} into the window")
            : "stops";
        string fade = window.FadeSeconds is { } seconds ? ", fading out over its last " + SongTime.Format(seconds) : string.Empty;
        return $"Window: {Point(window.Start)} to {end}, {SongTime.Format(window.Seconds)} long, {ending}{fade}";
    }

    private static ReportRow Row(WindowSummary window) => new(
        "window",
        ("start_order", window.Start.Order),
        ("start_row", window.Start.Row),
        ("start_s", Round(window.Start.Seconds)),
        ("end_order", window.End?.Order),
        ("end_row", window.End?.Row),
        ("end_s", Round(window.EndSeconds)),
        ("end", window.Loop is null ? "stops" : "loops"),
        ("loop_order", window.Loop?.Order),
        ("loop_row", window.Loop?.Row),
        ("loop_s", window.LoopSeconds is { } loop ? Round(loop) : null),
        ("fade_s", window.FadeSeconds is { } fade ? Round(fade) : null),
        ("seconds", Round(window.Seconds)));

    private static string Point(SourcePoint point) =>
        string.Create(CultureInfo.InvariantCulture, $"{SongTime.Format(point.Seconds)} (order {point.Order} row {point.Row})");

    private static double Round(double seconds) => Math.Round(seconds, 3);
}
