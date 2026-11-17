using Mod2Xgm.Model.Window;
using Mod2Xgm.Output;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests for <see cref="WindowReport"/>: the report line for the part of the song converted.</summary>
public sealed class WindowReportTests
{
    /// <summary>A window that stops says where it starts and ends in the source, how long it is and that it stops.</summary>
    [Fact]
    public void Line_WithStoppingWindow_SaysStartEndLengthAndStop()
    {
        // Arrange
        WindowSummary window = new(new SourcePoint(2, 0, 30), new SourcePoint(11, 25, 125.044), 125.044, null, null, null, 95.044, (1500, 6252, -1));

        // Act
        string line = WindowReport.Line(window);

        // Assert
        Assert.Equal("Window: 0:30.000 (order 2 row 0) to 2:05.044 (order 11 row 25), 1:35.044 long, stops", line);
    }

    /// <summary>A window that runs to the song's end and loops says so, with where the loop starts and any fade.</summary>
    [Fact]
    public void Line_WithLoopToSongEnd_SaysLoop()
    {
        // Arrange
        WindowSummary window = new(new SourcePoint(0, 0, 0), null, 145.92, new SourcePoint(2, 0, 7.68), 7.68, null, 145.92, (0, 7296, 384));

        // Act
        string line = WindowReport.Line(window);

        // Assert
        Assert.Equal("Window: 0:00.000 (order 0 row 0) to 2:25.920 (the song's end), 2:25.920 long, loops to 0:07.680 (order 2 row 0), 0:07.680 into the window", line);
    }

    /// <summary>A fade-out is added to the line.</summary>
    [Fact]
    public void Line_WithFade_SaysFade()
    {
        // Arrange
        WindowSummary window = new(new SourcePoint(0, 0, 0), new SourcePoint(1, 0, 7.68), 7.68, null, null, 2, 7.68, (0, 384, -1));

        // Act
        string line = WindowReport.Line(window);

        // Assert
        Assert.EndsWith("stops, fading out over its last 0:02.000", line, StringComparison.Ordinal);
    }
}
