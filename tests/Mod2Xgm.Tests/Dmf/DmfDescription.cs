using System.Globalization;
using System.Text;
using Mod2Xgm.Dmf;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Writes the song a <see cref="DmfFile"/> holds as text, one fact per line, leaving out what only says how the file
/// was saved: the file version, pattern names, sample cut points, trailing bytes, and the custom Hz bytes while custom
/// Hz is off. Two files with equal descriptions play the same song (plan T4.3: our probes vs DefleMask's "Save As").
/// </summary>
internal static class DmfDescription
{
    /// <summary>The song in <paramref name="file"/>, as text.</summary>
    public static string Describe(DmfFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        StringBuilder text = new();
        Line(text, $"system {file.System}, {file.Channels.Count} channels");
        Line(text, $"name \"{file.Name}\", author \"{file.Author}\", highlights {file.HighlightA}/{file.HighlightB}");
        DmfTiming timing = file.Timing;
        string customHz = timing.UsingCustomHz ? string.Create(CultureInfo.InvariantCulture, $"{timing.CustomHz} Hz") : "off";
        Line(text, $"time base {timing.TimeBase}, tick times {timing.TickTime1}/{timing.TickTime2}, {(timing.Ntsc ? "NTSC" : "PAL")}, custom Hz {customHz}");
        Line(text, $"{file.MatrixRows} matrix rows of {file.RowsPerPattern} rows");

        for (int i = 0; i < file.Instruments.Count; i++)
        {
            DescribeInstrument(text, i, file.Instruments[i]);
        }

        for (int i = 0; i < file.Wavetables.Count; i++)
        {
            Line(text, $"wavetable {i}: {string.Join(' ', file.Wavetables[i])}");
        }

        for (int channel = 0; channel < file.Channels.Count; channel++)
        {
            DmfChannel column = file.Channels[channel];
            Line(text, $"channel {channel + 1}: orders [{string.Join(' ', column.Orders)}], effect columns {column.EffectColumns}");
            for (int matrixRow = 0; matrixRow < column.Patterns.Count; matrixRow++)
            {
                for (int row = 0; row < column.Patterns[matrixRow].Count; row++)
                {
                    DmfCell cell = column.Patterns[matrixRow][row];
                    if (!cell.IsEmpty)
                    {
                        string effects = string.Join(' ', cell.Effects.Where(e => !e.IsEmpty).Select(e => $"{e.Code:X2}{e.Value:X2}"));
                        Line(text, $"  {matrixRow}:{row:D2} note {cell.Note} octave {cell.Octave} volume {cell.Volume} instrument {cell.Instrument} effects [{effects}]");
                    }
                }
            }
        }

        for (int i = 0; i < file.Samples.Count; i++)
        {
            DmfSample sample = file.Samples[i];
            Line(text, $"sample {i} \"{sample.Name}\": rate code {sample.RateCode}, pitch {sample.Pitch}, amp {sample.Amp}, {sample.Bits} bits, {sample.Data.Count} values, checksum {Checksum(sample.Data)}");
        }

        return text.ToString();
    }

    private static void DescribeInstrument(StringBuilder text, int index, DmfInstrument instrument)
    {
        if (instrument.Fm is { } fm)
        {
            Line(text, $"instrument {index} \"{instrument.Name}\" FM: ALG {fm.Algorithm} FB {fm.Feedback} FMS {fm.Fms} AMS {fm.Ams}");
            foreach (DmfOperator op in fm.Operators)
            {
                Line(text, $"  {op}");
            }
        }
        else if (instrument.Std is { } std)
        {
            Line(text, $"instrument {index} \"{instrument.Name}\" STD: arpeggio mode {std.ArpeggioMode}");
            Line(text, $"  volume {Macro(std.Volume)}; arpeggio {Macro(std.Arpeggio)}; duty/noise {Macro(std.DutyNoise)}; wavetable {Macro(std.Wavetable)}");
        }
    }

    private static string Macro(DmfMacro macro) => $"[{string.Join(' ', macro.Values)}] loop {macro.Loop}";

    /// <summary>A short hash of the sample values, so a description stays small but still shows changed sound.</summary>
    private static string Checksum(IReadOnlyList<short> data)
    {
        uint hash = 2166136261;
        foreach (short value in data)
        {
            hash = (hash ^ (ushort)value) * 16777619;
        }

        return hash.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(FormattableString.Invariant(line)).Append('\n');
}
