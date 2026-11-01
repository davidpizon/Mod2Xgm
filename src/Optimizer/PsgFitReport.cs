using System.Globalization;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>Logs PSG candidates at Debug level (shown with <c>--verbose</c>): one line per pitch region.</summary>
internal static class PsgFitReport
{
    /// <summary>Logs every region of every fit in <paramref name="fits"/>.</summary>
    public static void Log(ILogger logger, IReadOnlyList<PsgSampleFit> fits)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fits);
        foreach (PsgSampleFit fit in fits)
        {
            foreach (PsgRegionFit r in fit.Regions)
            {
                logger.Debug(
                    "Sample {Sample:l}, region {Region}: PSG tone {Tone:l}; noise {Noise:l}; best {Best:l}; best with room {Fits:l}; {Renders} notes rendered in {Seconds:0.0} s",
                    Format($"{fit.Sample + 1} \"{fit.Name}\""),
                    r.Region + 1,
                    r.Tone is { } tone ? Describe(tone) : "too low",
                    Describe(r.BestNoise),
                    Describe(r.Best),
                    r.BestThatFits is { } roomy ? Describe(roomy) : "none",
                    r.Renders,
                    r.Elapsed.TotalSeconds);
            }
        }
    }

    /// <summary>A try's settings and distance, e.g. "periodic noise, rate 3, period 254, 1.2 cents off: 14.20 dB".</summary>
    public static string Describe(PsgCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string cents = candidate.CentsOff is { } c ? Format($", {c:0.#} cents off") : string.Empty;
        return Format($"{Settings(candidate.Setting)}{cents}: {candidate.Score.MelDb:0.00} dB");
    }

    /// <summary>The settings in short: the sound, the noise rate and the period that apply to it.</summary>
    public static string Settings(PsgSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Sound switch
        {
            PsgSound.Tone => Format($"tone, period {setting.Period}"),
            _ when setting.NoiseRate == PsgSetting.Tone3Rate => Format($"{SoundName(setting.Sound)} noise, rate 3, period {setting.Period}"),
            _ => Format($"{SoundName(setting.Sound)} noise, rate {setting.NoiseRate}"),
        };
    }

    /// <summary>The sound as a word, for logs and the harness's CSV.</summary>
    public static string SoundName(PsgSound sound) => sound switch
    {
        PsgSound.Tone => "tone",
        PsgSound.WhiteNoise => "white",
        _ => "periodic",
    };

    /// <summary>The room as words, for logs and the harness's CSV.</summary>
    public static string RoomName(PsgRoom room) => room switch
    {
        PsgRoom.Fits => "fits",
        PsgRoom.SampleTooOften => "sample too often",
        _ => "song too full",
    };

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
