using Mod2Xgm.Model;

namespace Mod2Xgm.Analysis;

/// <summary>
/// How many notes the song sounds at once (pass 1 of the multi-pass optimizer, docs/multi-pass.md#passes): in all, and
/// of each sample. A channel sounds from a note's start until its sample ends (a one-shot runs out) or its volume is 0.
/// The PSG candidates use it to mark the tries the song has no room for (docs/adr/0070-psg-candidates.md).
/// </summary>
/// <param name="MostAtOnce">The most channels sounding on any tick.</param>
/// <param name="SampleMostAtOnce">Per sample (index into <c>Song.Samples</c>), the most channels sounding it on any tick.</param>
internal sealed record SongVoices(int MostAtOnce, IReadOnlyList<int> SampleMostAtOnce)
{
    /// <summary>The most channels sounding <paramref name="sample"/> at once; 0 for one the song never plays.</summary>
    public int MostAtOnceOf(int sample) => sample >= 0 && sample < SampleMostAtOnce.Count ? SampleMostAtOnce[sample] : 0;

    /// <summary>
    /// Counts the channels sounding on each tick of <paramref name="playback"/>, following each channel through its
    /// sample's parts (<see cref="ChannelTick.Started"/>, then <see cref="ChannelTick.Next"/> each time a part ends) at its
    /// playback rate. A tick counts a channel that sounds at its start.
    /// </summary>
    public static SongVoices Count(Song song, Playback playback)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        int[] sampleMost = new int[song.Samples.Count];
        int[] sampleNow = new int[song.Samples.Count];
        SampleBlock?[] part = new SampleBlock?[song.Channels.Count];
        double[] left = new double[song.Channels.Count];
        int most = 0;
        foreach (PlaybackTick tick in playback.Ticks)
        {
            Array.Clear(sampleNow);
            int now = 0;
            for (int c = 0; c < tick.Channels.Count && c < part.Length; c++)
            {
                ChannelTick channel = tick.Channels[c];
                if (channel.Started is { } started)
                {
                    part[c] = started;
                    left[c] = started.Frames;
                }

                if (channel is { Playing: true, Volume: > 0 } && part[c] is { IsSilence: false, Sample: { } sample })
                {
                    now++;
                    if (sample < sampleNow.Length)
                    {
                        sampleNow[sample]++;
                    }
                }

                Advance(ref part[c], ref left[c], channel.Next, channel.Pitch.PlaybackRateHz * tick.DurationSeconds);
            }

            most = Math.Max(most, now);
            for (int s = 0; s < sampleNow.Length; s++)
            {
                sampleMost[s] = Math.Max(sampleMost[s], sampleNow[s]);
            }
        }

        return new SongVoices(most, sampleMost);
    }

    /// <summary>Plays <paramref name="frames"/> frames of a channel's part, moving on to <paramref name="next"/> each time it ends; a silent part stays.</summary>
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
