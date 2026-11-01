using Mod2Xgm.Dmf;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Optimizer;

/// <summary>The output a PCM copy is made for: each plays PCM its own way (docs/sample-capacity.md#limits-per-target).</summary>
internal enum PcmTarget
{
    /// <summary>DefleMask's DAC on FM6: one sample at a time, 16-bit, at one of six rates.</summary>
    Dmf,

    /// <summary>SGDK's XGM driver: four channels of 8-bit PCM at about 14 kHz.</summary>
    Xgm,

    /// <summary>SGDK's XGM2 driver: three channels of 8-bit PCM at 13.3 kHz, or half that.</summary>
    Xgm2,
}

/// <summary>
/// Ways to make a sample's PCM copies smaller, each giving up a little sound (docs/adr/0071-pcm-candidates.md). Every
/// combination that applies is tried and scored.
/// </summary>
[Flags]
internal enum PcmSavings
{
    /// <summary>A copy for every pitch the song starts the sample at, played out as long as any note of it lasts.</summary>
    None = 0,

    /// <summary>XGM2 only: copies at half speed (6.65 kHz), half the bytes.</summary>
    HalfSpeed = 1,

    /// <summary>Pitches within the same semitone of the sample's measured pitch share the copy of the most played one.</summary>
    Semitones = 2,

    /// <summary>A looped sample's copy is played out only as long as the song holds that pitch.</summary>
    ShortLoops = 4,

    /// <summary>A one-shot's quiet end, below <see cref="PcmTargets.TrimDb"/> under its peak, is cut off.</summary>
    TrimTail = 8,
}

/// <summary>What each <see cref="PcmTarget"/> can hold and how it stores and plays a copy.</summary>
internal static class PcmTargets
{
    /// <summary>The targets, in order.</summary>
    public static IReadOnlyList<PcmTarget> All { get; } = [PcmTarget.Dmf, PcmTarget.Xgm, PcmTarget.Xgm2];

    /// <summary><see cref="PcmSavings.TrimTail"/> cuts a one-shot after its last point this far below its peak.</summary>
    public const double TrimDb = 48;

    /// <summary>XGM and XGM2 store samples in 256-byte blocks (docs/formats/xgm.md).</summary>
    private const int XgmAlignment = 256;

    /// <summary>
    /// The copies the target can hold: DMF's 144 DAC notes (12 banks × 12, docs/formats/dmf.md#dac-samples-on-fm6),
    /// XGM's 63 samples and XGM2's 122 (docs/sample-capacity.md#limits-per-target). A hard cap.
    /// </summary>
    public static int Slots(PcmTarget target) => target switch
    {
        PcmTarget.Dmf => 144,
        PcmTarget.Xgm => 63,
        _ => 122,
    };

    /// <summary>The PCM notes the target plays at once, before <c>--reserve-sfx-channels</c>. A hard cap.</summary>
    public static int Voices(PcmTarget target) => target switch
    {
        PcmTarget.Dmf => 1,
        PcmTarget.Xgm => 4,
        _ => 3,
    };

    /// <summary>The rate a copy of a note at <paramref name="noteRateHz"/> is stored and played at.</summary>
    public static double PlayRateHz(PcmTarget target, double noteRateHz, bool halfSpeed) => target switch
    {
        PcmTarget.Dmf => DmfSongBuilder.DacRateHz(noteRateHz),
        PcmTarget.Xgm => XgmPcmDriver.BeatRateHz(Ym3438.NtscClockHz),
        _ => Xgm2PcmDriver.BeatRateHz(Ym3438.NtscClockHz) / (halfSpeed ? 2 : 1),
    };

    /// <summary>The bytes a copy of <paramref name="points"/> points takes: 16-bit for DMF, 8-bit in 256-byte blocks for XGM and XGM2.</summary>
    public static int Bytes(PcmTarget target, int points) => target == PcmTarget.Dmf
        ? 2 * points
        : (points + XgmAlignment - 1) / XgmAlignment * XgmAlignment;

    /// <summary>The target's name, for logs and the harness's CSV.</summary>
    public static string Name(PcmTarget target) => target switch
    {
        PcmTarget.Dmf => "dmf",
        PcmTarget.Xgm => "xgm",
        _ => "xgm2",
    };

    /// <summary>The savings as words joined by "+", or "none", for logs and the harness's CSV.</summary>
    public static string Name(PcmSavings savings)
    {
        if (savings == PcmSavings.None)
        {
            return "none";
        }

        List<string> parts = [];
        if (savings.HasFlag(PcmSavings.HalfSpeed))
        {
            parts.Add("half speed");
        }

        if (savings.HasFlag(PcmSavings.Semitones))
        {
            parts.Add("semitones");
        }

        if (savings.HasFlag(PcmSavings.ShortLoops))
        {
            parts.Add("short loops");
        }

        if (savings.HasFlag(PcmSavings.TrimTail))
        {
            parts.Add("trim tail");
        }

        return string.Join('+', parts);
    }
}
