using System.Buffers.Binary;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Xgm;
using Serilog.Core;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>
/// Byte-exact tests of <see cref="Xgm2Writer"/> (plan T10.1) on streams built by hand: the header, the SID table, the
/// sample block, both streams' commands and waits, and the loop (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10).
/// </summary>
public sealed class Xgm2WriterTests
{
    private const double NtscRate = 13_317;
    private const int DataOffset = 0x104;
    private const int Block = 256;

    /// <summary>The <c>$9x</c> commands that set all 24 slots silent (level 127), part 0's then part 1's, as a loop frame starts.</summary>
    private static readonly byte[] SilentSlots = [.. new[] { 0, 1 }.SelectMany(part => new[] { 0x0, 0x1, 0x2, 0x4, 0x5, 0x6, 0x8, 0x9, 0xa, 0xc, 0xd, 0xe }
        .SelectMany(r => new[] { (byte)(0x90 | r), (byte)(0xfe | part) }))];

    /// <summary>An empty one-frame song is the header, an all-empty SID table, and streams that wait, go quiet and end.</summary>
    [Fact]
    public void Write_WithEmptySong_WritesHeaderAndSilentEnd()
    {
        // Arrange
        GenesisStream stream = new([[]], null, Pal: true, NtscRate);

        // Act
        byte[] sut = Xgm2Writer.Write(stream, Notes()).Bytes;

        // Assert
        byte[] expected = new byte[DataOffset + (2 * Block)];
        "XGM2"u8.CopyTo(expected);
        expected[4] = 0x10;
        expected[5] = 0x01;
        expected[8] = 1;
        expected[10] = 1;
        expected.AsSpan(0x0c, 248).Fill(0xff);
        byte[] fm = [0x00, 0xff, 0xff, 0xff, 0xff];
        byte[] psg = [0x00, 0x8f, 0x9f, 0xaf, 0xbf, 0x0f, 0xff, 0xff, 0xff];
        fm.CopyTo(expected, DataOffset);
        psg.CopyTo(expected, DataOffset + Block);
        Assert.Equal(expected, sut);
    }

