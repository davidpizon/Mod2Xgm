using Mod2Xgm.Cli;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Scoring;
using Serilog.Core;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>A stretch of the song where the two renders sound furthest apart.</summary>
/// <param name="StartSeconds">Where the window starts.</param>
/// <param name="MelDistanceDb">Its log-mel distance.</param>
internal sealed record SoundWindow(double StartSeconds, double MelDistanceDb);

/// <summary>What check 3 found.</summary>
/// <param name="Score">The project's score of the candidate against the reference (aligned, whole song).</param>
/// <param name="WindowSeconds">Length of each window in <paramref name="Worst"/>.</param>
/// <param name="MedianWindowDb">The median window's log-mel distance: what "normal" is for this song.</param>
/// <param name="Worst">The windows furthest apart, worst first.</param>
internal sealed record SoundComparison(ScoreReport Score, double WindowSeconds, double MedianWindowDb, IReadOnlyList<SoundWindow> Worst);

/// <summary>
/// Check 3 of the DMF sanity check, "sounds the same" (docs/testing.md#dmf-sanity-check): both logs rendered with the
/// project's chip models — the reference with DefleMask's DAC (<see cref="DmfDac"/>), the candidate with SGDK's XGM2
/// driver model — and scored with <see cref="SongScorer"/>, plus the log-mel distance of each window to show where they
/// differ. Both are rendered at the reference's frame rate, so the console's 59.92 Hz frames don't stretch the song;
/// that difference is the timing section's.
/// </summary>
internal static class SoundCheck
{
    /// <summary>Renders are compared at this rate: plenty for the metrics, and four times faster to score.</summary>
    private const int ScoreRateHz = 22050;

    /// <summary>Windows kept in <see cref="SoundComparison.Worst"/>.</summary>
    private const int WorstWindows = 5;

    /// <summary>Renders <paramref name="log"/>'s frames with <paramref name="driver"/> playing PCM, each frame <paramref name="frameSeconds"/> long.</summary>
    public static StereoAudio Render(ChipLog log, PcmDriver driver, double frameSeconds)
    {
        ArgumentNullException.ThrowIfNull(log);
        GenesisChips chips = new(Region.Ntsc, driver);
        double rate = chips.SampleRateHz;
        int total = (int)Math.Round(log.Frames.Count * frameSeconds * rate);
        float[] left = new float[total];
        float[] right = new float[total];
        for (int f = 0; f < log.Frames.Count; f++)
        {
            foreach (ChipWrite w in log.Frames[f])
            {
                Apply(chips, w);
            }

            int from = (int)Math.Round(f * frameSeconds * rate);
            int to = Math.Min(total, (int)Math.Round((f + 1) * frameSeconds * rate));
            chips.Render(left.AsSpan(from, to - from), right.AsSpan(from, to - from));
        }

        return new StereoAudio(left, right, (int)Math.Round(rate));
    }

    /// <summary>Scores <paramref name="candidate"/> against <paramref name="reference"/>, whole and by window.</summary>
    public static SoundComparison Compare(StereoAudio reference, StereoAudio candidate, double windowSeconds = 4)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        AudioSignal a = reference.At(ScoreRateHz).ToMono();
        AudioSignal b = candidate.At(ScoreRateHz).ToMono();
        ScoreReport score = SongScorer.Compare(a, b, ScoreOptions.Default, Logger.None);
        int window = (int)(windowSeconds * ScoreRateHz);
        List<SoundWindow> windows = [];
        for (int start = 0; start + window <= a.Length; start += window)
        {
            AudioSignal x = new(a.Samples.Slice(start, window).ToArray(), ScoreRateHz);
            AudioSignal y = new(Slice(b, start, window), ScoreRateHz);
            windows.Add(new SoundWindow((double)start / ScoreRateHz, SpectralMetrics.MelDistanceDb(x, y)));
        }

        double median = windows.Count == 0 ? 0 : windows.Select(w => w.MelDistanceDb).Order().ElementAt(windows.Count / 2);
        return new SoundComparison(score, windowSeconds, median, [.. windows.OrderByDescending(w => w.MelDistanceDb).Take(WorstWindows)]);
    }

    /// <summary><paramref name="length"/> samples of <paramref name="signal"/> from <paramref name="start"/>, padded with silence past its end.</summary>
    private static float[] Slice(AudioSignal signal, int start, int length)
    {
        float[] result = new float[length];
        if (start < signal.Length)
        {
            signal.Samples.Slice(start, Math.Min(length, signal.Length - start)).CopyTo(result);
        }

        return result;
    }

    private static void Apply(GenesisChips chips, ChipWrite w)
    {
        switch (w.Target)
        {
            case ChipTarget.Fm when w.Register == ChipLog.RegDacEnable:
                if (chips.Dac is DmfDac dac)
                {
                    dac.SetDacMode((w.Value & 0x80) != 0);
                }

                break;
            case ChipTarget.Fm when w.Register == ChipLog.RegDacData:
                break;
            case ChipTarget.Fm:
                chips.Ports.WriteRegister(w.Part, (byte)w.Register, (byte)w.Value);
                break;
            case ChipTarget.Psg:
                chips.WritePsg((byte)w.Value);
                break;
            case ChipTarget.PcmPlay:
                sbyte[] data = w.Data ?? [];
                switch (chips.Dac)
                {
                    case DmfDac dmf when data.Length > 0:
                        dmf.SetDacMode(true);
                        dmf.Play([.. data.Select(b => (short)(b << 8))], (int)Math.Round(w.RateHz));
                        break;
                    case Xgm2PcmDriver xgm2 when data.Length > 0:
                        xgm2.Play(w.Channel, data, Xgm2PcmDriver.LowPriority, w.HalfSpeed);
                        break;
                }

                break;
            default:
                switch (chips.Dac)
                {
                    case DmfDac dmf:
                        dmf.Stop();
                        break;
                    case Xgm2PcmDriver xgm2:
                        xgm2.Stop(w.Channel);
                        break;
                }

                break;
        }
    }
}
