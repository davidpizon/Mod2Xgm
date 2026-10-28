using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Mod2Xgm.Dmf;

/// <summary>
/// Writes a <see cref="DmfFile"/> as a DefleMask module (plan T5.2, docs/formats/dmf.md): file version 24, every
/// section in spec order, the whole thing zlib-compressed. The writer stores exactly the song it is given, one pattern
/// per matrix row per channel (the model already holds the patterns that way), and checks every value against the
/// format's limits first: a <see cref="DmfFile"/> that breaks one is a bug in whoever built it, so it throws
/// <see cref="ArgumentException"/> naming the field, instead of writing a file DefleMask would refuse. The only
/// changes it makes itself are to text (names, authors): characters DMF can't hold become <c>?</c> and over-long
/// text is cut, each with a conversion note.
/// </summary>
internal static class DmfWriter
{
    /// <summary>The 16 bytes every DMF starts with (docs/formats/dmf.md#1-format-flags).</summary>
    public const string Magic = ".DelekDefleMask.";

    /// <summary>The file version written: DefleMask 1.0, which has no pattern names and no sample cut points.</summary>
    public const int FileVersion = 24;

    /// <summary>Longest name or author: the length is one byte (docs/formats/dmf.md#container).</summary>
    private const int MaxTextLength = 255;

    /// <summary>Highest pattern index: DefleMask and Furnace reject more (docs/formats/dmf.md#5-pattern-matrix).</summary>
    private const int MaxPatternIndex = 0x7F;

    /// <summary>Highest matrix row count: Furnace rejects more (docs/formats/dmf.md#4-module-information).</summary>
    private const int MaxMatrixRows = 127;

    /// <summary>Most rows in one pattern: Furnace rejects more (docs/formats/dmf.md#4-module-information).</summary>
    private const int MaxRowsPerPattern = 256;

    /// <summary>Note value for "note off" (docs/formats/dmf.md#8-patterns).</summary>
    private const int NoteOff = 100;

    /// <summary>Highest of the 12 note values (12 = C of the octave above).</summary>
    private const int MaxNote = 12;

    /// <summary>Highest FM channel volume; the PSG's is <see cref="MaxPsgVolume"/> (docs/formats/dmf.md#8-patterns).</summary>
    private const int MaxFmVolume = 0x7F;

    /// <summary>Highest PSG channel volume.</summary>
    private const int MaxPsgVolume = 0xF;

    /// <summary>Highest custom Hz value: it is stored as 3 decimal digits (docs/formats/dmf.md#4-module-information).</summary>
    private const int MaxCustomHz = 999;

    /// <summary>Longest macro: the size is one byte, but DefleMask allows 127 (docs/formats/dmf.md#6-instruments).</summary>
    private const int MaxMacroLength = 127;

    /// <summary>Highest count of instruments, wavetables or samples: the count is one byte.</summary>
    private const int MaxCount = 255;

    /// <summary>Highest sample amp (docs/formats/dmf.md#9-pcm-samples).</summary>
    private const int MaxSampleAmp = 100;

    /// <summary>Highest sample pitch (docs/formats/dmf.md#9-pcm-samples).</summary>
    private const int MaxSamplePitch = 10;

    /// <summary>Highest sample rate code (docs/formats/dmf.md#9-pcm-samples).</summary>
    private const int MaxSampleRateCode = 5;

    /// <summary>Operators in an FM patch.</summary>
    private const int OperatorCount = 4;

    /// <summary>Writes <paramref name="file"/> as the bytes of a .dmf file: zlib-compressed.</summary>
    /// <param name="file">The song. Must be file version 24 with no pattern names, cut points or trailing bytes.</param>
    /// <param name="notes">Collects a note for each text change.</param>
    /// <exception cref="ArgumentException">The song breaks one of the format's limits.</exception>
    public static byte[] Write(DmfFile file, ConversionNotes notes)
    {
        using MemoryStream stream = new();
        using (ZLibStream zlib = new(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Serialize(file, notes));
        }

        return stream.ToArray();
    }