    /// <summary>
    /// A three-frame song that loops to frame 1, spelled out byte by byte: the loop frame sets the chips back as they were,
    /// every key, slot level and PSG volume included (up, silent and off where nothing was set yet), and the loop command
    /// ends the last frame.
    /// </summary>
    [Fact]
    public void Write_WithLoopingSong_WritesEveryByte()
    {
        // Arrange
        sbyte[] kick = [1, -1, 127];
        GenesisStream stream = new(
            [
                [
                    ChipEvent.Fm(0, 0xb4, 0xc0), ChipEvent.Fm(0, 0x28, 0xf0),
                    ChipEvent.Psg(0x92), ChipEvent.Psg(0x85), ChipEvent.Psg(0x01),
                    ChipEvent.Play(0, kick, false, 0, "1 \"kick\""),
                ],
                [ChipEvent.Play(0, kick, false, 0, "1 \"kick\""), ChipEvent.Fm(1, 0x30, 0x71)],
                [ChipEvent.Stop(0)],
            ],
            LoopFrame: 1,
            Pal: false,
            NtscRate);

        // Act
        byte[] sut = Xgm2Writer.Write(stream, Notes()).Bytes;

        // Assert
        byte[] expected = new byte[DataOffset + (3 * Block)];
        "XGM2"u8.CopyTo(expected);
        expected[4] = 0x10;
        expected[6] = 1;
        expected[8] = 1;
        expected[10] = 1;
        expected.AsSpan(0x0c, 248).Fill(0xff);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(0x0c), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(0x0e), 1);
        byte[] sdat = [0x81, 0x7f, 0xff];
        byte[] fm =
        [
            0xe0, 0xb4, 0xc0, 0x48, 0x10, 0x01, 0x00,
            .. SilentSlots,
            0xe0, 0xb4, 0xc0, 0x48, 0x41, 0x42, 0x44, 0x45, 0x46, 0x10, 0x01, 0xe8, 0x30, 0x71, 0x00,
            0x10, 0x00,
            0xff, 0x07, 0x00, 0x00,
        ];
        byte[] psg = [0x20, 0x15, 0x82, 0x00, 0x20, 0x15, 0x82, 0x9f, 0xaf, 0xbf, 0x00, 0x0f, 0x04, 0x00, 0x00];
        expected.AsSpan(DataOffset, Block).Fill(0x80);
        sdat.CopyTo(expected, DataOffset);
        fm.CopyTo(expected, DataOffset + Block);
        psg.CopyTo(expected, DataOffset + (2 * Block));
        Assert.Equal(expected, sut);
    }

    /// <summary>The FM stream waits up to 15 frames in one byte, up to 271 in two, and longer in several.</summary>
    [Theory]
    [InlineData(1, new byte[] { 0x00 })]
    [InlineData(15, new byte[] { 0x0e })]
    [InlineData(16, new byte[] { 0x0f, 0x00 })]
    [InlineData(271, new byte[] { 0x0f, 0xff })]
    [InlineData(272, new byte[] { 0x0f, 0xff, 0x00 })]
    [InlineData(299, new byte[] { 0x0f, 0xff, 0x0f, 0x0c })]
    public void Write_WithFmGap_WritesItsWait(int gap, byte[] wait)
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = Frames(gap + 1);
        frames[0] = [ChipEvent.Fm(0, 0x28, 0x00)];
        frames[gap] = [ChipEvent.Fm(0, 0x28, 0x00)];

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(new GenesisStream(frames, null, false, NtscRate), Notes()));

        // Assert
        Assert.Equal([0x40, .. wait, 0x40], sut[..(wait.Length + 2)]);
    }

    /// <summary>The PSG stream waits up to 14 frames in one byte, up to 270 in two, and longer in several.</summary>
    [Theory]
    [InlineData(1, new byte[] { 0x00 })]
    [InlineData(14, new byte[] { 0x0d })]
    [InlineData(15, new byte[] { 0x0e, 0x00 })]
    [InlineData(270, new byte[] { 0x0e, 0xff })]
    [InlineData(271, new byte[] { 0x0e, 0xff, 0x00 })]
    public void Write_WithPsgGap_WritesItsWait(int gap, byte[] wait)
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = Frames(gap + 1);
        frames[0] = [ChipEvent.Psg(0x90)];
        frames[gap] = [ChipEvent.Psg(0x91)];

        // Act
        byte[] sut = Psg(Xgm2Writer.Write(new GenesisStream(frames, null, false, NtscRate), Notes()));

        // Assert
        Assert.Equal([0x80, .. wait, 0x81], sut[..(wait.Length + 2)]);
    }

    /// <summary>Raw writes go 8 to a command, and a new command starts when the part changes.</summary>
    [Fact]
    public void Write_WithManyRegisterWrites_SplitsThemByEightAndByPart()
    {
        // Arrange
        List<ChipEvent> events = [.. Enumerable.Range(0, 9).Select(i => ChipEvent.Fm(0, (byte)(0x30 + i), (byte)i)), ChipEvent.Fm(1, 0x50, 0x7f)];
        GenesisStream stream = new([events], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        byte[] eight = [0xe7, .. Enumerable.Range(0, 8).SelectMany(i => new[] { (byte)(0x30 + i), (byte)i })];
        Assert.Equal([.. eight, 0xe0, 0x38, 0x08, 0xe8, 0x50, 0x7f], sut[..(eight.Length + 6)]);
    }

    /// <summary>
    /// A total level goes through <c>$9x</c> (slot and channel in x; level and part in the data byte), after the raw writes,
    /// so SGDK's driver keeps the level it adds the game's volume to.
    /// </summary>
    [Fact]
    public void Write_WithTotalLevelWrite_UsesSetTlCommand()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(1, 0x4e, 0x23), ChipEvent.Fm(0, 0x30, 0x71)]], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0xe0, 0x30, 0x71, 0x9e, 0x47, 0x00], sut[..6]);
    }

    /// <summary>
    /// A channel whose whole patch is written in one go gets one load-instrument command (<c>$2x</c>) with the values the
    /// writes leave, per register <c>$30</c>–<c>$90</c> its four slots, then <c>$B0</c> and <c>$B4</c>; other writes stay raw.
    /// </summary>
    [Fact]
    public void Write_WithWholePatch_UsesLoadInstrumentCommand()
    {
        // Arrange: part 1, channel 2; the first $30 write is overwritten.
        List<ChipEvent> events = [ChipEvent.Fm(1, 0x32, 0x55)];
        for (int r = 0; r < 7; r++)
        {
            for (int s = 0; s < 4; s++)
            {
                events.Add(ChipEvent.Fm(1, (byte)(0x32 + (r << 4) + (s << 2)), (byte)((r * 4) + s)));
            }
        }

        events.AddRange([ChipEvent.Fm(1, 0xb2, 0x3c), ChipEvent.Fm(1, 0xb6, 0xc0), ChipEvent.Fm(1, 0xa6, 0x22)]);
        GenesisStream stream = new([events], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0x26, .. Enumerable.Range(0, 28).Select(i => (byte)i), 0x3c, 0xc0, 0xe8, 0xa6, 0x22, 0x00], sut[..35]);
    }

    /// <summary>A register written twice before the next command is sent once, with its last value: here one <c>$8x</c> frequency set that ends the frame.</summary>
    [Fact]
    public void Write_WithRegisterWrittenTwice_SendsLastValue()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(0, 0xa4, 0x22), ChipEvent.Fm(0, 0xa0, 0x69), ChipEvent.Fm(0, 0xa4, 0x23), ChipEvent.Fm(0, 0xa0, 0x70)]], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0x80, 0x23, 0x70], sut[..3]);
    }

    /// <summary>
    /// A frame of more than 224 bytes, its ending wait included (room for xgm2tool's packer), is split with <c>$F0</c>, after which SGDK's driver is a
    /// frame behind: the next frame's commands follow with no wait, and the wait after them is a frame shorter, so FM stays
    /// in step with PSG.
    /// </summary>
    [Fact]
    public void Write_WithFrameOverMaxSize_SplitsItAndTakesTheFrameBack()
    {
        // Arrange: 192 raw writes, $30-$3F and $50-$9F on both parts (no total level, no whole patch): 24 commands of 17 bytes.
        ConversionNotes notes = Notes();
        IReadOnlyList<ChipEvent>[] frames = Frames(4);
        int[] registers = [.. Enumerable.Range(0x30, 0x10), .. Enumerable.Range(0x50, 0x50)];
        frames[0] = [.. registers.Select(r => ChipEvent.Fm(0, (byte)r, 1)), .. registers.Select(r => ChipEvent.Fm(1, (byte)r, 2))];
        frames[1] = [ChipEvent.Fm(0, 0x28, 0x00)];
        frames[3] = [ChipEvent.Fm(0, 0x28, 0x01)];

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(new GenesisStream(frames, null, false, NtscRate), notes));

        // Assert: 13 commands (221 bytes) fit with room for the frame's end within 224; a 14th (238) wouldn't.
        const int first = 13 * 17;
        const int rest = 11 * 17;
        Assert.Equal(0xf0, sut[first]);
        Assert.Equal([0x40, 0x01, 0x41], sut[(first + 1 + rest)..(first + 1 + rest + 3)]);
        Assert.Contains(notes.Messages, m => m.StartsWith("1 frame splits:", StringComparison.Ordinal));
    }

    /// <summary>Register <c>$27</c> becomes CH3 mode on or off: the driver owns its timer bits; <c>$22</c> becomes the LFO command.</summary>
    [Fact]
    public void Write_WithCh3AndLfoWrites_UsesTheirCommands()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(0, 0x27, 0x45), ChipEvent.Fm(0, 0x27, 0x05), ChipEvent.Fm(0, 0x22, 0x0b)]], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0xfa, 0xfb, 0xf9, 0x0b], sut[..4]);
    }

    /// <summary>The DAC registers are the driver's: a write to one is a bug in the player.</summary>
    [Theory]
    [InlineData(0x2a)]
    [InlineData(0x2b)]
    public void Write_WithDacRegisterWrite_Throws(byte register)
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(0, register, 0x80)]], null, false, NtscRate);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => Xgm2Writer.Write(stream, Notes()));
    }

    /// <summary>Every write to the noise register is sent, as each one restarts the noise; an unchanged volume isn't.</summary>
    [Fact]
    public void Write_WithRepeatedNoiseWrite_SendsItEachTime()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Psg(0xe4), ChipEvent.Psg(0xf3)], [ChipEvent.Psg(0xe4), ChipEvent.Psg(0xf3)]], null, false, NtscRate);

        // Act
        byte[] sut = Psg(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0x2c, 0x04, 0xb3, 0x00, 0x2c, 0x04, 0x00], sut[..7]);
    }

    /// <summary>
    /// While the shared F-number latch is unknown, frequency writes go raw in the recording's order; at the loop, the chip is
    /// forgotten and the channel's frequency is set back with one <c>$3x</c>, high byte first, after its levels.
    /// </summary>
    [Fact]
    public void Write_WithLoop_RestoresFrequencyHighByteBeforeLowByte()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(0, 0xa0, 0x69), ChipEvent.Fm(0, 0xa4, 0x22), ChipEvent.Fm(0, 0x40, 0x10)], []], 1, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        byte[] silent = [.. SilentSlots.Chunk(2).Where(c => c[0] != 0x90 || c[1] != 0xfe).SelectMany(c => c)];
        Assert.Equal([0x90, 0x20, 0xe1, 0xa0, 0x69, 0xa4, 0x22, 0x00, .. silent, 0x90, 0x20, 0x30, 0x22, 0x69], sut[..(13 + silent.Length)]);
    }

    /// <summary>
    /// A note keyed up at the song's end goes quiet at the loop: every slot whose level the first time through hadn't set
    /// yet is set silent before the loop's first writes, so a pan there can't bring its release back (docs/adr/0134-loop-silences-release-tails.md).
    /// </summary>
    [Fact]
    public void Write_WithNoteReleasingAtLoopToStart_SilencesItsSlotsBeforeTheFirstWrites()
    {
        // Arrange
        GenesisStream stream = new(
            [
                [ChipEvent.Fm(0, 0xb5, 0xc0)],
                [ChipEvent.Fm(0, 0x41, 0x10), ChipEvent.Fm(0, 0x28, 0xf1)],
                [ChipEvent.Fm(0, 0x28, 0x01)],
            ],
            LoopFrame: 0,
            Pal: false,
            NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([.. SilentSlots, 0x40, 0x41, 0x42, 0x44, 0x45, 0x46, 0xe0, 0xb5, 0xc0], sut[..(SilentSlots.Length + 9)]);
    }

    /// <summary>The same stored copy played twice gets one sample id; another copy gets the next.</summary>
    [Fact]
    public void Write_WithCopiesPlayedAgain_StoresEachOnce()
    {
        // Arrange
        sbyte[] a = new sbyte[300];
        sbyte[] b = new sbyte[10];
        GenesisStream stream = new([[ChipEvent.Play(0, a, false, 0, "1"), ChipEvent.Play(1, b, true, 1, "2"), ChipEvent.Play(2, a, false, 0, "1")]], null, false, NtscRate);

        // Act
        Xgm2Output sut = Xgm2Writer.Write(stream, Notes());

        // Assert
        Assert.Equal([0x10, 0x01, 0x15, 0x02, 0x12, 0x01], Fm(sut)[..6]);
        Assert.Equal([new Xgm2Sample(1, 0, "1", 512, false, 13_317), new Xgm2Sample(2, 1, "2", 256, true, 6_659)], sut.Samples);
        Assert.Equal([0x00, 0x00, 0x02, 0x00, 0x03, 0x00, 0xff, 0xff], sut.Bytes[0x0c..0x14]);
    }

    /// <summary>XGM2 holds 123 recordings; the 124th is a capacity failure (exit 6).</summary>
    [Fact]
    public void Write_WithTooManyRecordings_ThrowsDoesNotFit()
    {
        // Arrange
        GenesisStream stream = new([[.. Enumerable.Range(0, Xgm2Writer.MaxSamples + 1).Select(i => ChipEvent.Play(0, [(sbyte)i], false, 0, "x"))]], null, false, NtscRate);

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, error.Code);
    }

    /// <summary>With 123 recordings, the SID table's last entry is the end of the last one.</summary>
    [Fact]
    public void Write_WithMostRecordings_EndsTheTableWithTheirEnd()
    {
        // Arrange
        GenesisStream stream = new([[.. Enumerable.Range(0, Xgm2Writer.MaxSamples).Select(i => ChipEvent.Play(0, [(sbyte)i], false, 0, "x"))]], null, false, NtscRate);

        // Act
        byte[] sut = Xgm2Writer.Write(stream, Notes()).Bytes;

        // Assert
        Assert.Equal(Xgm2Writer.MaxSamples, BinaryPrimitives.ReadUInt16LittleEndian(sut.AsSpan(0x0c + (2 * Xgm2Writer.MaxSamples))));
        Assert.Equal(Xgm2Writer.MaxSamples, BinaryPrimitives.ReadUInt16LittleEndian(sut.AsSpan(6)));
    }

    /// <summary>A recording still sounding at the loop frame can't start partway the second time: a conversion note says so.</summary>
    [Fact]
    public void Write_WithRecordingAcrossLoop_AddsNote()
    {
        // Arrange
        ConversionNotes notes = Notes();
        GenesisStream stream = new([[ChipEvent.Play(0, new sbyte[13_317], false, 0, "x")], []], 1, false, NtscRate);

        // Act
        Xgm2Writer.Write(stream, notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.StartsWith("1 recordings still sound where the song loops back", StringComparison.Ordinal));
    }

    /// <summary>A song that ends stops every PCM channel still playing and lets every key up.</summary>
    [Fact]
    public void Write_WithEndingSong_StopsPcmAndKeys()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Fm(0, 0x28, 0xf1), ChipEvent.Play(2, new sbyte[13_317], false, 0, "x")]], null, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert
        Assert.Equal([0x49, 0x12, 0x01, 0x00, 0x41, 0x12, 0x00, 0xff, 0xff, 0xff, 0xff], sut);
    }

    /// <summary>A recording still sounding where a looping song jumps back is stopped in the last frame, so it can't play into the loop's opening.</summary>
    [Fact]
    public void Write_WithRecordingAtLoopJump_StopsIt()
    {
        // Arrange
        GenesisStream stream = new([[], [ChipEvent.Play(1, new sbyte[13_317], false, 0, "x")]], 0, false, NtscRate);

        // Act
        byte[] sut = Fm(Xgm2Writer.Write(stream, Notes()));

        // Assert: frame 0 sets the slots silent and the keys up; frame 1 plays, and stops before the loop command ends it.
        Assert.Equal([0x00, 0x11, 0x01, 0x11, 0x00, 0xff, 0x00, 0x00, 0x00], sut[(SilentSlots.Length + 6)..]);
    }

    /// <summary>A recording is stored only as far as a play reaches, plus two frames: here 3 frames at 60 Hz of a 1 s recording.</summary>
    [Fact]
    public void Write_WithRecordingStoppedEarly_StoresWhatIsHeard()
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = Frames(2);
        frames[0] = [ChipEvent.Play(0, new sbyte[13_317], false, 0, "x")];
        frames[1] = [ChipEvent.Stop(0)];

        // Act
        Xgm2Output sut = Xgm2Writer.Write(new GenesisStream(frames, null, false, NtscRate), Notes());

        // Assert: ceil(3 / 60 × 13,317) = 666 bytes, padded to 768.
        Assert.Equal(768, sut.Samples[0].Bytes);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(sut.Bytes.AsSpan(6)));
    }

    /// <summary>
    /// Sample bytes are stored unsigned, silence as <c>$80</c> padding included, because xgm2tool adds <c>$80</c> to every
    /// byte it compiles (docs/adr/0127-xgm2-unsigned-samples.md).
    /// </summary>
    [Fact]
    public void Write_WithRecording_StoresItUnsignedWithSilentPadding()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Play(0, [0, 1, -1, 127, -128], false, 0, "1")]], null, false, NtscRate);

        // Act
        byte[] sut = Xgm2Writer.Write(stream, Notes()).Bytes;

        // Assert
        Assert.Equal([0x80, 0x81, 0x7f, 0xff, 0x00], sut.AsSpan(DataOffset, 5).ToArray());
        Assert.All(sut.AsSpan(DataOffset + 5, Block - 5).ToArray(), b => Assert.Equal(0x80, b));
    }

    /// <summary>Copies made apart with the same bytes are stored once, with one sample id, as the capacity check counts them.</summary>
    [Fact]
    public void Write_WithSameBytesInTwoCopies_StoresThemOnce()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Play(0, [1, 2, 3], false, 0, "1"), ChipEvent.Play(1, [1, 2, 3], false, 0, "1")]], null, false, NtscRate);

        // Act
        Xgm2Output sut = Xgm2Writer.Write(stream, Notes());

        // Assert
        Assert.Single(sut.Samples);
        Assert.Equal([0x10, 0x01, 0x11, 0x01], Fm(sut)[..4]);
    }

    /// <summary>The recordings the file stores must fit <c>--max-pcm-bytes</c>, counted as written: over it is a capacity failure (exit 6).</summary>
    [Fact]
    public void Write_WithRecordingsOverBudget_ThrowsDoesNotFit()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Play(0, new sbyte[300], false, 0, "x")]], null, false, NtscRate);

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => Xgm2Writer.Write(stream, Notes(), pcmBudget: 511));

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, error.Code);
    }

    /// <summary>A loop to a frame past the start gets a conversion note: SGDK's packer doesn't move the loop point.</summary>
    [Fact]
    public void Write_WithLoopPastStart_NotesSgdkPacking()
    {
        // Arrange
        ConversionNotes notes = Notes();

        // Act
        Xgm2Writer.Write(new GenesisStream(Frames(2), 1, false, NtscRate), notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.StartsWith("The song loops back to frame 1, not to its start", StringComparison.Ordinal));
    }

    /// <summary>A moment goes to the nearest frame start; half a frame rounds up.</summary>
    [Theory]
    [InlineData(0.02, 50, 1)]
    [InlineData(0.01, 50, 1)]
    [InlineData(0.0099, 50, 0)]
    [InlineData(1.0, 60, 60)]
    [InlineData(60.0, 49.7015, 2982)]
    public void FrameAt_WithMoment_ReturnsNearestFrame(double seconds, double rate, int expected)
    {
        // Act
        int sut = GenesisStream.FrameAt(seconds, rate);

        // Assert
        Assert.Equal(expected, sut);
    }

    /// <summary>Report section 3 sums the file up, then lists each recording with its rate and size.</summary>
    [Fact]
    public void ReportLines_WithRecordings_ListsThem()
    {
        // Arrange
        GenesisStream stream = new([[ChipEvent.Play(0, new sbyte[300], true, 0, "1 \"kick\"")]], null, true, NtscRate);
        Xgm2Output output = Xgm2Writer.Write(stream, Notes());
        GenesisPlan plan = new("t", [], null, new Timing(6, 125), [], []);

        // Act
        IReadOnlyList<string> sut = Xgm2Report.Lines(output, plan);

        // Assert
        Assert.Equal(
            [
                "Output (XGM2): 0 FM patches, 0 PSG sounds, 1 PCM sample, 1 KB PCM, 1 KB FM and 1 KB PSG stream, 1 frames, no loop, 2 KB file",
                " DAC 001  ← 1 \"kick\"   6659 Hz  512 B  half speed",
            ],
            sut);
    }

    private static ConversionNotes Notes() => new(Logger.None);

    private static IReadOnlyList<ChipEvent>[] Frames(int count) => [.. Enumerable.Range(0, count).Select(_ => (IReadOnlyList<ChipEvent>)[])];

    /// <summary>The FM block of <paramref name="output"/>, unpadded.</summary>
    private static byte[] Fm(Xgm2Output output)
    {
        int slen = BinaryPrimitives.ReadUInt16LittleEndian(output.Bytes.AsSpan(6)) * Block;
        return output.Bytes[(DataOffset + slen)..(DataOffset + slen + output.FmBytes)];
    }

    /// <summary>The PSG block of <paramref name="output"/>, unpadded.</summary>
    private static byte[] Psg(Xgm2Output output)
    {
        int slen = BinaryPrimitives.ReadUInt16LittleEndian(output.Bytes.AsSpan(6)) * Block;
        int fmlen = BinaryPrimitives.ReadUInt16LittleEndian(output.Bytes.AsSpan(8)) * Block;
        return output.Bytes[(DataOffset + slen + fmlen)..(DataOffset + slen + fmlen + output.PsgBytes)];
    }
}
