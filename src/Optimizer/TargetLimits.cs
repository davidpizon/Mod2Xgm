using Mod2Xgm.Cli;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// What one conversion's output can hold: the rule book the capacity check and the allocator read (plan T9.1,
/// docs/adr/0085-target-limits.md, docs/sample-capacity.md#limits-per-target). Each <c>--format</c> has its own.
/// </summary>
/// <param name="Target">The file written.</param>
/// <param name="FmChannels">FM channels the music plays on: always 5, as FM6 belongs to the PCM.</param>
/// <param name="Ch3Special">True when FM3 may play two voices in CH3 special mode.</param>
/// <param name="PsgTones">PSG square channels.</param>
/// <param name="PsgNoise">PSG noise channels.</param>
/// <param name="PcmVoices">PCM notes the music may play at once, after <c>--reserve-sfx-channels</c>.</param>
/// <param name="PcmSlots">Stored PCM sounds the song may have.</param>
/// <param name="PcmBytes">Bytes those sounds may take, or <c>null</c> for no limit (DMF).</param>
internal sealed record TargetLimits(
    PcmTarget Target,
    int FmChannels,
    bool Ch3Special,
    int PsgTones,
    int PsgNoise,
    int PcmVoices,
    int PcmSlots,
    long? PcmBytes)
{
    /// <summary>FM1–5: the drivers mix PCM on the DAC in place of FM6 (docs/formats/xgm.md#driver-capabilities), and a DMF keeps to that (David).</summary>
    private const int MusicFmChannels = 5;

    /// <summary>The SN76489's square channels.</summary>
    private const int SquareChannels = 3;

    /// <summary>The SN76489's noise channel.</summary>
    private const int NoiseChannels = 1;

    /// <summary>The limits of this conversion. A budget bigger than XGM or XGM2 can hold is cut down, with a conversion note.</summary>
    /// <param name="options">The validated command line.</param>
    /// <param name="notes">Collects the note about a cut budget.</param>
    public static TargetLimits For(ConversionOptions options, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(notes);
        PcmTarget target = options.Format switch
        {
            OutputFormat.Dmf => PcmTarget.Dmf,
            OutputFormat.Xgm => PcmTarget.Xgm,
            _ => PcmTarget.Xgm2,
        };
        TargetLimits limits = For(target, options.MaxPcmBytes, options.ReserveSfxChannels);
        if (limits.PcmBytes < options.MaxPcmBytes)
        {
            notes.Add(
                "--max-pcm-bytes {Asked} is more than {Format:l} can hold; using {Bytes} bytes",
                options.MaxPcmBytes,
                PcmTargets.Name(target),
                limits.PcmBytes);
        }

        return limits;
    }

    /// <summary>The limits of <paramref name="target"/>.</summary>
    /// <param name="target">The file written.</param>
    /// <param name="maxPcmBytes">The song's PCM budget (<c>--max-pcm-bytes</c>); DMF has none.</param>
    /// <param name="reservedVoices">PCM voices left to the game (<c>--reserve-sfx-channels</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException">The budget isn't positive, or more voices are reserved than the target has.</exception>
    public static TargetLimits For(PcmTarget target, long maxPcmBytes, int reservedVoices)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPcmBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedVoices);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reservedVoices, PcmTargets.Voices(target));
        return new TargetLimits(
            target,
            MusicFmChannels,
            Ch3SpecialAllowed(target),
            SquareChannels,
            NoiseChannels,
            PcmTargets.Voices(target) - reservedVoices,
            PcmTargets.Slots(target),
            target == PcmTarget.Dmf ? null : Math.Min(maxPcmBytes, PcmTargets.MaxStoredBytes));
    }

    /// <summary>
    /// DMF (system 0x42) and XGM2 have CH3 special mode. XGM v1 waits for a probe ROM to show that register <c>$27</c>'s
    /// timer bits leave its driver working (plan T11.1).
    /// </summary>
    private static bool Ch3SpecialAllowed(PcmTarget target) => target != PcmTarget.Xgm;
}