    /// <summary>The file's contents before compression; <see cref="Write"/> compresses these.</summary>
    internal static byte[] Serialize(DmfFile file, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(notes);
        Validate(file);

        Output output = new(notes);
        output.Ascii(Magic);
        output.Byte(FileVersion);
        output.Byte((int)file.System);
        output.Text("Song name", file.Name);
        output.Text("Song author", file.Author);
        output.Byte(file.HighlightA);
        output.Byte(file.HighlightB);
        WriteTiming(output, file.Timing);
        output.Int32(file.RowsPerPattern);
        output.Byte(file.MatrixRows);

        foreach (DmfChannel channel in file.Channels)
        {
            foreach (int pattern in channel.Orders)
            {
                output.Byte(pattern);
            }
        }

        output.Byte(file.Instruments.Count);
        for (int i = 0; i < file.Instruments.Count; i++)
        {
            WriteInstrument(output, i, file.Instruments[i]);
        }

        output.Byte(file.Wavetables.Count);
        foreach (IReadOnlyList<int> wavetable in file.Wavetables)
        {
            output.Int32(wavetable.Count);
            foreach (int value in wavetable)
            {
                output.Int32(value);
            }
        }

        foreach (DmfChannel channel in file.Channels)
        {
            output.Byte(channel.EffectColumns);
            foreach (IReadOnlyList<DmfCell> pattern in channel.Patterns)
            {
                foreach (DmfCell cell in pattern)
                {
                    WriteCell(output, cell, channel.EffectColumns);
                }
            }
        }

        output.Byte(file.Samples.Count);
        for (int i = 0; i < file.Samples.Count; i++)
        {
            WriteSample(output, i, file.Samples[i]);
        }

        return output.ToArray();
    }

    /// <summary>Time base, tick times, frames mode, custom Hz and its three ASCII digits.</summary>
    private static void WriteTiming(Output output, DmfTiming timing)
    {
        output.Byte(timing.TimeBase);
        output.Byte(timing.TickTime1);
        output.Byte(timing.TickTime2);
        output.Byte(timing.Ntsc ? 1 : 0);
        output.Byte(timing.UsingCustomHz ? 1 : 0);

        // The digits are written even while custom Hz is off; then they just repeat the PAL/NTSC rate.
        int hz = timing.CustomHz ?? (timing.Ntsc ? 60 : 50);
        string digits = hz.ToString(CultureInfo.InvariantCulture);
        output.Ascii(digits);
        for (int i = digits.Length; i < 3; i++)
        {
            output.Byte(0);
        }
    }

    private static void WriteInstrument(Output output, int index, DmfInstrument instrument)
    {
        output.Text($"Instrument {index} name", instrument.Name);
        if (instrument.Fm is { } fm)
        {
            output.Byte(1);
            output.Byte(fm.Algorithm);
            output.Byte(fm.Feedback);
            output.Byte(fm.Fms);
            output.Byte(fm.Ams);
            foreach (DmfOperator op in fm.Operators)
            {
                output.Byte(op.Am);
                output.Byte(op.Ar);
                output.Byte(op.Dr);
                output.Byte(op.Mult);
                output.Byte(op.Rr);
                output.Byte(op.Sl);
                output.Byte(op.Tl);
                output.Byte(op.Dt2);
                output.Byte(op.Rs);
                output.Byte(op.Dt);
                output.Byte(op.D2R);
                output.Byte(op.SsgMode);
            }

            return;
        }

        DmfStdInstrument std = instrument.Std!;
        output.Byte(0);
        WriteMacro(output, std.Volume);
        WriteMacro(output, std.Arpeggio);
        output.Byte(std.ArpeggioMode);
        WriteMacro(output, std.DutyNoise);
        WriteMacro(output, std.Wavetable);
    }

