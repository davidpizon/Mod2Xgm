using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Harness;

/// <summary>A renderer that can play any DMF file, which stems need (docs/adr/0051-instrument-balance-metric.md).</summary>
internal interface IDmfRenderer
{
    /// <summary>Renders <paramref name="dmfPath"/> once through, by way of the WAV file <paramref name="wavPath"/>.</summary>
    StereoAudio RenderDmf(string dmfPath, string wavPath);
}

/// <summary>
/// Makes stems: one MOD sample alone, from the original and from the conversion (docs/adr/0051-instrument-balance-metric.md).
/// The original's stem is the MOD with every other sample's data zeroed, so its patterns and effects are untouched. The
/// conversion's stem is the converter's own plan with every other sample's notes turned into note-offs, so routing,
/// clashes and dropped notes are those of the real conversion; it is cut after the orders the clip reaches, so Furnace
/// renders seconds rather than the whole song.
/// </summary>
internal sealed class StemSource
{
    private readonly byte[] _mod;
    private readonly ModModule _module;
    private readonly Song _song;
    private readonly GenesisPlan _plan;
    private readonly DmfTimingPlan _timing;
    private readonly DmfBuildSettings _settings;

    private StemSource(byte[] mod, ModModule module, Song song, GenesisPlan plan, DmfTimingPlan timing, DmfBuildSettings settings)
    {
        _mod = mod;
        _module = module;
        _song = song;
        _plan = plan;
        _timing = timing;
        _settings = settings;
    }

    /// <summary>Orders in the plan (one per entry of the order list).</summary>
    public int Orders => _plan.Patterns.Count;

    /// <summary>
    /// Runs the converter's steps on <paramref name="mod"/> with the default options, as <c>mod2xgm</c> does, and checks
    /// that they give exactly <paramref name="converted"/> (the DMF the conversion wrote), so stems come from the same plan.
    /// </summary>
    /// <param name="mod">The MOD file.</param>
    /// <param name="converted">The DMF the default conversion wrote.</param>
    public static StemSource Prepare(byte[] mod, byte[] converted)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(converted);
        ConversionOptions options = new() { InputPath = "stems.mod" };
        ConversionNotes silent = new(Logger.None);
        ModModule module = ModReader.Read(mod, silent, options.Player);
        Song song = ModToSong.Convert(module, options.AmigaClockHz, silent);
        GenesisPlan plan = new SimpleMapper().Map(song, MappingOptions.From(options), silent);
        DmfTimingPlan timing = DmfTimingPlanner.Plan(plan, options.AmigaTickRateScale, options.OutputRegion, silent);
        StemSource source = new(mod, module, song, plan, timing, new DmfBuildSettings(options.AmigaClockHz, options.Resampler));
        if (!source.Dmf(_ => true, plan.Patterns.Count).AsSpan().SequenceEqual(converted))
        {
            throw new HarnessException("The stems' conversion steps don't give the converter's DMF; the harness is out of step with ConversionCommand");
        }

        return source;
    }

    /// <summary>
    /// The samples (0-based, as <see cref="Song.Samples"/>) heard in the first <paramref name="orders"/> orders, on either
    /// side: named by a MOD cell, or played by a note of the plan. Samples without data are left out (silent everywhere).
    /// </summary>
    public IReadOnlyList<int> SamplesIn(int orders)
    {
        SortedSet<int> samples = [];
        foreach (int pattern in _module.Orders.Take(orders))
        {
            ModPattern cells = _module.Patterns[pattern];
            for (int row = 0; row < ModPattern.Rows; row++)
            {
                for (int channel = 0; channel < cells.ChannelCount; channel++)
                {
                    if (cells[row, channel].Sample is > 0 and int number && number <= _module.Samples.Count)
                    {
                        samples.Add(number - 1);
                    }
                }
            }
        }

        foreach (PlanCell cell in Cells(orders))
        {
            if (cell is { Note: not null, Sample: { } sample })
            {
                samples.Add(sample);
            }
        }

        return [.. samples.Where(s => !_module.Samples[s].IsEmpty)];
    }

    /// <summary>What the conversion plays sample <paramref name="sample"/> with in the first <paramref name="orders"/> orders: <c>FM</c>, <c>DAC</c>, <c>noise</c>, joined by <c>+</c>; empty when it plays none of its notes.</summary>
    public string Voices(int sample, int orders) =>
        string.Join('+', Cells(orders)
            .Where(c => c.Note is not null && c.Sample == sample)
            .Select(c => c.Voice?.Kind)
            .OfType<VoiceKind>()
            .Distinct()
            .Order()
            .Select(k => k switch { VoiceKind.Fm => "FM", VoiceKind.Dac => "DAC", VoiceKind.PsgNoise => "noise", _ => k.ToString() }));

    /// <summary>The name of sample <paramref name="sample"/> (0-based).</summary>
    public string Name(int sample) => _module.Samples[sample].Name;

    /// <summary>A copy of the MOD with every sample's data but <paramref name="sample"/>'s zeroed: that sample alone.</summary>
    public byte[] SoloMod(int sample)
    {
        byte[] copy = (byte[])_mod.Clone();
        foreach (ModSample other in _module.Samples.Where((_, i) => i != sample))
        {
            Array.Clear(copy, other.DataOffset, other.Length);
        }

        return copy;
    }

    /// <summary>The conversion's DMF with only <paramref name="sample"/>'s notes, cut after the first <paramref name="orders"/> orders.</summary>
    public byte[] SoloDmf(int sample, int orders) => Dmf(s => s == sample, orders);

    /// <summary>
    /// The DMF of the plan's first <paramref name="orders"/> orders, with every note whose sample (<c>null</c> when the plan
    /// names none) <paramref name="keep"/> refuses turned into a note-off (its effects stay, so the song's flow is the same). The timing is the whole song's.
    /// </summary>
    private byte[] Dmf(Func<int?, bool> keep, int orders)
    {
        int count = Math.Clamp(orders, 1, _plan.Patterns.Count);
        GenesisPlan solo = _plan with
        {
            Patterns = [.. _plan.Patterns.Take(count).Select(p => (IReadOnlyList<PlanRow>)[.. p.Select(r => r with { Cells = [.. r.Cells.Select(c => Keep(c, keep))] })])],
            LoopOrder = _plan.LoopOrder < count ? _plan.LoopOrder : null,
        };
        ConversionNotes silent = new(Logger.None);
        DmfSong dmf = DmfSongBuilder.Build(solo, _song, _timing, _settings, silent);
        return DmfWriter.Write(dmf.File, silent);
    }

    private static PlanCell Keep(PlanCell cell, Func<int?, bool> keep) =>
        cell.Note is null || keep(cell.Sample)
            ? cell
            : cell with { Note = null, Voice = null, Volume = null, NoteOff = true, Sample = null };

    private IEnumerable<PlanCell> Cells(int orders) => _plan.Patterns.Take(orders).SelectMany(p => p).SelectMany(r => r.Cells);
}
