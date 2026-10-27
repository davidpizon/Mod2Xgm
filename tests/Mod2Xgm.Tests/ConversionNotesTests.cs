using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

public sealed class ConversionNotesTests
{
    [Fact]
    public void EachNoteIsLoggedAsAWarning()
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());

        notes.Add("Sample {Sample} truncated to {Length} bytes", "snare", 1024);

        LogEvent logged = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Warning, logged.Level);
        Assert.Equal("Sample {Sample} truncated to {Length} bytes", logged.MessageTemplate.Text);
    }

    [Fact]
    public void NotesAreKeptInOrderWithTextValuesUnquoted()
    {
        ConversionNotes notes = new(new CollectingSink().CreateLogger());

        notes.Add("Sample {Sample} truncated to {Length} bytes", "snare", 1024);
        notes.Add("Effect {Effect} dropped on channel {Channel}", "7xy", 2);

        Assert.Equal(2, notes.Count);
        Assert.Equal(
            new[] { "Sample snare truncated to 1024 bytes", "Effect 7xy dropped on channel 2" },
            notes.Messages);
    }

    [Fact]
    public void ASilentLoggerStillRendersTheNote()
    {
        ConversionNotes notes = new(Logger.None);

        notes.Add("Sample {Sample} skipped", "vocal");

        Assert.Equal(1, notes.Count);
        Assert.Equal("Sample vocal skipped", Assert.Single(notes.Messages));
    }

    [Fact]
    public void AMalformedTemplateIsKeptAsWritten()
    {
        ConversionNotes notes = new(Logger.None);

        notes.Add("Unclosed {Sample", "vocal");

        Assert.Equal("Unclosed {Sample", Assert.Single(notes.Messages));
    }
}