    /// <summary>Size byte, 4-byte values, and the loop byte only when there are values.</summary>
    private static void WriteMacro(Output output, DmfMacro macro)
    {
        output.Byte(macro.Values.Count);
        foreach (int value in macro.Values)
        {
            output.Int32(value);
        }

        if (macro.Values.Count > 0)
        {
            output.Byte((byte)(sbyte)macro.Loop);
        }
    }

    /// <summary>One row: note, octave, volume, the channel's effect columns (empty ones padded), instrument.</summary>
    private static void WriteCell(Output output, DmfCell cell, int effectColumns)
    {
        output.Int16(cell.Note);
        output.Int16(cell.Octave);
        output.Int16(cell.Volume);
        for (int i = 0; i < effectColumns; i++)
        {
            DmfEffect effect = i < cell.Effects.Count ? cell.Effects[i] : DmfEffect.Empty;
            output.Int16(effect.Code);
            output.Int16(effect.Value);
        }

        output.Int16(cell.Instrument);
    }

    private static void WriteSample(Output output, int index, DmfSample sample)
    {
        output.Int32(sample.Data.Count);
        output.Text($"Sample {index} name", sample.Name);
        output.Byte(sample.RateCode);
        output.Byte(sample.Pitch);
        output.Byte(sample.Amp);
        output.Byte(sample.Bits);
        foreach (short value in sample.Data)
        {
            output.Int16(value);
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> for the first value the format can't hold.</summary>
    private static void Validate(DmfFile file)
    {
        Check(file.Version == FileVersion, $"file version {file.Version}; only version {FileVersion} is written");
        Check(file.TrailingBytes == 0, $"{file.TrailingBytes} trailing bytes; the writer leaves none");
        Check(Enum.IsDefined(file.System), $"system {(int)file.System} isn't Genesis (0x02) or Genesis with extended channel 3 (0x42)");
        CheckByte("highlight A", file.HighlightA);
        CheckByte("highlight B", file.HighlightB);
        ValidateTiming(file.Timing);
        Check(file.RowsPerPattern is >= 1 and <= MaxRowsPerPattern, $"rows per pattern is {file.RowsPerPattern}, not 1–{MaxRowsPerPattern}");
        Check(file.Channels.Count == DmfSystems.ChannelCount(file.System), $"{file.Channels.Count} channels; {file.System} has {DmfSystems.ChannelCount(file.System)}");
        Check(file.Channels[0].Orders.Count is >= 1 and <= MaxMatrixRows, $"{file.Channels[0].Orders.Count} matrix rows, not 1–{MaxMatrixRows}");

        Check(file.Instruments.Count <= MaxCount, $"{file.Instruments.Count} instruments; at most {MaxCount}");
        for (int i = 0; i < file.Instruments.Count; i++)
        {
            ValidateInstrument(i, file.Instruments[i]);
        }

        Check(file.Wavetables.Count <= MaxCount, $"{file.Wavetables.Count} wavetables; at most {MaxCount}");
        for (int channel = 0; channel < file.Channels.Count; channel++)
        {
            ValidateChannel(file, channel);
        }

        Check(file.Samples.Count <= MaxCount, $"{file.Samples.Count} samples; at most {MaxCount}");
        for (int i = 0; i < file.Samples.Count; i++)
        {
            ValidateSample(i, file.Samples[i]);
        }
    }

    private static void ValidateTiming(DmfTiming timing)
    {
        CheckByte("time base", timing.TimeBase);
        Check(timing.TickTime1 is >= 1 and <= byte.MaxValue, $"tick time 1 is {timing.TickTime1}, not 1–255");
        Check(timing.TickTime2 is >= 1 and <= byte.MaxValue, $"tick time 2 is {timing.TickTime2}, not 1–255");
        Check(timing.CustomHz is null or (>= 1 and <= MaxCustomHz), $"custom Hz is {timing.CustomHz}, not 1–{MaxCustomHz}");
        Check(!timing.UsingCustomHz || timing.CustomHz is not null, "custom Hz is on, but no rate is given");
    }

    private static void ValidateInstrument(int index, DmfInstrument instrument)
    {
        Check((instrument.Fm is null) != (instrument.Std is null), $"instrument {index} must be FM or STD, exactly one");
        if (instrument.Fm is { } fm)
        {
            CheckByte($"instrument {index} ALG", fm.Algorithm);
            CheckByte($"instrument {index} FB", fm.Feedback);
            CheckByte($"instrument {index} FMS", fm.Fms);
            CheckByte($"instrument {index} AMS", fm.Ams);
            Check(fm.Operators.Count == OperatorCount, $"instrument {index} has {fm.Operators.Count} operators, not {OperatorCount}");
            foreach (DmfOperator op in fm.Operators)
            {
                foreach (int value in (int[])[op.Am, op.Ar, op.Dr, op.Mult, op.Rr, op.Sl, op.Tl, op.Dt2, op.Rs, op.Dt, op.D2R, op.SsgMode])
                {
                    CheckByte($"instrument {index} operator value", value);
                }
            }

            return;
        }

        DmfStdInstrument std = instrument.Std!;
        ValidateMacro(index, "volume", std.Volume);
        ValidateMacro(index, "arpeggio", std.Arpeggio);
        Check(std.ArpeggioMode is 0 or 1, $"instrument {index} arpeggio mode is {std.ArpeggioMode}, not 0 or 1");
        ValidateMacro(index, "duty/noise", std.DutyNoise);
        ValidateMacro(index, "wavetable", std.Wavetable);
    }

    private static void ValidateMacro(int instrument, string name, DmfMacro macro)
    {
        Check(macro.Values.Count <= MaxMacroLength, $"instrument {instrument} {name} macro has {macro.Values.Count} values; at most {MaxMacroLength}");
        Check(
            macro.Values.Count == 0 || (macro.Loop >= -1 && macro.Loop < macro.Values.Count),
            $"instrument {instrument} {name} macro loops at {macro.Loop}, outside its {macro.Values.Count} values");
    }

    private static void ValidateChannel(DmfFile file, int channel)
    {
        DmfChannel column = file.Channels[channel];
        string name = $"channel {channel + 1}";
        Check(column.PatternNames is null, $"{name} has pattern names; version {FileVersion} stores none");
        Check(column.EffectColumns is >= 1 and <= 4, $"{name} has {column.EffectColumns} effect columns, not 1–4");
        Check(column.Orders.Count == file.MatrixRows, $"{name} has {column.Orders.Count} matrix rows, not {file.MatrixRows}");
        Check(column.Patterns.Count == file.MatrixRows, $"{name} has {column.Patterns.Count} patterns, not one for each of the {file.MatrixRows} matrix rows");
        Check(column.Orders.All(o => o is >= 0 and <= MaxPatternIndex), $"{name} names a pattern outside 0–{MaxPatternIndex}");

        int maxVolume = DmfSystems.IsPsg(file.System, channel) ? MaxPsgVolume : MaxFmVolume;
        for (int matrixRow = 0; matrixRow < column.Patterns.Count; matrixRow++)
        {
            IReadOnlyList<DmfCell> pattern = column.Patterns[matrixRow];
            Check(pattern.Count == file.RowsPerPattern, $"{name} matrix row {matrixRow} has {pattern.Count} rows, not {file.RowsPerPattern}");
            for (int row = 0; row < pattern.Count; row++)
            {
                ValidateCell(pattern[row], column.EffectColumns, maxVolume, file.Instruments.Count, $"{name} matrix row {matrixRow} row {row}");
            }
        }
    }

    private static void ValidateCell(DmfCell cell, int effectColumns, int maxVolume, int instrumentCount, string where)
    {
        Check(cell.Note is (>= 0 and <= MaxNote) or NoteOff, $"{where}: note {cell.Note} isn't 0–{MaxNote} or {NoteOff} (off)");
        Check(cell.Note != 0 || cell.Octave == 0, $"{where}: empty note with octave {cell.Octave}");
        Check(cell.Octave is >= short.MinValue and <= short.MaxValue, $"{where}: octave {cell.Octave} doesn't fit 16 bits");
        Check(cell.Volume >= -1 && cell.Volume <= maxVolume, $"{where}: volume {cell.Volume} isn't −1 or 0–{maxVolume:X}");
        Check(cell.Effects.Count <= effectColumns, $"{where}: {cell.Effects.Count} effects, but the channel has {effectColumns} effect columns");
        foreach (DmfEffect effect in cell.Effects)
        {
            Check(effect.Code is >= -1 and <= 0xFF && effect.Value is >= -1 and <= 0xFF, $"{where}: effect {effect.Code},{effect.Value} isn't −1 or 0–FF");
        }

        Check(cell.Instrument >= -1 && cell.Instrument < instrumentCount, $"{where}: instrument {cell.Instrument} isn't −1 or one of the {instrumentCount} instruments");
    }

    private static void ValidateSample(int index, DmfSample sample)
    {
        Check(sample.RateCode is >= 0 and <= MaxSampleRateCode, $"sample {index} rate code is {sample.RateCode}, not 0–{MaxSampleRateCode}");
        Check(sample.Pitch is >= 0 and <= MaxSamplePitch, $"sample {index} pitch is {sample.Pitch}, not 0–{MaxSamplePitch}");
        Check(sample.Amp is >= 0 and <= MaxSampleAmp, $"sample {index} amp is {sample.Amp}, not 0–{MaxSampleAmp}");
        Check(sample.Bits is 8 or 16, $"sample {index} has {sample.Bits} bits, not 8 or 16");
        Check(sample.CutStart is null && sample.CutEnd is null, $"sample {index} has cut points; version {FileVersion} stores none");
        Check(sample.Bits == 16 || sample.Data.All(v => v is >= 0 and <= byte.MaxValue), $"sample {index} is 8-bit but holds values outside 0–255");
    }

    private static void CheckByte(string field, int value) =>
        Check(value is >= 0 and <= byte.MaxValue, $"{field} is {value}, not 0–255");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException($"DMF song can't be written: {message}.", "file");
        }
    }

