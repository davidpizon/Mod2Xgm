using Mod2Xgm.Audio;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;

namespace Mod2Xgm.Cli;

/// <summary>
/// Everything the command line can set, already validated. See docs/cli.md.
/// </summary>
internal sealed record ConversionOptions
{
    public required string InputPath { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Dmf;
    public string? OutputPath { get; init; }

    /// <summary>
    /// The Amiga the MOD was made on (<c>--input-region</c>): the song's real pitch (<see cref="AmigaClockHz"/>) and
    /// speed (50 ticks/s on PAL, 60 on NTSC, at BPM 125; docs/cli.md#regions).
    /// </summary>
    public Region InputRegion { get; init; } = Region.Pal;

    /// <summary>The Genesis the output is made for (<c>--output-region</c>): its clocks and frame rate.</summary>
    public Region OutputRegion { get; init; } = Region.Pal;

    /// <summary>Paula clock that turns MOD periods into pitch, from <see cref="InputRegion"/> (docs/formats/mod.md#pitch).</summary>
    public double AmigaClockHz => InputRegion == Region.Ntsc ? AmigaPitch.NtscClockHz : AmigaPitch.PalClockHz;

    /// <summary>The source Amiga's tick rate relative to PAL, from <see cref="InputRegion"/>: 6/5 on NTSC (60 ticks/s at 125 BPM; docs/cli.md#regions).</summary>
    public double AmigaTickRateScale => InputRegion == Region.Ntsc ? 6.0 / 5.0 : 1.0;

    /// <summary>Whose rules to read the MOD by (<c>--player</c>), or <c>null</c> for the default for the file's format.</summary>
    public ModPlayer? Player { get; init; }

    /// <summary>How samples are played at a new rate (<c>--resampler</c>): smooth (default) or Amiga steps.</summary>
    public ResamplingMode Resampler { get; init; } = ResamplingMode.Smooth;

    public string? Mapping { get; init; }

    /// <summary>MOD sample numbers (1-based) to leave out.</summary>
    public IReadOnlySet<int> IgnoreSamples { get; init; } = new HashSet<int>();

    /// <summary>MOD sample numbers (1-based) that must be played by FM, never as PCM (<c>--require-fm</c>).</summary>
    public IReadOnlySet<int> RequireFm { get; init; } = new HashSet<int>();

    /// <summary>MOD sample numbers (1-based) that must stay PCM, never FM or PSG (<c>--require-dac</c>).</summary>
    public IReadOnlySet<int> RequireDac { get; init; } = new HashSet<int>();

    /// <summary>MOD sample numbers (1-based) that must be played by the PSG, never FM or the DAC (<c>--require-psg</c>).</summary>
    public IReadOnlySet<int> RequirePsg { get; init; } = new HashSet<int>();

    /// <summary>True when the DAC must not be used (<c>--no-dac</c>): every sample is played by FM or the PSG.</summary>
    public bool NoDac { get; init; }

    public bool NoConsolidate { get; init; }
    public long MaxPcmBytes { get; init; } = ConversionDefaults.MaxPcmBytes;
    public Quality Quality { get; init; } = Quality.Normal;

    /// <summary>Optimizer time limit; <c>null</c> means the default for <see cref="Quality"/>.</summary>
    public TimeSpan? TimeBudget { get; init; }

    public int Seed { get; init; }
    public bool NoCache { get; init; }
    public int ReserveSfxChannels { get; init; }
    public bool InfoOnly { get; init; }
    public bool Force { get; init; }
    public bool Verbose { get; init; }
}

/// <summary>Default values and limits shared by the CLI and the converter.</summary>
internal static class ConversionDefaults
{
    /// <summary>128 KB PCM budget per song (docs/sample-capacity.md).</summary>
    public const long MaxPcmBytes = 128 * 1024;

    /// <summary>Highest sample number a MOD can have (31-sample ProTracker format).</summary>
    public const int MaxModSamples = 31;

    /// <summary>PCM channels the SGDK XGM driver mixes.</summary>
    public const int XgmPcmChannels = 4;

    /// <summary>PCM channels the SGDK XGM2 driver mixes.</summary>
    public const int Xgm2PcmChannels = 3;
}
