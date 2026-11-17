using System.Globalization;

namespace Mod2Xgm.Model.Window;

/// <summary>
/// A place in the source song, as <c>--start</c>, <c>--end</c> and <c>--loop-from</c> give it (docs/cli.md#converting-part-of-a-song):
/// a time from the song's start, or a tracker position. Either one snaps to the nearest row start
/// (docs/adr/0116-song-window.md).
/// </summary>
internal abstract record SongPosition
{
    /// <summary>The position as the user would write it, for conversion notes and errors.</summary>
    public abstract string Display { get; }
}

/// <summary>A time from the start of the song, on the source's own timeline (<c>--input-region</c>'s tick rate).</summary>
/// <param name="Seconds">Seconds from the start, 0 or more.</param>
internal sealed record TimePosition(double Seconds) : SongPosition
{
    /// <inheritdoc/>
    public override string Display => SongTime.Format(Seconds);
}

/// <summary>A tracker position: the first time the song plays this order's row (0-based, as conversion notes print them).</summary>
/// <param name="Order">Index into <see cref="Song.Orders"/>.</param>
/// <param name="Row">Row within the order's pattern.</param>
internal sealed record TrackerPosition(int Order, int Row) : SongPosition
{
    /// <inheritdoc/>
    public override string Display => string.Create(CultureInfo.InvariantCulture, $"o{Order}r{Row}");
}

/// <summary>What part of the song to convert (docs/adr/0116-song-window.md); every part is optional.</summary>
/// <param name="Start">Where the window starts, or <c>null</c> for the song's start.</param>
/// <param name="End">Where the window ends (that row isn't played), or <c>null</c> for where the song ends or would repeat.</param>
/// <param name="LoopFrom">Where the window loops back to after its end, or <c>null</c>: then it stops at <paramref name="End"/>, or keeps the song's own ending without one.</param>
/// <param name="FadeOutSeconds">How long the fade to silence before the end lasts, or <c>null</c> for none.</param>
internal sealed record SongWindowRequest(SongPosition? Start, SongPosition? End, SongPosition? LoopFrom, double? FadeOutSeconds);

/// <summary>Formats song times for notes and the report.</summary>
internal static class SongTime
{
    /// <summary><c>m:ss.fff</c> (or <c>h:mm:ss.fff</c> from an hour up), rounded to the millisecond.</summary>
    public static string Format(double seconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Round(Math.Max(seconds, 0) * 1000));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}

/// <summary>A window that can't be cut from this song (a position it never reaches, an empty window); a usage error.</summary>
/// <param name="message">Why, naming the option at fault.</param>
internal sealed class WindowException(string message) : Exception(message);