    /// <summary>The bytes being written, little-endian, with text limited to what DMF holds.</summary>
    private sealed class Output(ConversionNotes notes)
    {
        private readonly MemoryStream _stream = new();

        /// <summary>One byte; <paramref name="value"/> was checked to fit.</summary>
        public void Byte(int value) => _stream.WriteByte((byte)value);

        /// <summary>A signed 16-bit little-endian field.</summary>
        public void Int16(int value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)value);
            _stream.Write(bytes);
        }

        /// <summary>A signed 32-bit little-endian field.</summary>
        public void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        /// <summary>Characters with no length byte (the format string, the custom Hz digits).</summary>
        public void Ascii(string text) => _stream.Write(Encoding.ASCII.GetBytes(text));

        /// <summary>
        /// 1-byte length, then ASCII characters (docs/formats/dmf.md#container): anything else becomes <c>?</c> and
        /// text over 255 characters is cut, each with a conversion note.
        /// </summary>
        public void Text(string field, string text)
        {
            if (text.Length > MaxTextLength)
            {
                notes.Add("{Field} is {Length} characters; DMF holds {Max}, so it is cut", field, text.Length, MaxTextLength);
                text = text[..MaxTextLength];
            }

            bool changed = false;
            byte[] bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is >= ' ' and <= '~')
                {
                    bytes[i] = (byte)c;
                }
                else
                {
                    bytes[i] = (byte)'?';
                    changed = true;
                }
            }

            if (changed)
            {
                notes.Add("{Field} {Text} has characters DMF can't hold; written with ? in their place", field, text);
            }

            Byte(bytes.Length);
            _stream.Write(bytes);
        }

        /// <summary>Everything written so far.</summary>
        public byte[] ToArray() => _stream.ToArray();
    }
}
