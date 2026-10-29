using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Runs a MOD through every stage to a DMF, the way a conversion will (reader, song model, simple mapper, timing,
/// song builder, writer), and reads the file back with the test-only <see cref="DmfReader"/>, so tests see exactly the
/// bytes DefleMask would.
/// </summary>
internal static class DmfConversion
{
    /// <summary>The DMF file as read back, its bytes, the conversion notes, the song model and the builder's output.</summary>
    internal sealed record Result(DmfFile File, byte[] Bytes, List<string> Notes, Song Song, DmfSong Built)
    {
        /// <summary>The cell of channel <paramref name="channel"/> at <paramref name="order"/>, <paramref name="row"/>.</summary>
        public DmfCell Cell(GenesisChannel channel, int row, int order = 0) => File.Channels[(int)channel].Patterns[order][row];

        /// <summary>The effects of that cell as (code, value) pairs, empty columns left out.</summary>
        public List<(int Code, int Value)> Effects(GenesisChannel channel, int row, int order = 0) =>
            [.. Cell(channel, row, order).Effects.Where(e => !e.IsEmpty).Select(e => (e.Code, e.Value))];

        /// <summary>Every effect on a row, all channels together.</summary>
        public List<(int Code, int Value)> RowEffects(int row, int order = 0) =>
            [.. Enum.GetValues<GenesisChannel>().SelectMany(c => Effects(c, row, order))];
    }

    /// <summary>Converts <paramref name="mod"/> (PAL in and out, smooth resampling).</summary>
    /// <param name="mod">The MOD file.</param>
    /// <param name="options">Mapping options; the defaults when <c>null</c>.</param>
    /// <param name="transform">Changes the plan before timing (e.g. a loop order, which the tick player will find in a later task).</param>
    public static Result Convert(byte[] mod, MappingOptions? options = null, Func<GenesisPlan, GenesisPlan>? transform = null)
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        ModModule module = ModReader.Read(mod, notes);
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, notes);
        GenesisPlan plan = new SimpleMapper().Map(song, options ?? MappingOptions.Default, notes);
        plan = transform?.Invoke(plan) ?? plan;
        DmfTimingPlan timing = DmfTimingPlanner.Plan(plan, 1.0, Region.Pal, notes);
        DmfSong built = DmfSongBuilder.Build(plan, song, timing, new DmfBuildSettings(AmigaPitch.PalClockHz, ResamplingMode.Smooth), notes);
        byte[] bytes = DmfWriter.Write(built.File, notes);
        return new Result(DmfReader.Read(bytes), bytes, [.. notes.Messages], song, built);
    }
}
