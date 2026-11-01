using Mod2Xgm.Model;

namespace Mod2Xgm.Analysis;

/// <summary>
/// One note the song plays (pass 1 of the multi-pass optimizer, docs/multi-pass.md#passes): where it starts, the pitch and
/// volume it starts at, how long it sounds, and whether it does something a PCM copy can't play by itself
/// (docs/adr/0071-pcm-candidates.md).
/// </summary>
/// <param name="Channel">Song channel.</param>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="FirstTick">Index into <see cref="Playback.Ticks"/> of the tick it starts on.</param>
/// <param name="Ticks">Ticks it sounds on, the first included.</param>
/// <param name="RateHz">The playback rate it starts at.</param>
/// <param name="StartFrame">The first frame it plays (MOD <c>9xx</c>).</param>
/// <param name="Volume">The volume it starts at, 0–1.</param>
/// <param name="Seconds">How long it sounds: until the next note on its channel, its sample runs out, the channel
/// swaps to another sample, or the song ends.</param>
/// <param name="Bent">True when it starts past frame 0, or its pitch or volume changes while it sounds (a slide, vibrato,
/// arpeggio, volume slide or tremolo).</param>
internal sealed record SongNote(int Channel, int Sample, int FirstTick, int Ticks, double RateHz, int StartFrame, double Volume, double Seconds, bool Bent)
{
    /// <summary>Every note of <paramref name="playback"/>, in the order they start (by tick, then channel).</summary>
    public static IReadOnlyList<SongNote> Find(Song song, Playback playback)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        List<SongNote> found = [];
        int channels = song.Channels.Count;
        for (int c = 0; c < channels; c++)
        {
            Open? open = null;
            SampleBlock? part = null;
            double left = 0;
            for (int t = 0; t < playback.Ticks.Count; t++)
            {
                PlaybackTick tick = playback.Ticks[t];
                if (c >= tick.Channels.Count)
                {
                    continue;
                }

                ChannelTick channel = tick.Channels[c];
                if (channel.Started is { } started)
                {
                    Close(open, found);
                    open = started is { IsSilence: false, Sample: { } s } && s < song.Samples.Count && channel.Pitch.PlaybackRateHz > 0
                        ? new Open(c, s, t, channel.Pitch.PlaybackRateHz, started.StartFrame, channel.Volume)
                        : null;
                    part = started;
                    left = started.Frames;
                }
                else if (open is not null && (part is not { IsSilence: false } || (channel.Next.Sample is { } next && next != open.Sample)))
                {
                    Close(open, found);
                    open = null;
                }

                if (open is not null)
                {
                    open.Add(channel, tick.DurationSeconds);
                }

                Advance(ref part, ref left, channel.Next, channel.Pitch.PlaybackRateHz * tick.DurationSeconds);
            }

            Close(open, found);
        }

        return [.. found.OrderBy(n => n.FirstTick).ThenBy(n => n.Channel)];
    }

    private static void Close(Open? open, List<SongNote> found)
    {
        if (open is not null)
        {
            found.Add(open.Note());
        }
    }

    /// <summary>Plays <paramref name="frames"/> frames of a channel's part, moving on to <paramref name="next"/> each time it ends (as <see cref="SongVoices"/>).</summary>
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

    /// <summary>A note still sounding, as the scan follows it.</summary>
    private sealed class Open(int channel, int sample, int firstTick, double rateHz, int startFrame, double volume)
    {
        /// <summary>A rate (as a share) or a volume that moves less than this hasn't changed: rounding, not an effect.</summary>
        private const double Same = 1e-9;

        private int _ticks;
        private double _seconds;
        private bool _bent = startFrame > 0;

        public int Sample => sample;

        /// <summary>One more tick of the note.</summary>
        public void Add(ChannelTick tick, double seconds)
        {
            _ticks++;
            _seconds += seconds;
            _bent |= Math.Abs(tick.Pitch.PlaybackRateHz - rateHz) > rateHz * Same || Math.Abs(tick.Volume - volume) > Same;
        }

        public SongNote Note() => new(channel, sample, firstTick, _ticks, rateHz, startFrame, volume, _seconds, _bent);
    }
}
