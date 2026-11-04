using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

/// <summary>Tests for <see cref="ConversionNotes"/>: every lossy decision is kept and logged as a Warning.</summary>
public sealed class ConversionNotesTests
{
    /// <summary>A note is logged once, at Warning level, with its message template unchanged.</summary>
    [Fact]
    public void Add_WithTemplateAndValues_LogsOneWarning()
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes sut = new(sink.CreateLogger());

        // Act
        sut.Add("Sample {Sample} truncated to {Length} bytes", "snare", 1024);

        // Assert
        LogEvent logged = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Warning, logged.Level);
        Assert.Equal("Sample {Sample} truncated to {Length} bytes", logged.MessageTemplate.Text);
    }

    /// <summary>Notes are kept in the order they were added, rendered with text values unquoted.</summary>
    [Fact]
    public void Add_WithTwoNotes_KeepsThemInOrderWithTextUnquoted()
    {
        // Arrange
        ConversionNotes sut = new(new CollectingSink().CreateLogger());
        string[] expected = ["Sample snare truncated to 1024 bytes", "Effect 7xy dropped on channel 2"];

        // Act
        sut.Add("Sample {Sample} truncated to {Length} bytes", "snare", 1024);
        sut.Add("Effect {Effect} dropped on channel {Channel}", "7xy", 2);

        // Assert
        Assert.Equal(2, sut.Count);
        Assert.Equal(expected, sut.Messages);
    }

    /// <summary>A logger that writes nowhere still leaves the rendered note in the collection.</summary>
    [Fact]
    public void Add_WithSilentLogger_StillRendersNote()
    {
        // Arrange
        ConversionNotes sut = new(Logger.None);

        // Act
        sut.Add("Sample {Sample} skipped", "vocal");

        // Assert
        Assert.Equal(1, sut.Count);
        Assert.Equal("Sample vocal skipped", Assert.Single(sut.Messages));
    }

    /// <summary>A template Serilog can't parse is kept as written instead of throwing.</summary>
    [Fact]
    public void Add_WithMalformedTemplate_KeepsTemplateAsWritten()
    {
        // Arrange
        ConversionNotes sut = new(Logger.None);

        // Act
        sut.Add("Unclosed {Sample", "vocal");

        // Assert
        Assert.Equal("Unclosed {Sample", Assert.Single(sut.Messages));
    }
}
