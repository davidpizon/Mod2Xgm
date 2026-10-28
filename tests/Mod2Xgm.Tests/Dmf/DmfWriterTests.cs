using Mod2Xgm.Dmf;
using Serilog.Core;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Tests for <see cref="DmfWriter"/> (plan T5.2): one song spelled out byte by byte from DMF_SPECS.txt, the writer's
/// bytes against the independent <see cref="DmfBuilder"/> on every probe, a round trip through the strict
/// <see cref="DmfReader"/>, the text rules, and the checks that refuse a song the format can't hold.
/// </summary>
public sealed class DmfWriterTests
{
    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(DmfProbes.All.Keys);

    /// <summary>A minimal valid song: Genesis, PAL, speed 6, one matrix row of 2 empty rows, one effect column, nothing else.</summary>
    private static DmfFile EmptySong(DmfSystem system = DmfSystem.Genesis)
    {
        DmfChannel[] channels = [.. Enumerable.Range(0, DmfSystems.ChannelCount(system)).Select(_ => Column(Empty(), Empty()))];
        return new DmfFile(24, system, "empty", "nobody", 4, 16, new DmfTiming(0, 6, 6, false, false, [], null), 2, channels, [], [], [], 0);
    }

    private static DmfCell Empty() => new(0, 0, -1, [], -1);

    private static DmfChannel Column(params DmfCell[] rows) => new([0], null, 1, [rows]);

