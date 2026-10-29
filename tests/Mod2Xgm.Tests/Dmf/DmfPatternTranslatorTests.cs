using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Edge cases of <see cref="DmfPatternTranslator"/> and <see cref="DmfSongBuilder"/> (plan T5.5, ADR 0044): what happens where a
/// jump lands, when a cell needs more than four effects, at DMF's limits (144 DAC notes, 127 orders, 255 ticks, octaves
/// 0–7), and with looped samples on the DAC. Plans are made by the real pipeline and, where a MOD can't express the case,
/// edited before timing.
/// </summary>
public sealed class DmfPatternTranslatorTests
{
    private const int C2 = 428;
    private const int Lead = 1;
    private const int Drum = 2;
    private const int Long = 3;
    private const int Hiss = 4;
    private const int LeadCycle = 32;

    private static ModBuilder Song() =>
        new ModBuilder()
            .WithSample(Lead, "lead", LeadCycle, loopStart: 0, loopLength: LeadCycle, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / LeadCycle)))
            .WithSample(Drum, "drum", 4000, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 40) * Math.Exp(-i / 800.0)))
            .WithSample(Long, "long", 8000, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 40)))
            .WithSample(Hiss, "hiss", HissData.Length, data: i => HissData[i]);

    /// <summary>White noise from a fixed seed: no pitch to measure.</summary>
    private static readonly sbyte[] HissData = [.. new Random(11).GetItems<sbyte>([-100, -50, 50, 100], 4000)];

    /// <summary>The plan with the cell of <paramref name="channel"/> at <paramref name="row"/> of order 0 changed.</summary>
    private static Func<GenesisPlan, GenesisPlan> Edit(GenesisChannel channel, int row, Func<PlanCell, PlanCell> change) => plan =>
    {
        List<PlanRow> rows = [.. plan.Patterns[0]];
        List<PlanCell> cells = [.. rows[row].Cells];
        cells[(int)channel] = change(cells[(int)channel]);
        rows[row] = rows[row] with { Cells = cells };
        return plan with { Patterns = [rows, .. plan.Patterns.Skip(1)] };
    };

    /// <summary>
    /// The song loops back to row 0 with a vibrato running from its last row; DMF would keep it running, so row 0 stops it,
    /// and restates the pan, which can't be known after a jump.
    /// </summary>
    [Fact]
    public void WhereAJumpLandsRunningEffectsAreStopped()
    {
        DmfConversion.Result result = DmfConversion.Convert(Song()
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 63, 0, 0, 0, 0x4, 0x48)
            .Build());

        Assert.Contains(result.Effects(GenesisChannel.Fm1, 63), e => e is { Code: 0x04, Value: > 0 });
        Assert.Contains((0x04, 0x00), result.Effects(GenesisChannel.Fm1, 0));
        Assert.Contains((0x08, 0x10), result.Effects(GenesisChannel.Fm1, 0));
    }

    /// <summary>A cell needing more than four effects keeps the most important four and says how many went.</summary>
    [Fact]
    public void ACellKeepsItsFourMostImportantEffects()
    {
        Effect[] many = [new Arpeggio(3, 7), new Retrigger(2), new NoteCut(4), new Tremolo(4 / 64.0, 0.25), new SetVibratoWaveform(Waveform.Square, false)];
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            transform: Edit(GenesisChannel.Fm1, 0, cell => cell with { Effects = many }));

        List<(int Code, int Value)> kept = result.Effects(GenesisChannel.Fm1, 0);
        Assert.Equal(4, kept.Count);
        Assert.Contains((0x08, 0x10), kept);
        Assert.Contains((0x00, 0x37), kept);
        Assert.DoesNotContain(kept, e => e.Code == 0xE3);
        Assert.Contains(result.Notes, n => n.Contains("more than four", StringComparison.Ordinal));
    }

    /// <summary>The vibrato waveform (<c>E4x</c>) becomes DefleMask's shape effect <c>E3xx</c>; a pan effect sets the side.</summary>
    [Fact]
    public void VibratoShapeAndPanAreTranslated()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            transform: plan => Edit(GenesisChannel.Fm1, 2, cell => cell with { Effects = [new SetVibratoWaveform(Waveform.RampDown, false), new SetPan(1)], Source = 0 })(plan));

        Assert.Contains((0xE3, 5), result.Effects(GenesisChannel.Fm1, 2));
        Assert.Contains((0x08, 0x01), result.Effects(GenesisChannel.Fm1, 2));
    }

    /// <summary>Effects with nothing playing, and effects with no DMF equivalent, are counted in notes.</summary>
    [Fact]
    public void LeftOutEffectsAreCounted()
    {
        Effect[] untranslatable = [new SetGlissando(true), new SetTremoloWaveform(Waveform.Square, false), new InvertLoop(3), new UnsupportedEffect("E8x with x = 3"), new SampleOffset(256)];
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            transform: plan => Edit(GenesisChannel.Fm1, 1, cell => cell with { Effects = untranslatable, Source = 0 })(
                Edit(GenesisChannel.Fm2, 1, cell => cell with { Effects = [new Retrigger(2)], Source = 1 })(plan)));

        Assert.Contains(result.Notes, n => n.Contains("glissando (E3x)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("tremolo waveform (E7x)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("invert loop (EFx)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("unsupported command (E8x with x = 3)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("sample offset (9xx)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("rows where their channel plays nothing", StringComparison.Ordinal));
    }

    /// <summary>A pattern delay longer than 255 ticks is cut to 255; a jump past the song's end goes to order 0.</summary>
    [Fact]
    public void FlowLimitsGetNotes()
    {
        DmfConversion.Result result = DmfConversion.Convert(Song()
            .WithCell(0, 0, 0, Lead, C2, 0xF, 0x1F)
            .WithCell(0, 2, 0, 0, 0, 0xE, 0xEF)
            .WithCell(0, 4, 0, 0, 0, 0xB, 0x30)
            .Build());

        Assert.Contains((0x09, 255), result.RowEffects(2));
        Assert.Contains((0x0B, 0), result.RowEffects(4));
        Assert.Contains(result.Notes, n => n.Contains("at most 255 ticks", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("past the song's end", StringComparison.Ordinal));
    }

    /// <summary>An FM note above octave 7 is moved down by octaves, with a note.</summary>
    [Fact]
    public void FmNotesStayWithinDefleMasksOctaves()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            transform: Edit(GenesisChannel.Fm1, 0, cell => cell with { Note = new NotePitch(48, 0, 8287 * 64) }));

        Assert.InRange(result.Cell(GenesisChannel.Fm1, 0).Octave, 0, 7);
        Assert.Contains(result.Notes, n => n.Contains("octaves 0–7", StringComparison.Ordinal));
    }

    /// <summary>A note with no measurable pitch is placed two octaves above its name: ProTracker C-2 is C-4.</summary>
    [Fact]
    public void WithoutAPitchFmNotesMoveTwoOctaves()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Hiss, C2).Build(),
            MappingOptions.Default with { RequireFm = new HashSet<int> { Hiss - 1 } });

        Assert.Contains(result.Notes, n => n.Contains("no clear pitch found", StringComparison.Ordinal));
    }

    /// <summary>FM6 playing FM after the DAC is switched back with <c>1700</c>.</summary>
    [Fact]
    public void Fm6SwitchesBackFromTheDac()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Drum, C2).WithCell(0, 4, 0, Lead, C2).Build(),
            transform: plan => Edit(GenesisChannel.Fm6, 8, _ => plan.Patterns[0][4].Cells[(int)GenesisChannel.Fm1])(plan));

        Assert.Contains((0x17, 0x01), result.Effects(GenesisChannel.Fm6, 0));
        Assert.Contains((0x17, 0x00), result.Effects(GenesisChannel.Fm6, 8));
    }

    /// <summary>A DAC note started past its sample's end, or quieter than −45 dB, is silent: a note off.</summary>
    [Fact]
    public void SilentDacNotesAreNoteOffs()
    {
        DmfConversion.Result result = DmfConversion.Convert(Song()
            .WithCell(0, 0, 0, Drum, C2)
            .WithCell(0, 2, 0, Drum, C2, 0x9, 0x40)
            .WithCell(0, 4, 0, Drum, C2, 0xC)
            .Build());

        Assert.Equal(100, result.Cell(GenesisChannel.Fm6, 2).Note);
        Assert.Equal(100, result.Cell(GenesisChannel.Fm6, 4).Note);
        Assert.Contains(result.Notes, n => n.Contains("start past their sample's end", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("quieter than −45 dB", StringComparison.Ordinal));
    }

    /// <summary>
    /// Past 144 copies a DAC note plays the nearest copy of its sample, and a sample with no copy yet is left out; both
    /// get a note.
    /// </summary>
    [Fact]
    public void Past144CopiesTheNearestIsUsed()
    {
        // 15 start points × 11 loudnesses (64, 45, 32 … 2: one 3 dB step apart) = 165 copies of the drum. A row sets the
        // volume (Cxx) and the next plays the drum without an instrument, which keeps it, from a start point (9xx).
        int[] volumes = [64, 45, 32, 23, 16, 11, 8, 6, 4, 3, 2];
        ModBuilder mod = Song().WithOrders(0, 1, 2, 3, 4, 5).WithCell(0, 0, 0, Drum, C2);
        int at = 1;
        for (int offset = 0; offset < 15; offset++)
        {
            foreach (int volume in volumes)
            {
                mod.WithCell(at / 64, at % 64, 0, 0, 0, 0xC, volume);
                at++;
                mod.WithCell(at / 64, at % 64, 0, 0, C2, offset == 0 ? 0 : 0x9, offset);
                at++;
            }
        }

        mod.WithCell(5, 60, 0, Long, C2);
        DmfConversion.Result result = DmfConversion.Convert(mod.Build());

        Assert.Equal(DmfPatternTranslator.MaxDacNotes, result.File.Samples.Count);
        Assert.Equal(100, result.Cell(GenesisChannel.Fm6, 60, order: 5).Note);
        Assert.Contains(result.Notes, n => n.Contains("nearest existing copy", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("DAC notes were left out", StringComparison.Ordinal));
    }

    /// <summary>A looped sample on the DAC (<c>--require-dac</c>) is played out as long as its longest note: here the whole song.</summary>
    [Fact]
    public void LoopedDacSamplesArePlayedOutForTheirLongestNote()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            MappingOptions.Default with { RequireDac = new HashSet<int> { 0 } });

        DmfSample sample = Assert.Single(result.File.Samples);
        double seconds = sample.Data.Count / (double)sample.RateHz;
        Assert.InRange(seconds, 7.6, 7.8);
    }

    /// <summary>A looped DAC note held longer than 10 s is cut to 10 s, with a note.</summary>
    [Fact]
    public void LongLoopedDacNotesAreCut()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithOrders(0, 1).WithCell(0, 0, 0, Lead, C2).Build(),
            MappingOptions.Default with { RequireDac = new HashSet<int> { 0 } });

        DmfSample sample = Assert.Single(result.File.Samples);
        Assert.InRange(sample.Data.Count / (double)sample.RateHz, 9.9, 10.1);
        Assert.Contains(result.Notes, n => n.Contains("holds its loop for", StringComparison.Ordinal));
    }

    /// <summary>A song longer than DMF's 127 orders keeps the first 127, with a note.</summary>
    [Fact]
    public void MoreThan127OrdersAreCut()
    {
        DmfConversion.Result result = DmfConversion.Convert(
            Song().WithOrders([.. Enumerable.Repeat(0, 128)]).WithCell(0, 0, 0, Lead, C2).Build());

        Assert.Equal(DmfSongBuilder.MaxMatrixRows, result.File.MatrixRows);
        Assert.Contains(result.Notes, n => n.Contains("DMF holds 127", StringComparison.Ordinal));
    }

    /// <summary>A plan without orders can't be written.</summary>
    [Fact]
    public void APlanWithoutOrdersIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DmfConversion.Convert(
            Song().WithCell(0, 0, 0, Lead, C2).Build(),
            transform: plan => plan with { Patterns = [] }));
    }

    /// <summary>The DMF ticks twice as fast as a 33 BPM MOD, so arpeggio steps are slowed with <c>E0xx</c> and tick counts doubled.</summary>
    [Fact]
    public void AFasterDmfScalesTickEffects()
    {
        DmfConversion.Result result = DmfConversion.Convert(Song()
            .WithCell(0, 0, 0, Lead, C2, 0xF, 33)
            .WithCell(0, 1, 0, 0, 0, 0xE, 0xC2)
            .Build());

        (int _, int factor) = result.RowEffects(0).Single(e => e.Code == 0xE0);
        Assert.True(factor > 1);
        Assert.Contains((0xEC, 2 * factor), result.Effects(GenesisChannel.Fm1, 1));
    }
}
