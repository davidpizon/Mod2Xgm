using Mod2Xgm.Analysis;
using Mod2Xgm.Model;
using Mod2Xgm.Render;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Some of a song's notes as the MOD renderer plays them, alone and in mono: what a combined candidate (a pre-mix, a CH3
/// pair) is measured against (docs/adr/0072-pre-mix-candidates.md, docs/adr/0073-ch3-locked-pairs.md).
/// </summary>
internal static class NoteMix
{
    /// <summary>
    /// <paramref name="notes"/> from tick <paramref name="startTick"/> to the tick before <paramref name="endTick"/>, every
    /// other note silenced, at <see cref="SampleAnalyzer.AnalysisRateHz"/>, scaled so one note at full volume is full
    /// scale; at least <paramref name="seconds"/> long (silence after the last tick).
    /// </summary>
    public static float[] Render(Song song, Playback playback, IReadOnlyList<SongNote> notes, int startTick, int endTick, double seconds)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(notes);
        SampleBlock silence = new(null, 0, 0);
        double start = playback.Ticks[startTick].TimeSeconds;
        List<PlaybackTick> ticks = [];
        for (int t = startTick; t < Math.Min(endTick, playback.Ticks.Count); t++)
        {
            PlaybackTick tick = playback.Ticks[t];
            ChannelTick[] channels = new ChannelTick[notes.Count];
            for (int k = 0; k < notes.Count; k++)
            {
                SongNote note = notes[k];
                ChannelTick channel = tick.Channels[note.Channel];
                channels[k] = t >= note.FirstTick && t < note.FirstTick + note.Ticks
                    ? channel with { Pan = 0 }
                    : new ChannelTick(false, channel.Pitch, 0, 0, null, silence);
            }

            ticks.Add(tick with { TimeSeconds = tick.TimeSeconds - start, Channels = channels });
        }

        float[] left = ModRenderer.Render(song, new Playback(ticks, null, PlaybackEnd.Stops), new ModRenderOptions(SampleAnalyzer.AnalysisRateHz, 0)).Left;
        float[] mix = new float[Math.Max(left.Length, (int)Math.Ceiling(seconds * SampleAnalyzer.AnalysisRateHz))];
        for (int i = 0; i < left.Length; i++)
        {
            mix[i] = (float)(left[i] / ModRenderer.MonoChannelLevel);
        }

        return mix;
    }
}