    /// <summary>Runs <see cref="DmfWriter.Serialize"/> with a collecting logger, returning the notes it added.</summary>
    private static (byte[] Bytes, List<string> Notes) Serialize(DmfFile file)
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        byte[] bytes = DmfWriter.Serialize(file, notes);
        return (bytes, [.. notes.Messages]);
    }

    private static ConversionNotes SilentNotes() => new(Logger.None);

    /// <summary>A small song spelled out byte by byte from DMF_SPECS.txt is written exactly so.</summary>
    [Fact]
    public void HandSpelledFileFromTheSpec()
    {
        List<byte> expected =
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
            expected.Add(1); // 1 effect column
            expected.AddRange(channel == 0
                ? [12, 0, 3, 0, 0x40, 0, 0x17, 0, 1, 0, 0, 0] // C-4 (note 12, octave 3), volume 40, 1701, instrument 0
                : [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // empty
        }

        expected.AddRange([1, 2, 0, 0, 0, 1, (byte)'s', 5, 5, 50, 16, 0xFF, 0x7F, 0x00, 0x80]); // sample "s": 32 kHz, 7FFF, 8000

        DmfCell note = new(12, 3, 0x40, [new DmfEffect(0x17, 1)], 0);
        DmfChannel[] channels =
        [
            .. Enumerable.Range(0, 10).Select(i => new DmfChannel([i], null, 1, [[i == 0 ? note : Empty()]])),
        ];
        DmfInstrument instrument = new("p", null, new DmfStdInstrument(new DmfMacro([15, 7], -1), new DmfMacro([], -1), 1, new DmfMacro([], -1), new DmfMacro([], -1)));
        DmfSample sample = new("s", 5, 5, 50, 16, null, null, [short.MaxValue, short.MinValue]);
        DmfFile file = new(24, DmfSystem.Genesis, "A", string.Empty, 4, 16, new DmfTiming(0, 6, 3, true, true, [], 125), 1, channels, [instrument], [], [sample], 0);

        (byte[] bytes, List<string> notes) = Serialize(file);

        Assert.Equal([.. expected], bytes);
        Assert.Empty(notes);
    }

    /// <summary>
    /// The writer's bytes equal the independent <see cref="DmfBuilder"/>'s on every probe: read each probe, write it
    /// back, and the decompressed bytes match. (The compressed bytes may differ between platforms.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ProbesAreWrittenByteForByte(string name)
    {
        DmfBuilder builder = DmfProbes.All[name]();
        DmfFile probe = DmfReader.Read(builder.Build());

        byte[] written = DmfWriter.Write(probe, SilentNotes());

        Assert.Equal(builder.BuildDecompressed(), DmfReader.Decompress(written));
    }

    /// <summary>A song using every section (both instrument kinds, a wavetable, effect columns, both sample sizes) survives write → read.</summary>
    [Fact]
    public void EverySectionRoundTrips()
    {
        DmfBuilder builder = new()
        {
            System = (int)DmfSystem.GenesisExtendedChannel3,
            Name = "round trip",
            Author = "me",
            FramesMode = 1,
            UsingCustomHz = 1,
            CustomHzBytes = "125"u8.ToArray(),
            TickTime1 = 7,
            TickTime2 = 5,
            RowsPerPattern = 8,
            MatrixRows = 3,
        };
        builder
            .WithFmInstrument("fm", DmfBuilder.CountingFm(1))
            .WithStdInstrument("psg", new DmfStdInstrument(new DmfMacro([15, 8, 4], 1), new DmfMacro([12, 19], 0), 1, new DmfMacro([1], -1), new DmfMacro([], -1)))
            .WithWavetable(1, 2, 3)
            .WithEffectColumns(0, 4)
            .WithOrder(0, 2, 1)
            .WithCell(0, 1, 3, note: 9, octave: 4, volume: 0x7F, instrument: 0, effects: [new DmfEffect(0x08, 0x11), new DmfEffect(0x0F, 0x03), new DmfEffect(0x0C, 0x02)])
            .WithCell(12, 2, 7, note: 100)
            .WithCell(9, 0, 0, note: 12, octave: 3, volume: 0xF, instrument: 1)
            .WithSample(new DmfSample("big", 3, 5, 50, 16, null, null, [short.MinValue, -1, 0, 1, short.MaxValue]))
            .WithSample(new DmfSample("small", 0, 4, 100, 8, null, null, [0, 128, 255]));
        DmfFile original = DmfReader.Read(builder.Build());

        DmfFile roundTripped = DmfReader.Read(DmfWriter.Write(original, SilentNotes()));

        Assert.Equal(DmfDescription.Describe(original), DmfDescription.Describe(roundTripped));
        Assert.Equal(DmfDescription.Describe(original), DmfDescription.Describe(DmfReader.Read(builder.Build())));
        Assert.Equal(0, roundTripped.TrailingBytes);
        Assert.Equal(24, roundTripped.Version);
        Assert.Equal(builder.BuildDecompressed(), DmfReader.Decompress(DmfWriter.Write(original, SilentNotes())));
    }

    /// <summary>The same song always gives the same bytes.</summary>
    [Fact]
    public void WritingIsDeterministic()
    {
        DmfFile file = EmptySong();

        Assert.Equal(DmfWriter.Write(file, SilentNotes()), DmfWriter.Write(file, SilentNotes()));
    }

    /// <summary>The file is one zlib stream (header and Adler-32), not raw deflate.</summary>
    [Fact]
    public void OutputIsZlib()
    {
        byte[] written = DmfWriter.Write(EmptySong(), SilentNotes());

        Assert.Equal(0x78, written[0]); // zlib header: deflate, 32 KB window
        Assert.Equal(0, ((written[0] << 8) | written[1]) % 31); // header check bits
        Assert.Equal(DmfWriter.Serialize(EmptySong(), SilentNotes()), DmfReader.Decompress(written));
    }

    /// <summary>The 3 custom Hz bytes are ASCII digits, left-aligned, padded with 0; with no rate they repeat the PAL/NTSC rate.</summary>
    [Theory]
    [InlineData(125, false, false, "125")]
    [InlineData(50, true, false, "50\0")]
    [InlineData(7, true, false, "7\0\0")]
    [InlineData(999, true, true, "999")]
    [InlineData(null, false, false, "50\0")]
    [InlineData(null, false, true, "60\0")]
    public void CustomHzDigits(int? hz, bool usingCustomHz, bool ntsc, string expected)
    {
        DmfFile file = EmptySong() with { Timing = new DmfTiming(0, 6, 6, ntsc, usingCustomHz, [], hz) };

        DmfFile read = DmfReader.Read(DmfWriter.Write(file, SilentNotes()));

        Assert.Equal(expected.Select(c => (byte)c), read.Timing.CustomHzBytes);
        Assert.Equal(usingCustomHz, read.Timing.UsingCustomHz);
        Assert.Equal(ntsc, read.Timing.Ntsc);
    }

    /// <summary>Both Genesis systems are written with their own channel count.</summary>
    [Theory]
    [InlineData(0x02, 10)]
    [InlineData(0x42, 13)]
    public void SystemsHaveTheirChannelCount(int system, int channels)
    {
        DmfFile read = DmfReader.Read(DmfWriter.Write(EmptySong((DmfSystem)system), SilentNotes()));

        Assert.Equal((DmfSystem)system, read.System);
        Assert.Equal(channels, read.Channels.Count);
    }

    /// <summary>A cell with fewer effects than its channel has columns is padded with empty ones.</summary>
    [Fact]
    public void ShortEffectListsArePadded()
    {
        DmfCell cell = new(1, 4, 5, [new DmfEffect(0x0F, 3)], -1);
        DmfFile file = EmptySong() with
        {
            Channels = [new DmfChannel([0], null, 3, [[cell, Empty()]]), .. EmptySong().Channels.Skip(1)],
        };

        DmfCell read = DmfReader.Read(DmfWriter.Write(file, SilentNotes())).Channels[0].Patterns[0][0];

        Assert.Equal([new DmfEffect(0x0F, 3), DmfEffect.Empty, DmfEffect.Empty], read.Effects);
    }

    /// <summary>Names with characters outside printable ASCII are written with ? and a conversion note each.</summary>
    [Fact]
    public void NonAsciiTextBecomesQuestionMarks()
    {
        DmfFile file = EmptySong() with
        {
            Name = "Café",
            Author = "plain",
            Instruments = [new DmfInstrument("Lösung\t1", null, new DmfStdInstrument(new DmfMacro([], -1), new DmfMacro([], -1), 0, new DmfMacro([], -1), new DmfMacro([], -1)))],
            Samples = [new DmfSample("日本", 0, 5, 50, 16, null, null, [])],
        };

        (byte[] bytes, List<string> notes) = Serialize(file);
        DmfFile read = DmfReader.ReadDecompressed(bytes);

        Assert.Equal("Caf?", read.Name);
        Assert.Equal("plain", read.Author);
        Assert.Equal("L?sung?1", read.Instruments[0].Name);
        Assert.Equal("??", read.Samples[0].Name);
        Assert.Equal(3, notes.Count);
        Assert.Contains(notes, n => n.Contains("Song name", StringComparison.Ordinal) && n.Contains("Café", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Instrument 0 name", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 0 name", StringComparison.Ordinal));
    }

    /// <summary>Text over 255 characters is cut to 255, with a note.</summary>
    [Fact]
    public void LongTextIsCut()
    {
        DmfFile file = EmptySong() with { Name = new string('x', 300) };

        (byte[] bytes, List<string> notes) = Serialize(file);

        Assert.Equal(new string('x', 255), DmfReader.ReadDecompressed(bytes).Name);
        string note = Assert.Single(notes);
        Assert.Contains("Song name", note, StringComparison.Ordinal);
        Assert.Contains("300", note, StringComparison.Ordinal);
    }

    /// <summary>Text the format holds as it is gives no note.</summary>
    [Fact]
    public void PlainTextGivesNoNotes()
    {
        (_, List<string> notes) = Serialize(EmptySong() with { Name = new string('x', 255), Author = " ~ " });

        Assert.Empty(notes);
    }

    /// <summary>Songs the format can't hold are refused with <see cref="ArgumentException"/> naming the problem; nothing is written.</summary>
    [Theory]
    [MemberData(nameof(BadSongNames))]
    public void BadSongsAreRefused(string description)
    {
        (Func<DmfFile, DmfFile> spoil, string message) = BadSongs[description];
        DmfFile bad = spoil(EmptySong());

        ArgumentException error = Assert.Throws<ArgumentException>(() => DmfWriter.Write(bad, SilentNotes()));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    /// <summary>The names of the broken songs, for the theory.</summary>
    public static TheoryData<string> BadSongNames => new(BadSongs.Keys);

    /// <summary>One song broken in each way the writer checks: how to spoil an empty song, and what the error says.</summary>
    private static Dictionary<string, (Func<DmfFile, DmfFile> Spoil, string Message)> BadSongs
    {
        get
        {
            DmfFmInstrument body = DmfBuilder.CountingFm(0);
            DmfInstrument fm = new("fm", body, null);
            DmfStdInstrument std = new(new DmfMacro([], -1), new DmfMacro([], -1), 0, new DmfMacro([], -1), new DmfMacro([], -1));
            return new()
            {
                ["version 27"] = (f => f with { Version = 27 }, "file version 27"),
                ["trailing bytes"] = (f => f with { TrailingBytes = 2 }, "trailing bytes"),
                ["unknown system"] = (f => f with { System = (DmfSystem)0x12 }, "system 18"),
                ["highlight"] = (f => f with { HighlightA = 256 }, "highlight A is 256"),
                ["time base"] = (f => f with { Timing = f.Timing with { TimeBase = -1 } }, "time base"),
                ["tick time 1"] = (f => f with { Timing = f.Timing with { TickTime1 = 0 } }, "tick time 1 is 0"),
                ["tick time 2"] = (f => f with { Timing = f.Timing with { TickTime2 = 256 } }, "tick time 2 is 256"),
                ["custom Hz range"] = (f => f with { Timing = f.Timing with { CustomHz = 1000 } }, "custom Hz is 1000"),
                ["custom Hz missing"] = (f => f with { Timing = f.Timing with { UsingCustomHz = true } }, "no rate"),
                ["rows per pattern"] = (f => f with { RowsPerPattern = 0 }, "rows per pattern is 0"),
                ["channel count"] = (f => f with { Channels = [.. f.Channels.Skip(1)] }, "9 channels"),
                ["no matrix rows"] = (f => f with { Channels = [new DmfChannel([], null, 1, []), .. f.Channels.Skip(1)] }, "0 matrix rows"),
                ["pattern index"] = (f => f with { Channels = [new DmfChannel([128], null, 1, f.Channels[0].Patterns), .. f.Channels.Skip(1)] }, "pattern outside 0–127"),
                ["pattern names"] = (f => f with { Channels = [new DmfChannel([0], [string.Empty], 1, f.Channels[0].Patterns), .. f.Channels.Skip(1)] }, "pattern names"),
                ["effect columns"] = (f => f with { Channels = [new DmfChannel([0], null, 5, f.Channels[0].Patterns), .. f.Channels.Skip(1)] }, "5 effect columns"),
                ["orders differ"] = (f => f with { Channels = [.. f.Channels.Take(1), new DmfChannel([0, 0], null, 1, f.Channels[1].Patterns), .. f.Channels.Skip(2)] }, "channel 2 has 2 matrix rows"),
                ["patterns differ"] = (f => f with { Channels = [new DmfChannel([0], null, 1, []), .. f.Channels.Skip(1)] }, "channel 1 has 0 patterns"),
                ["pattern length"] = (f => f with { Channels = [Column(Empty()), .. f.Channels.Skip(1)] }, "channel 1 matrix row 0 has 1 rows, not 2"),
                ["note"] = (f => f with { Channels = [Column(new DmfCell(13, 0, -1, [], -1), Empty()), .. f.Channels.Skip(1)] }, "note 13"),
                ["empty note with octave"] = (f => f with { Channels = [Column(new DmfCell(0, 3, -1, [], -1), Empty()), .. f.Channels.Skip(1)] }, "empty note with octave 3"),
                ["FM volume"] = (f => f with { Channels = [Column(new DmfCell(1, 3, 0x80, [], -1), Empty()), .. f.Channels.Skip(1)] }, "volume 128"),
                ["PSG volume"] = (f => f with { Channels = [.. f.Channels.Take(6), Column(new DmfCell(1, 3, 0x10, [], -1), Empty()), .. f.Channels.Skip(7)] }, "volume 16"),
                ["too many effects"] = (f => f with { Channels = [Column(new DmfCell(1, 3, -1, [new DmfEffect(1, 1), new DmfEffect(2, 2)], -1), Empty()), .. f.Channels.Skip(1)] }, "2 effects"),
                ["effect value"] = (f => f with { Channels = [Column(new DmfCell(1, 3, -1, [new DmfEffect(1, 256)], -1), Empty()), .. f.Channels.Skip(1)] }, "effect 1,256"),
                ["instrument number"] = (f => f with { Channels = [Column(new DmfCell(1, 3, -1, [], 0), Empty()), .. f.Channels.Skip(1)] }, "instrument 0"),
                ["instrument kind"] = (f => f with { Instruments = [new DmfInstrument("none", null, null)] }, "FM or STD"),
                ["both instrument kinds"] = (f => f with { Instruments = [fm with { Std = std }] }, "FM or STD"),
                ["operator count"] = (f => f with { Instruments = [fm with { Fm = body with { Operators = [] } }] }, "0 operators"),
                ["operator value"] = (f => f with { Instruments = [fm with { Fm = body with { Operators = [.. body.Operators.Take(3), DmfBuilder.Operator(250)] } }] }, "operator value is 256"),
                ["macro length"] = (f => f with { Instruments = [new DmfInstrument("s", null, std with { Volume = new DmfMacro(new int[128], -1) })] }, "128 values"),
                ["macro loop"] = (f => f with { Instruments = [new DmfInstrument("s", null, std with { Arpeggio = new DmfMacro([1, 2], 2) })] }, "loops at 2"),
                ["arpeggio mode"] = (f => f with { Instruments = [new DmfInstrument("s", null, std with { ArpeggioMode = 2 })] }, "arpeggio mode is 2"),
                ["too many instruments"] = (f => f with { Instruments = [.. Enumerable.Repeat(fm, 256)] }, "256 instruments"),
                ["too many wavetables"] = (f => f with { Wavetables = [.. Enumerable.Repeat<IReadOnlyList<int>>([], 256)] }, "256 wavetables"),
                ["too many samples"] = (f => f with { Samples = [.. Enumerable.Repeat(Sample(), 256)] }, "256 samples"),
                ["sample rate code"] = (f => f with { Samples = [Sample() with { RateCode = 6 }] }, "rate code is 6"),
                ["sample pitch"] = (f => f with { Samples = [Sample() with { Pitch = 11 }] }, "pitch is 11"),
                ["sample amp"] = (f => f with { Samples = [Sample() with { Amp = 101 }] }, "amp is 101"),
                ["sample bits"] = (f => f with { Samples = [Sample() with { Bits = 12 }] }, "12 bits"),
                ["sample cut points"] = (f => f with { Samples = [Sample() with { CutStart = 0, CutEnd = 1 }] }, "cut points"),
                ["8-bit range"] = (f => f with { Samples = [Sample() with { Bits = 8, Data = [-1] }] }, "outside 0–255"),
            };
        }
    }

    private static DmfSample Sample() => new("s", 0, 5, 50, 16, null, null, [1, 2, 3]);
}
