using System.Text;
using Mod2Xgm.Dmf;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Tests for the test-only DMF reader (plan T4.2): every field of docs/formats/dmf.md, file versions 24–27, both
/// Genesis systems, and the strict checks that catch writer bugs. One file is spelled out byte by byte from the spec,
/// so the reader and <see cref="DmfBuilder"/> can't share a mistake unnoticed.
/// </summary>
public sealed class DmfReaderTests
{
    /// <summary>Reads what the builder makes.</summary>
    private static DmfFile Read(DmfBuilder builder) => DmfReader.Read(builder.Build());

    /// <summary>Asserts the reader rejects the file, with a message containing <paramref name="message"/>.</summary>
    private static void Rejects(DmfBuilder builder, string message)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Read(builder));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Contains("at byte 0x", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A STD instrument with only a volume macro.</summary>
    private static DmfStdInstrument VolumeOnly(params int[] volumes) =>
        new(new DmfMacro(volumes, volumes.Length > 0 ? 0 : -1), new DmfMacro([], -1), 0, new DmfMacro([], -1), new DmfMacro([], -1));

    /// <summary>A small file spelled out byte by byte from DMF_SPECS.txt reads back field by field.</summary>
    [Fact]
    public void Read_WithHandSpelledFile_ReadsEveryField()
    {
        // Arrange
        List<byte> bytes =
        [
            .. ".DelekDefleMask."u8,
            24, 0x02, // version, system
            1, (byte)'A', 0, // name "A", author ""
            4, 16, // highlights
            0, 6, 3, 1, 1, (byte)'1', (byte)'2', (byte)'5', // time base, tick times, NTSC, custom Hz "125"
            1, 0, 0, 0, 1, // 1 row per pattern, 1 matrix row
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, // one pattern index per channel
            1, 1, (byte)'p', 0, // 1 instrument "p", STD
            2, 15, 0, 0, 0, 7, 0, 0, 0, 0xFF, // volume macro 15, 7, no loop
            0, 1, 0, 0, // empty arpeggio macro, fixed mode, empty duty and wavetable macros
            0, // no wavetables
        ];
        for (int channel = 0; channel < 10; channel++)
        {
            bytes.Add(1); // 1 effect column
            byte[] cell = channel == 0
                ? [12, 0, 3, 0, 0x40, 0, 0x17, 0, 1, 0, 0, 0] // C-4 (note 12, octave 3), volume 40, 1701, instrument 0
                : [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]; // empty
            bytes.AddRange(cell);
        }
        bytes.AddRange([1, 2, 0, 0, 0, 1, (byte)'s', 5, 5, 50, 16, 0xFF, 0x7F, 0x00, 0x80]); // sample "s": 32 kHz, 7FFF, 8000

        // Act
        DmfFile file = DmfReader.Read(DmfBuilder.Compress([.. bytes]));

        // Assert
        Assert.Equal((24, DmfSystem.Genesis, "A", "", 4, 16), (file.Version, file.System, file.Name, file.Author, file.HighlightA, file.HighlightB));
        Assert.Equal((0, 6, 3, true, true, 125, 125), (file.Timing.TimeBase, file.Timing.TickTime1, file.Timing.TickTime2, file.Timing.Ntsc, file.Timing.UsingCustomHz, file.Timing.CustomHz, file.Timing.TickRateHz));
        Assert.Equal("125"u8.ToArray(), file.Timing.CustomHzBytes);
        Assert.Equal((1, 1, 10), (file.RowsPerPattern, file.MatrixRows, file.Channels.Count));
        Assert.Equal(Enumerable.Range(0, 10), file.Channels.Select(c => c.Orders.Single()));
        Assert.All(file.Channels, c => Assert.Null(c.PatternNames));
        DmfInstrument instrument = Assert.Single(file.Instruments);
        Assert.Equal("p", instrument.Name);
        Assert.Null(instrument.Fm);
        DmfStdInstrument std = Assert.IsType<DmfStdInstrument>(instrument.Std);
        Assert.Equal([15, 7], std.Volume.Values);
        Assert.Equal(-1, std.Volume.Loop);
        Assert.Empty(std.Arpeggio.Values);
        Assert.Equal(1, std.ArpeggioMode);
        Assert.Empty(std.DutyNoise.Values);
        Assert.Empty(std.Wavetable.Values);
        Assert.Empty(file.Wavetables);
        DmfCell first = file.Channels[0].Patterns[0][0];
        Assert.Equal((12, 3, 0x40, 0), (first.Note, first.Octave, first.Volume, first.Instrument));
        Assert.Equal([new DmfEffect(0x17, 1)], first.Effects);
        Assert.False(first.IsEmpty);
        Assert.All(file.Channels.Skip(1), c => Assert.True(c.Patterns[0][0].IsEmpty));
        DmfSample sample = Assert.Single(file.Samples);
        Assert.Equal(("s", 5, 5, 50, 16, 32000), (sample.Name, sample.RateCode, sample.Pitch, sample.Amp, sample.Bits, sample.RateHz));
        Assert.Equal([short.MaxValue, short.MinValue], sample.Data);
        Assert.Null(sample.CutStart);
        Assert.Null(sample.CutEnd);
        Assert.Equal(0, file.TrailingBytes);
    }

    /// <summary>The builder's defaults read back as a minimal PAL Genesis song.</summary>
    [Fact]
    public void Read_WithDefaults_ReadsThemBack()
    {
        // Act
        DmfFile file = Read(new DmfBuilder());

        // Assert
        Assert.Equal((24, DmfSystem.Genesis, "test song", "Mod2Xgm"), (file.Version, file.System, file.Name, file.Author));
        Assert.Equal((false, false, 50, 50), (file.Timing.Ntsc, file.Timing.UsingCustomHz, file.Timing.CustomHz, file.Timing.TickRateHz));
        Assert.Equal((4, 1), (file.RowsPerPattern, file.MatrixRows));
        Assert.All(file.Channels, c => Assert.Equal((1, 4), (c.EffectColumns, c.Patterns.Single().Count)));
        Assert.All(file.Channels.SelectMany(c => c.Patterns.Single()), cell => Assert.True(cell.IsEmpty));
        Assert.Empty(file.Instruments);
        Assert.Empty(file.Samples);
    }

    /// <summary>Every header field reads back as written.</summary>
    [Fact]
    public void Read_WithHeaderFields_ReadsThem()
    {
        // Act
        DmfFile file = Read(new DmfBuilder { Name = "Café", Author = "", HighlightA = 8, HighlightB = 32, TimeBase = 1, TickTime1 = 3, TickTime2 = 5 });

        // Assert
        Assert.Equal(("Café", "", 8, 32), (file.Name, file.Author, file.HighlightA, file.HighlightB));
        Assert.Equal((1, 3, 5), (file.Timing.TimeBase, file.Timing.TickTime1, file.Timing.TickTime2));
    }

    /// <summary>Ext. CH3 files have 13 channels; the PSG is still the last 4.</summary>
    [Fact]
    public void Read_WithExtendedChannel3_Has13Channels()
    {
        // Act
        DmfFile file = Read(new DmfBuilder { System = 0x42 }.WithCell(12, 0, 0, note: 5, octave: 2, volume: 0xF));

        // Assert
        Assert.Equal(DmfSystem.GenesisExtendedChannel3, file.System);
        Assert.Equal(13, file.Channels.Count);
        Assert.Equal((5, 2, 0xF), (file.Channels[12].Patterns[0][0].Note, file.Channels[12].Patterns[0][0].Octave, file.Channels[12].Patterns[0][0].Volume));
        Assert.Equal(13, DmfSystems.ChannelCount(file.System));
        Assert.False(DmfSystems.IsPsg(file.System, 8));
        Assert.True(DmfSystems.IsPsg(file.System, 9));
    }

    /// <summary>FM channels take volumes up to 7F, PSG channels up to F.</summary>
    [Theory]
    [InlineData(0x02, 5, 0x7F)]
    [InlineData(0x02, 6, 0xF)]
    [InlineData(0x42, 8, 0x7F)]
    [InlineData(0x42, 9, 0xF)]
    public void Read_WithChannelKind_UsesItsVolumeRange(int system, int channel, int max)
    {
        // Arrange
        DmfBuilder builder = new DmfBuilder { System = system }.WithCell(channel, 0, 0, volume: max);

        // Act
        DmfFile file = Read(builder);

        // Assert
        Assert.Equal(max, file.Channels[channel].Patterns[0][0].Volume);
    }

    /// <summary>One more than a channel's highest volume is rejected, naming the cell.</summary>
    [Theory]
    [InlineData(0x02, 5, 0x7F)]
    [InlineData(0x02, 6, 0xF)]
    [InlineData(0x42, 8, 0x7F)]
    [InlineData(0x42, 9, 0xF)]
    public void ReadDecompressed_WithVolumePastChannelRange_Throws(int system, int channel, int max)
    {
        // Arrange
        DmfBuilder builder = new DmfBuilder { System = system }.WithCell(channel, 0, 0, volume: max + 1);

        // Act & Assert
        Rejects(builder, $"channel {channel + 1} matrix row 0 row 0: volume {max + 1}");
    }

    /// <summary>Matrix, cells and effect columns land on the right channel, matrix row and row.</summary>
    [Fact]
    public void Read_WithMatrix_StoresPatternsPerRow()
    {
        // Act
        DmfFile file = Read(new DmfBuilder { MatrixRows = 3, RowsPerPattern = 2 }
            .WithOrder(1, 2, 0x7F)
            .WithEffectColumns(3, 4)
            .WithCell(3, 2, 1, note: 100, effects: [new DmfEffect(0x0B, 0), new DmfEffect(-1, -1), new DmfEffect(0x0D, 0x10), new DmfEffect(0xFF, 0xFF)])
            .WithCell(0, 1, 0, note: 1, octave: 4, instrument: -1));

        // Assert
        Assert.Equal(3, file.MatrixRows);
        Assert.Equal([0, 1, 0x7F], file.Channels[1].Orders);
        Assert.Equal([0, 1, 2], file.Channels[0].Orders);
        Assert.Equal(4, file.Channels[3].EffectColumns);
        Assert.Equal(3, file.Channels[3].Patterns.Count);
        DmfCell off = file.Channels[3].Patterns[2][1];
        Assert.Equal(100, off.Note);
        Assert.Equal([new DmfEffect(0x0B, 0), DmfEffect.Empty, new DmfEffect(0x0D, 0x10), new DmfEffect(0xFF, 0xFF)], off.Effects);
        Assert.True(off.Effects[1].IsEmpty);
        Assert.False(off.Effects[0].IsEmpty);
        Assert.Equal((1, 4), (file.Channels[0].Patterns[1][0].Note, file.Channels[0].Patterns[1][0].Octave));
        Assert.True(file.Channels[0].Patterns[1][1].IsEmpty);
    }

    /// <summary>FM operators come back in file order with every byte in its field.</summary>
    [Fact]
    public void Read_WithFmInstrument_ReadsFields()
    {
        // Act
        DmfFile file = Read(new DmfBuilder().WithFmInstrument("bass", DmfBuilder.CountingFm(1)).WithCell(0, 0, 0, note: 12, octave: 2, instrument: 0));

        // Assert
        DmfInstrument instrument = Assert.Single(file.Instruments);
        Assert.Equal("bass", instrument.Name);
        Assert.Null(instrument.Std);
        DmfFmInstrument fm = Assert.IsType<DmfFmInstrument>(instrument.Fm);
        Assert.Equal((1, 2, 3, 4), (fm.Algorithm, fm.Feedback, fm.Fms, fm.Ams));
        Assert.Equal([DmfBuilder.Operator(5), DmfBuilder.Operator(17), DmfBuilder.Operator(29), DmfBuilder.Operator(41)], fm.Operators);
        DmfOperator last = fm.Operators[3];
        Assert.Equal((41, 42, 43, 44, 45, 46), (last.Am, last.Ar, last.Dr, last.Mult, last.Rr, last.Sl));
        Assert.Equal((47, 48, 49, 50, 51, 52), (last.Tl, last.Dt2, last.Rs, last.Dt, last.D2R, last.SsgMode));
        Assert.Equal(0, file.Channels[0].Patterns[0][0].Instrument);
    }

    /// <summary>All four STD macros, with values, loops, signed arpeggio values and an empty macro (no loop byte).</summary>
    [Fact]
    public void Read_WithStdInstrument_ReadsMacros()
    {
        // Arrange
        DmfStdInstrument std = new(new DmfMacro([15, 12, 8], 1), new DmfMacro([12, 0, -12], -1), 0, new DmfMacro([1, 3], 0), new DmfMacro([], -1));

        // Act
        DmfFile file = Read(new DmfBuilder().WithStdInstrument("noise", std).WithStdInstrument("next", VolumeOnly(9)));

        // Assert
        DmfStdInstrument read = Assert.IsType<DmfStdInstrument>(file.Instruments[0].Std);
        Assert.Equal([15, 12, 8], read.Volume.Values);
        Assert.Equal(1, read.Volume.Loop);
        Assert.Equal([12, 0, -12], read.Arpeggio.Values);
        Assert.Equal(-1, read.Arpeggio.Loop);
        Assert.Equal(0, read.ArpeggioMode);
        Assert.Equal([1, 3], read.DutyNoise.Values);
        Assert.Equal(0, read.DutyNoise.Loop);
        Assert.Empty(read.Wavetable.Values);
        Assert.Equal(-1, read.Wavetable.Loop);
        Assert.Equal("next", file.Instruments[1].Name);
        Assert.Equal([9], Assert.IsType<DmfStdInstrument>(file.Instruments[1].Std).Volume.Values);
    }

    /// <summary>Wavetables (unused on Genesis, but part of the layout) are read as 4-byte values.</summary>
    [Fact]
    public void Read_WithWavetables_ReadsThem()
    {
        // Act
        DmfFile file = Read(new DmfBuilder().WithWavetable(0, 15, -1).WithWavetable());

        // Assert
        Assert.Equal(2, file.Wavetables.Count);
        Assert.Equal([0, 15, -1], file.Wavetables[0]);
        Assert.Empty(file.Wavetables[1]);
    }

    /// <summary>The custom Hz bytes spell the rate in ASCII digits padded with 0; it replaces PAL/NTSC only while switched on.</summary>
    [Theory]
    [InlineData("50\0", 1, 0, 50, 50)]
    [InlineData("125", 1, 0, 125, 125)]
    [InlineData("7\0\0", 1, 1, 7, 7)]
    [InlineData("60\0", 0, 0, 60, 50)]
    [InlineData("60\0", 0, 1, 60, 60)]
    [InlineData("\0\0\0", 0, 0, null, 50)]
    [InlineData("abc", 0, 1, null, 60)]
    public void Read_WithCustomHz_ReadsRate(string bytes, int usingCustomHz, int framesMode, int? customHz, int tickRateHz)
    {
        // Act
        DmfTiming timing = Read(new DmfBuilder { CustomHzBytes = Encoding.Latin1.GetBytes(bytes), UsingCustomHz = usingCustomHz, FramesMode = framesMode }).Timing;

        // Assert
        Assert.Equal(Encoding.Latin1.GetBytes(bytes), timing.CustomHzBytes);
        Assert.Equal(customHz, timing.CustomHz);
        Assert.Equal(tickRateHz, timing.TickRateHz);
    }

    /// <summary>Version 25 and later store a pattern name after each matrix byte.</summary>
    [Theory]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    public void Read_FromVersion25_ReadsPatternNames(int version)
    {
        // Arrange
        DmfBuilder builder = new DmfBuilder { Version = version, MatrixRows = 2 }.WithPatternName(4, 1, "chorus").WithOrder(4, 1, 9).WithCell(4, 1, 3, note: 7, octave: 1);

        // Act
        DmfFile file = Read(builder);

        // Assert
        Assert.Equal(version, file.Version);
        Assert.Equal(["", "chorus"], file.Channels[4].PatternNames);
        Assert.Equal([0, 9], file.Channels[4].Orders);
        Assert.Equal(7, file.Channels[4].Patterns[1][3].Note);
    }

    /// <summary>Version 24 has no pattern names, and the matrix and cells read the same.</summary>
    [Fact]
    public void Read_FromVersion24_HasNoPatternNames()
    {
        // Arrange
        DmfBuilder builder = new DmfBuilder { Version = 24, MatrixRows = 2 }.WithPatternName(4, 1, "chorus").WithOrder(4, 1, 9).WithCell(4, 1, 3, note: 7, octave: 1);

        // Act
        DmfFile file = Read(builder);

        // Assert
        Assert.Equal(24, file.Version);
        Assert.Null(file.Channels[4].PatternNames);
        Assert.Equal([0, 9], file.Channels[4].Orders);
        Assert.Equal(7, file.Channels[4].Patterns[1][3].Note);
    }

    /// <summary>Version 27 stores sample cut points before the data; earlier versions don't.</summary>
    [Theory]
    [InlineData(26, null, null)]
    [InlineData(27, 1, 3)]
    public void Read_FromVersion27_ReadsSampleCutPoints(int version, int? cutStart, int? cutEnd)
    {
        // Act
        DmfFile file = Read(new DmfBuilder { Version = version }.WithSample(new DmfSample("snare", 2, 5, 50, 16, 1, 3, [10, 20, 30, 40])));

        // Assert
        DmfSample sample = Assert.Single(file.Samples);
        Assert.Equal((cutStart, cutEnd), (sample.CutStart, sample.CutEnd));
        Assert.Equal([10, 20, 30, 40], sample.Data);
    }

    /// <summary>Several samples, 8-bit ones included (their 2-byte values hold 0–255), and an empty one.</summary>
    [Fact]
    public void Read_WithSamples_ReadsThem()
    {
        // Act
        DmfFile file = Read(new DmfBuilder()
            .WithSample(new DmfSample("eight", 0, 10, 100, 8, null, null, [0, 128, 255]))
            .WithSample(new DmfSample("empty", 4, 0, 0, 16, null, null, [])));

        // Assert
        Assert.Equal(2, file.Samples.Count);
        Assert.Equal(("eight", 0, 10, 100, 8, 4000), (file.Samples[0].Name, file.Samples[0].RateCode, file.Samples[0].Pitch, file.Samples[0].Amp, file.Samples[0].Bits, file.Samples[0].RateHz));
        Assert.Equal([0, 128, 255], file.Samples[0].Data);
        Assert.Equal(("empty", 22050), (file.Samples[1].Name, file.Samples[1].RateHz));
        Assert.Empty(file.Samples[1].Data);
    }

    /// <summary>Each rate code stands for its rate.</summary>
    [Theory]
    [InlineData(0, 4000)]
    [InlineData(1, 8000)]
    [InlineData(2, 11025)]
    [InlineData(3, 16000)]
    [InlineData(4, 22050)]
    [InlineData(5, 32000)]
    public void Read_WithSampleRateCode_ReadsRate(int code, int hz)
    {
        // Act & Assert
        Assert.Equal(hz, Read(new DmfBuilder().WithSample(new DmfSample("x", code, 5, 50, 16, null, null, [0]))).Samples[0].RateHz);
    }

    /// <summary>Bytes after the last sample are counted, not rejected.</summary>
    [Fact]
    public void Read_WithTrailingBytes_CountsThem()
    {
        // Act & Assert
        Assert.Equal(3, Read(new DmfBuilder { TrailingBytes = [1, 2, 3] }).TrailingBytes);
    }

    /// <summary>A file with one of everything, for the cut-short tests.</summary>
    private static byte[] FileWithEverySection() => new DmfBuilder { Version = 27 }
        .WithFmInstrument("fm", DmfBuilder.CountingFm(0))
        .WithStdInstrument("psg", VolumeOnly(15, 0))
        .WithWavetable(1, 2)
        .WithSample(new DmfSample("s", 5, 5, 50, 16, null, null, [1, 2]))
        .BuildDecompressed();

    /// <summary>The whole file reads to its last byte, so the cut-short test below cuts something that matters.</summary>
    [Fact]
    public void ReadDecompressed_WithWholeFile_LeavesNoTrailingBytes()
    {
        // Arrange
        byte[] data = FileWithEverySection();

        // Act
        DmfFile file = DmfReader.ReadDecompressed(data);

        // Assert
        Assert.Equal(0, file.TrailingBytes);
    }

    /// <summary>A file cut short anywhere is rejected, naming the field it ends in.</summary>
    [Fact]
    public void ReadDecompressed_WithTruncatedFile_Throws()
    {
        // Arrange
        byte[] data = FileWithEverySection();

        // Act & Assert
        Assert.All(Enumerable.Range(0, data.Length), length =>
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => DmfReader.ReadDecompressed(data[..length]));
            Assert.Contains("the file ends inside", error.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>Data that isn't zlib-compressed is rejected.</summary>
    [Fact]
    public void Decompress_WithUncompressedData_Throws()
    {
        // Act & Assert
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => DmfReader.Read(new DmfBuilder().BuildDecompressed()));
        Assert.StartsWith("Not zlib-compressed data", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Header values the format or DefleMask doesn't allow are rejected.</summary>
    [Fact]
    public void ReadDecompressed_WithBadHeader_Throws()
    {
        // Act & Assert
        Rejects(new DmfBuilder { Magic = ".DelekDefleMask!" }, "format string is \".DelekDefleMask!\"");
        Rejects(new DmfBuilder { Version = DmfReader.MinVersion - 1 }, "file version 23 isn't 24–27");
        Rejects(new DmfBuilder { Version = DmfReader.MaxVersion + 1 }, "file version 28 isn't 24–27");
        Rejects(new DmfBuilder { System = 0x03 }, "system 0x03 isn't Genesis");
        Rejects(new DmfBuilder { TickTime1 = 0 }, "tick time 1 is 0");
        Rejects(new DmfBuilder { TickTime2 = 0 }, "tick time 2 is 0");
        Rejects(new DmfBuilder { FramesMode = 2 }, "frames mode is 2");
        Rejects(new DmfBuilder { UsingCustomHz = 2 }, "using custom Hz is 2");
        Rejects(new DmfBuilder { UsingCustomHz = 1, CustomHzBytes = "5\00"u8.ToArray() }, "custom Hz bytes 350030 don't spell a rate");
        Rejects(new DmfBuilder { UsingCustomHz = 1, CustomHzBytes = "000"u8.ToArray() }, "custom Hz bytes 303030 don't spell a rate");
        Rejects(new DmfBuilder { RowsPerPattern = 0 }, "rows per pattern is 0");
        Rejects(new DmfBuilder { RowsPerPattern = 257 }, "rows per pattern is 257");
        Rejects(new DmfBuilder { MatrixRows = 0 }, "matrix rows is 0");
        Rejects(new DmfBuilder { MatrixRows = 128 }, "matrix rows is 128");
        Rejects(new DmfBuilder().WithOrder(2, 0, 0x80), "channel 3 matrix row 0 names pattern 128");
    }

    /// <summary>Instrument bytes the format doesn't allow are rejected.</summary>
    [Fact]
    public void ReadDecompressed_WithBadInstrument_Throws()
    {
        // Act & Assert
        Rejects(new DmfBuilder().WithRawInstrument([1, (byte)'x', 2]), "instrument 0 mode is 2");
        Rejects(new DmfBuilder().WithStdInstrument("a", VolumeOnly() with { ArpeggioMode = 2 }), "instrument 0 arpeggio mode is 2");
        Rejects(new DmfBuilder().WithStdInstrument("a", VolumeOnly([.. Enumerable.Range(0, 128)])), "volume macro size is 128");
        Rejects(new DmfBuilder().WithStdInstrument("a", VolumeOnly(1, 2) with { DutyNoise = new DmfMacro([1, 2], 2) }), "duty/noise macro loops at 2");
        Rejects(new DmfBuilder().WithStdInstrument("a", VolumeOnly(1) with { Wavetable = new DmfMacro([1], -2) }), "wavetable macro loops at -2");
    }

    /// <summary>Cells the format or DefleMask doesn't allow are rejected, naming the cell.</summary>
    [Fact]
    public void ReadDecompressed_WithBadCell_Throws()
    {
        // Act & Assert
        Rejects(new DmfBuilder { RowsPerPattern = 8 }.WithCell(1, 0, 7, note: 13, octave: 1), "channel 2 matrix row 0 row 7: note 13");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, note: -1), "note -1");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, note: 0, octave: 2), "empty note with octave 2");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, volume: -2), "volume -2");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, effects: new DmfEffect(0x100, 0)), "effect 256,0");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, effects: new DmfEffect(0x08, -2)), "effect 8,-2");
        Rejects(new DmfBuilder().WithCell(0, 0, 0, instrument: 0), "matrix row 0 row 0: instrument 0, but there are no instruments");
        Rejects(new DmfBuilder().WithStdInstrument("a", VolumeOnly()).WithCell(9, 0, 0, instrument: 1), "channel 10 matrix row 0 row 0: instrument 1");
        Rejects(new DmfBuilder().WithEffectColumns(4, 0), "channel 5 has 0 effect columns");
        Rejects(new DmfBuilder().WithEffectColumns(4, 5), "channel 5 has 5 effect columns");
    }

    /// <summary>Sample fields the format or DefleMask doesn't allow are rejected.</summary>
    [Fact]
    public void ReadDecompressed_WithBadSample_Throws()
    {
        // Arrange
        DmfSample good = new("s", 5, 5, 50, 16, null, null, [1, 2]);

        // Act & Assert
        Rejects(new DmfBuilder().WithSample(good with { RateCode = 6 }), "sample 0 rate code is 6");
        Rejects(new DmfBuilder().WithSample(good).WithSample(good with { Pitch = 11 }), "sample 1 pitch is 11");
        Rejects(new DmfBuilder().WithSample(good with { Bits = 12 }), "sample 0 has 12 bits");
        Rejects(new DmfBuilder { Version = 27 }.WithSample(good with { CutStart = 2, CutEnd = 1 }), "sample 0 cut 2–1");
        Rejects(new DmfBuilder { Version = 27 }.WithSample(good with { CutStart = -1 }), "sample 0 cut -1–2");
    }

    /// <summary>A sample size that is negative is rejected before reading the data.</summary>
    [Fact]
    public void ReadDecompressed_WithNegativeSampleSize_Throws()
    {
        // Arrange
        byte[] data = new DmfBuilder().WithSample(new DmfSample("s", 5, 5, 50, 16, null, null, [1])).BuildDecompressed();
        int sizeAt = data.Length - 2 - 4 - 2 - 4; // data, rate/pitch/amp/bits, name "s", size
        data[sizeAt + 3] = 0x80;

        // Act
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => DmfReader.ReadDecompressed(data));

        // Assert
        Assert.Contains("sample 0 size is", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A sample size larger than the data left is rejected before reading the data.</summary>
    [Fact]
    public void ReadDecompressed_WithSampleSizePastEnd_Throws()
    {
        // Arrange
        byte[] data = new DmfBuilder().WithSample(new DmfSample("s", 5, 5, 50, 16, null, null, [1])).BuildDecompressed();
        int sizeAt = data.Length - 2 - 4 - 2 - 4; // data, rate/pitch/amp/bits, name "s", size
        data[sizeAt] = 2;

        // Act
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => DmfReader.ReadDecompressed(data));

        // Assert
        Assert.Contains("the file ends inside sample 0's data (2 × 2 bytes needed, 2 left)", error.Message, StringComparison.Ordinal);
    }
}
