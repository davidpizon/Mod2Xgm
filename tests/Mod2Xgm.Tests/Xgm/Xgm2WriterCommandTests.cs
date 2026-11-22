using System.Buffers.Binary;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Xgm;
using Serilog.Events;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>
/// Tests for the XGM2 writer's FM commands of docs/adr/0139-xgm2-frame-budget.md: writes the chip already holds aren't
/// sent, the compact commands, patch writes moved to silent frames and the busy-frame note.
/// </summary>
public sealed class Xgm2WriterCommandTests
{
    private const double NtscRate = 13_317;
    private const int DataOffset = 0x104;
    private const int Block = 256;

    /// <summary>A register written again with the value the chip holds isn't sent; the wait covers the empty frame.</summary>
    [Fact]
    public void Write_WithSameValueAgain_DoesNotSendIt()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0x30, 0x71)], [Fm(0, 0x30, 0x71)], [Fm(0, 0x31, 0x05)]);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, Notes().Notes);
        byte[] sut = FmBytes(output);

        // Assert
        Assert.Equal([0xe0, 0x30, 0x71, 0x01, 0xe0, 0x31, 0x05], sut[..7]);
        Assert.Equal(1, output.Stats.Dropped);
    }

    /// <summary>
    /// A high byte then its low byte is one <c>$3x</c> (here <c>$8x</c>, ending the frame); once the driver stores the
    /// frequency, a small step is a <c>$Ax</c> delta, the unchanged high byte left out.
    /// </summary>
    [Fact]
    public void Write_WithFrequencySteps_UsesSetThenDelta()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0xa4, 0x22), Fm(0, 0xa0, 0x69)], [Fm(0, 0xa4, 0x22), Fm(0, 0xa0, 0x70)], [], [Fm(0, 0xa0, 0x75)], []);

        // Act
        byte[] sut = FmBytes(Xgm2Writer.Write(stream, Notes().Notes));

        // Assert
        Assert.Equal([0x80, 0x22, 0x69, 0xa0, 0x0c, 0x01, 0xa0, 0x08], sut[..8]);
    }

    /// <summary>A high byte written with no low byte after it still reaches the chip's latch, as a raw write.</summary>
    [Fact]
    public void Write_WithHighByteAlone_SendsItRaw()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0xa4, 0x22)], []);

        // Act
        byte[] sut = FmBytes(Xgm2Writer.Write(stream, Notes().Notes));

        // Assert
        Assert.Equal([0xe0, 0xa4, 0x22], sut[..3]);
    }

    /// <summary>Keys: all four operators is <c>$4x</c>, an off-then-on pair is <c>$5x</c>, any other key is <c>$F8</c>.</summary>
    [Fact]
    public void Write_WithKeys_UsesCompactKeyCommands()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0x28, 0xf0)], [Fm(0, 0x28, 0x00), Fm(0, 0x28, 0xf0)], [Fm(0, 0x28, 0x10)], []);

        // Act
        byte[] sut = FmBytes(Xgm2Writer.Write(stream, Notes().Notes));

        // Assert
        Assert.Equal([0x48, 0x00, 0x50, 0x00, 0xf8, 0x10], sut[..6]);
    }

    /// <summary>A frequency then a key on is one <c>$3x</c> with bit 15 set; a key off, a new frequency and a key on, one with bits 14 and 15.</summary>
    [Fact]
    public void Write_WithKeyOffFrequencyKeyOn_FoldsKeysIntoFrequencySet()
    {
        // Arrange
        GenesisStream stream = Stream(
            [Fm(0, 0xa4, 0x22), Fm(0, 0xa0, 0x69), Fm(0, 0x28, 0xf0)],
            [Fm(0, 0x28, 0x00), Fm(0, 0xa4, 0x23), Fm(0, 0xa0, 0x10), Fm(0, 0x28, 0xf0)],
            [],
            []);

        // Act
        byte[] sut = FmBytes(Xgm2Writer.Write(stream, Notes().Notes));

        // Assert
        Assert.Equal([0x80, 0xa2, 0x69, 0x30, 0xe3, 0x10], sut[..6]);
    }

    /// <summary>A <c>$B4</c> change of the pan bits alone is the one-byte <c>$6x</c>.</summary>
    [Fact]
    public void Write_WithPanOnlyChange_UsesPanCommand()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0xb4, 0xc0)], [], [Fm(0, 0xb4, 0x40)], [], []);

        // Act
        byte[] sut = FmBytes(Xgm2Writer.Write(stream, Notes().Notes));

        // Assert
        Assert.Equal([0xe0, 0xb4, 0xc0, 0x01, 0x64], sut[..5]);
    }

    /// <summary>A small level step that ends its frame, with the next frame due, is a <c>$Dx</c> delta that ends the frame.</summary>
    [Fact]
    public void Write_WithLevelStepAtFrameEnd_UsesLevelDeltaThatEndsTheFrame()
    {
        // Arrange
        GenesisStream stream = Stream([Fm(0, 0x40, 0x10)], [Fm(0, 0x40, 0x12)], [Fm(0, 0x40, 0x11)]);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, Notes().Notes);
        byte[] sut = FmBytes(output);

        // Assert
        Assert.Equal([0x90, 0x20, 0x00, 0xd0, 0x04, 0xd0, 0x02], sut[..7]);
        Assert.True(output.Stats.Compact >= 2);
    }

    /// <summary>A whole patch again with one register changed sends that register alone, not a 31-byte load instrument.</summary>
    [Fact]
    public void Write_WithPatchAgainOneChange_SendsOnlyTheChange()
    {
        // Arrange
        ChipEvent[] patch = [.. Patch(0x05)];
        ChipEvent[] again = [.. Patch(0x05).Select(e => e.Register == 0x30 ? Fm(0, 0x30, 0x07) : e)];
        GenesisStream stream = Stream(patch, [], again, []);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, Notes().Notes);
        byte[] sut = FmBytes(output);

        // Assert
        Assert.Equal(0x20, sut[0]);
        Assert.Equal([0x01, 0xe0, 0x30, 0x07], sut[31..35]);
        Assert.Equal(1, output.Stats.LoadInstruments);
    }

    /// <summary>A frame at 80 % of the budget or more that doesn't split gets a note with where it is.</summary>
    [Fact]
    public void Write_WithBusyFrame_NotesIt()
    {
        // Arrange
        int[] registers = [.. Enumerable.Range(0x30, 0x10), .. Enumerable.Range(0x50, 0x50)];
        GenesisStream stream = Stream([.. registers.Select(r => Fm(0, (byte)r, 1))], []) with { Positions = [(3, 7), (3, 8)] };
        (ConversionNotes notes, CollectingSink sink) = Notes();

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, notes);

        // Assert
        Assert.Equal(1, output.Stats.BusyFrames);
        Assert.Equal(0, output.Stats.Splits);
        string note = Assert.Single(sink.Messages(LogEventLevel.Warning), m => m.Contains("80%", StringComparison.Ordinal));
        Assert.Contains("frame 0 (0.00 s, order 3 row 7) 205 bytes, 96 register writes on FM1, FM2, FM3", note, StringComparison.Ordinal);
    }

    /// <summary>Patch writes of a channel that is still sounding stay in their frame, even when it splits.</summary>
    [Fact]
    public void Write_WithOverBudgetFrameOnSoundingChannels_MovesNothing()
    {
        // Arrange
        List<ChipEvent>[] frames = [.. Enumerable.Range(0, 30).Select(_ => new List<ChipEvent>())];
        frames[0].AddRange(Enumerable.Range(0, 6).Select(c => Fm(0, 0x28, (byte)(0xf0 | (c / 3 * 4) + (c % 3)))));
        frames[20].AddRange(OverBudget());
        GenesisStream stream = new([.. frames], null, false, NtscRate);

        // Act
        Xgm2Output sut = Xgm2Writer.Write(stream, Notes().Notes);

        // Assert
        Assert.Equal(0, sut.Stats.Moved);
        Assert.True(sut.Stats.Stuck > 0);
        Assert.Equal(1, sut.Stats.Splits);
    }

    /// <summary>Patch writes in a looping song's loop frame can't move before it, where the second time through wouldn't play them.</summary>
    [Fact]
    public void Write_WithOverBudgetLoopFrame_MovesNothingBeforeTheLoop()
    {
        // Arrange
        List<ChipEvent>[] frames = [.. Enumerable.Range(0, 30).Select(_ => new List<ChipEvent>())];
        frames[20].AddRange(OverBudget());
        GenesisStream stream = new([.. frames], 20, false, NtscRate);

        // Act
        Xgm2Output sut = Xgm2Writer.Write(stream, Notes().Notes);

        // Assert
        Assert.Equal(0, sut.Stats.Moved);
    }

    /// <summary>The YM3438 model calls a channel silent before it sounds and not while it is keyed on.</summary>
    [Fact]
    public void ChannelSilent_BeforeAndAfterKeyOn_FollowsTheKey()
    {
        // Arrange
        Ym3438 sut = new();
        bool before = sut.ChannelSilent(0);

        // Act
        sut.WriteRegister(0, 0x28, 0xf0);

        // Assert
        Assert.True(before);
        Assert.False(sut.ChannelSilent(0));
        Assert.True(sut.ChannelSilent(1));
    }

    /// <summary>Six channels' partial patches and key ons: more than a frame's budget of raw writes.</summary>
    private static IEnumerable<ChipEvent> OverBudget()
    {
        for (int c = 0; c < 6; c++)
        {
            int part = c / 3;
            int index = c % 3;
            for (int row = 0x30; row < 0x90; row += 0x10)
            {
                for (int slot = 0; slot < 16; slot += 4)
                {
                    yield return Fm(part, (byte)(row + slot + index), (byte)(row == 0x40 ? 0x7f : 0x11 + c));
                }
            }

            yield return Fm(0, 0x28, (byte)(0xf0 | (part * 4) + index));
        }
    }

    /// <summary>A whole patch for channel 0 of part 0, every value <paramref name="value"/> but the levels.</summary>
    private static IEnumerable<ChipEvent> Patch(byte value)
    {
        for (int row = 0x30; row <= 0x90; row += 0x10)
        {
            for (int slot = 0; slot < 16; slot += 4)
            {
                yield return Fm(0, (byte)(row + slot), row == 0x40 ? (byte)0x20 : value);
            }
        }

        yield return Fm(0, 0xb0, 0x32);
        yield return Fm(0, 0xb4, 0xc0);
    }

    private static ChipEvent Fm(int part, byte register, byte value) => ChipEvent.Fm(part, register, value);

    /// <summary>A song of these frames, no loop, NTSC.</summary>
    private static GenesisStream Stream(params ChipEvent[][] frames) => new([.. frames.Select(f => (IReadOnlyList<ChipEvent>)f)], null, false, NtscRate);

    /// <summary>The FM stream of a written file.</summary>
    private static byte[] FmBytes(Xgm2Output output)
    {
        int slen = BinaryPrimitives.ReadUInt16LittleEndian(output.Bytes.AsSpan(6)) * Block;
        return output.Bytes[(DataOffset + slen)..(DataOffset + slen + output.FmBytes)];
    }

    /// <summary>A notes collector and the sink that sees the conversion notes it logs.</summary>
    private static (ConversionNotes Notes, CollectingSink Sink) Notes()
    {
        CollectingSink sink = new();
        return (new ConversionNotes(sink.CreateLogger()), sink);
    }
}
