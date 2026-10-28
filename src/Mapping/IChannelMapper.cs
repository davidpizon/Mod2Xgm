using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// Decides which Genesis channel plays each note of a <see cref="Song"/> and with which sound (docs/channel-mapping.md).
/// The simple mapper (<see cref="SimpleMapper"/>) comes first; the multi-pass optimizer (plan M9) is another.
/// </summary>
internal interface IChannelMapper
{
    /// <summary>Maps <paramref name="song"/>; every lossy decision becomes a note in <paramref name="notes"/>.</summary>
    GenesisPlan Map(Song song, MappingOptions options, ConversionNotes notes);
}

/// <summary>What the user can pin or forbid for mapping; sample numbers are 0-based, like <see cref="Song.Samples"/>.</summary>
/// <param name="RequireFm">Samples that must be played by FM (<c>--require-fm</c>).</param>
/// <param name="RequireDac">Samples that must be played on the DAC (<c>--require-dac</c>).</param>
/// <param name="RequirePsg">Samples that must be played by the PSG (<c>--require-psg</c>).</param>
/// <param name="UseDac">False when the DAC must not be used (<c>--no-dac</c>).</param>
internal sealed record MappingOptions(IReadOnlySet<int> RequireFm, IReadOnlySet<int> RequireDac, IReadOnlySet<int> RequirePsg, bool UseDac = true)
{
    /// <summary>No requirements; the DAC may be used.</summary>
    public static MappingOptions Default { get; } = new(new HashSet<int>(), new HashSet<int>(), new HashSet<int>());

    /// <summary>The options a conversion was asked for, with the command line's 1-based sample numbers made 0-based.</summary>
    public static MappingOptions From(Cli.ConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MappingOptions(
            new HashSet<int>(options.RequireFm.Select(n => n - 1)),
            new HashSet<int>(options.RequireDac.Select(n => n - 1)),
            new HashSet<int>(options.RequirePsg.Select(n => n - 1)),
            !options.NoDac);
    }
}
