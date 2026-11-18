namespace Mod2Xgm.Model.Window;

/// <summary>
/// Which ticks of a playback sound (docs/adr/0122-trim-silence.md): a tick sounds when any channel plays a part of a sample
/// at a volume above 0. A channel plays the part it started, then the parts that follow it (<see cref="ChannelTick.Next"/>)
/// as each runs out; a one-shot sample that has played out plays silence.
/// </summary>
internal static class SilenceScan
{
    /// <summary>For each tick of <paramref name="playback"/>'s pass, true when anything sounds on it.</summary>
    public static bool[] Sounding(Playback playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        IReadOnlyList<PlaybackTick> ticks = playback.Ticks;
        bool[] sounding = new bool[ticks.Count];
        int channels = ticks.Count == 0 ? 0 : ticks[0].Channels.Count;
        SampleBlock?[] part = new SampleBlock?[channels];
        double[] left = new double[channels];
        for (int t = 0; t < ticks.Count; t++)
        {
            PlaybackTick tick = ticks[t];
            for (int c = 0; c < channels && c < tick.Channels.Count; c++)
            {
                ChannelTick channel = tick.Channels[c];
                if (channel.Started is { } started)
                {
                    part[c] = started;
                    left[c] = started.Frames;
                }

                if (part[c] is { IsSilence: false } && channel.Volume > 0)
                {
                    sounding[t] = true;
                }

                Advance(ref part[c], ref left[c], channel.Next, channel.Pitch.PlaybackRateHz * tick.DurationSeconds);
            }
        }

        return sounding;
    }

    /// <summary>Plays <paramref name="frames"/> frames of a channel's part, moving on to <paramref name="next"/> each time it ends.</summary>
    private static void Advance(ref SampleBlock? part, ref double left, SampleBlock next, double frames)
    {
        if (part is not { IsSilence: false } || frames <= 0)
        {
            return;
        }

        left -= frames;
        while (left <= 0)
        {
            part = next;
            if (next.IsSilence)
            {
                return;
            }

            left += next.Frames;
        }
    }
}
