using System.Globalization;
using System.Runtime.InteropServices;
using Mod2Xgm.Analysis;
using Mod2Xgm.Audio;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// One pre-mix the allocator plays in place of a voice steal (plan T9.4b, docs/adr/0094-pre-mixed-voice-steals.md): the
/// notes mixed into one recording, and the rest of a looped note that outlasts them.
/// </summary>
/// <param name="Head">The mixed recording, played by the first note's cell.</param>
/// <param name="Tail">The rest of <paramref name="Outlast"/>'s sample, from <paramref name="SwitchTick"/>; <c>null</c> when every note is baked into <paramref name="Head"/>.</param>
/// <param name="SwitchTick">The row's first tick the tail starts on.</param>
/// <param name="Outlast">The note the tail goes on with.</param>
/// <param name="ClipDb">How far the mix was turned down so it doesn't clip; 0 when it didn't need to be.</param>
/// <param name="HeadKey">A fingerprint of the head's bytes and rate: the same sound twice is stored once.</param>
/// <param name="TailKey">The same for the tail.</param>
internal sealed record StealMix(DacSample Head, DacSample? Tail, int SwitchTick, SongNote? Outlast, double ClipDb, Fingerprint HeadKey, Fingerprint TailKey);

/// <summary>
/// Mixes notes that sound together into one recording for one target (plan T9.4b): the MOD renderer plays them alone, in
/// mono, the mix is turned down if it would clip and resampled to the target's rate, quantized only when written, as for
/// the pre-mix candidates (docs/adr/0072-pre-mix-candidates.md).
/// </summary>
internal static class StealMixer
{
    /// <summary>A pre-mix holds at most this many notes: XGM's PCM voices, the most any target has (docs/adr/0072-pre-mix-candidates.md).</summary>
    public const int MaxNotes = 4;

    /// <summary>Quantizing for a fingerprint makes no notes: the writers quantize again and note it there.</summary>
    private static readonly ConversionNotes Silent = new(Logger.None);

    /// <summary>
    /// The pre-mix of <paramref name="notes"/> on <paramref name="target"/>, or <c>null</c> when <paramref name="rules"/>
    /// don't allow it: the mix would be turned down more than <see cref="PreMixRules.MaxClipDb"/>, or a looped note outlasts
    /// it and tails are off or no row starts in time.
    /// </summary>
    /// <param name="song">The song.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="notes">The notes, each played once, overlapping.</param>
    /// <param name="target">The output.</param>
    /// <param name="rules">The pre-mix rules.</param>
    public static StealMix? Make(Song song, Playback playback, IReadOnlyList<SongNote> notes, PcmTarget target, PreMixRules rules)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(rules);
        List<SongNote> sorted = [.. notes.OrderBy(n => n.FirstTick).ThenBy(n => n.Channel)];
        int start = sorted[0].FirstTick;
        int end = sorted.Max(EndOf);
        int overlapEnd = OverlapEnd(sorted);
        SongNote? outlast = sorted.Where(n => EndOf(n) > overlapEnd).Cast<SongNote?>().SingleOrDefault();
        int headEnd = end;
        int switchTick = -1;
        if (outlast is { } last && song.Samples[last.Sample].Loop is not null)
        {
            switchTick = rules.Tails ? RowStart(playback, overlapEnd, EndOf(last)) : -1;
            if (switchTick < 0)
            {
                return null;
            }

            headEnd = switchTick;
        }

        float[] mix = NoteMix.Render(song, playback, sorted, start, headEnd, 0);
        if (switchTick < 0)
        {
            mix = Trim(mix);
        }

        if (mix.Length == 0)
        {
            return null;
        }

        double rate = target == PcmTarget.Dmf ? DmfSongBuilder.DacRateHz(sorted.Max(n => n.RateHz)) : PcmTargets.PlayRateHz(target, 0, false);
        float[] head = SampleProcessor.Resample(mix, SampleAnalyzer.AnalysisRateHz, rate, ResamplingMode.Smooth);
        float peak = 0;
        foreach (float x in head)
        {
            peak = Math.Max(peak, Math.Abs(x));
        }

        // Measured after resampling, which can overshoot, and against the loudest point 8-bit PCM holds.
        double clipDb = peak > Ceiling ? 20 * Math.Log10(peak / Ceiling) : 0;
        if (clipDb > rules.MaxClipDb)
        {
            return null;
        }

        float scale = peak > Ceiling ? Ceiling / peak : 1;
        head = SampleProcessor.ApplyGain(head, scale);
        bool centred = sorted.Select(n => PlanVoicing.Side(playback.Ticks[n.FirstTick].Channels[n.Channel].Pan)).Distinct().Count() > 1;
        string name = "mix " + string.Join('+', sorted.Select(n => (n.Sample + 1).ToString("00", CultureInfo.InvariantCulture)));
        DacSample headSample = new(sorted[0].Sample, rate) { Mix = new DacMix(head, name, centred) };
        if (switchTick < 0)
        {
            return new StealMix(headSample, null, -1, null, clipDb, Key(head, rate, target), default);
        }

        SongNote o = outlast!; // A switch is only found for an outlasting note.
        (float[] tail, double tailRate) = Tail(song, playback, o, switchTick, scale, target);
        DacSample tailSample = new(o.Sample, tailRate) { Mix = new DacMix(tail, string.Create(CultureInfo.InvariantCulture, $"{o.Sample + 1:00} tail"), false) };
        return new StealMix(headSample, tailSample, switchTick, o, clipDb, Key(head, rate, target), Key(tail, tailRate, target));
    }

    /// <summary>
    /// The loudest point a mix may reach: 127/128, the top of 8-bit PCM (<c>SampleProcessor</c> clips a positive point
    /// past it). DMF's 16-bit DAC holds a little more, but one ceiling keeps the targets' mixes alike.
    /// </summary>
    private const float Ceiling = 127f / 128;

    /// <summary>The bytes <paramref name="sample"/>'s mix takes on <paramref name="target"/>.</summary>
    public static int Bytes(DacSample sample, PcmTarget target)
    {
        ArgumentNullException.ThrowIfNull(sample);
        int points = sample.Mix?.Points.Length ?? 0;
        return PcmTargets.Bytes(target, points);
    }

    /// <summary>The tick after a note's last.</summary>
    private static int EndOf(SongNote note) => note.FirstTick + note.Ticks;

    /// <summary>The tick after the last on which two or more of <paramref name="notes"/> sound.</summary>
    private static int OverlapEnd(List<SongNote> notes)
    {
        int last = notes[0].FirstTick;
        for (int t = notes[0].FirstTick; t < notes.Max(EndOf); t++)
        {
            if (notes.Count(n => t >= n.FirstTick && t < EndOf(n)) >= 2)
            {
                last = t + 1;
            }
        }

        return last;
    }

    /// <summary>
    /// The first tick from <paramref name="from"/> to before <paramref name="to"/> that starts a row the song plays only
    /// once, so the tail's cell starts it only there; −1 when there is none.
    /// </summary>
    private static int RowStart(Playback playback, int from, int to)
    {
        for (int t = from; t < to; t++)
        {
            PlaybackTick tick = playback.Ticks[t];
            if (tick.Tick == 0 && playback.Ticks.Count(k => k.Tick == 0 && k.Order == tick.Order && k.Row == tick.Row) == 1)
            {
                return t;
            }
        }

        return -1;
    }

    /// <summary>The mix up to its last point within <see cref="PcmTargets.TrimDb"/> of its peak: a one-shot's silence after it ends costs bytes and is heard as nothing.</summary>
    private static float[] Trim(float[] mix)
    {
        float peak = 0;
        foreach (float x in mix)
        {
            peak = Math.Max(peak, Math.Abs(x));
        }

        float floor = peak * (float)Math.Pow(10, -PcmTargets.TrimDb / 20);
        int last = mix.Length - 1;
        while (last >= 0 && Math.Abs(mix[last]) <= floor)
        {
            last--;
        }

        return mix[..(last + 1)];
    }

    /// <summary>
    /// The rest of looped note <paramref name="o"/> from <paramref name="switchTick"/> to its end (at most
    /// <see cref="PcmFitter.MaxLoopSeconds"/>): its sample from where the note is then, at its rate and volume then and the
    /// mix's <paramref name="scale"/>, at the target's rate.
    /// </summary>
    private static (float[] Points, double RateHz) Tail(Song song, Playback playback, SongNote o, int switchTick, float scale, PcmTarget target)
    {
        double frame = o.StartFrame;
        for (int t = o.FirstTick; t < switchTick; t++)
        {
            frame += playback.Ticks[t].Channels[o.Channel].Pitch.PlaybackRateHz * playback.Ticks[t].DurationSeconds;
        }

        ChannelTick now = playback.Ticks[switchTick].Channels[o.Channel];
        double noteRate = now.Pitch.PlaybackRateHz;
        double seconds = Math.Min(PcmFitter.MaxLoopSeconds, Time(playback, EndOf(o)) - Time(playback, switchTick));
        int from = (int)Math.Round(frame);
        Sample sample = SampleProcessor.CutAfterLoop(song.Samples[o.Sample]);
        float[] data = SampleProcessor.Unroll(sample, from + (int)Math.Ceiling(seconds * noteRate) + 1);
        data = data.Length > from ? data[from..] : [];
        double rate = target == PcmTarget.Dmf ? DmfSongBuilder.DacRateHz(noteRate) : PcmTargets.PlayRateHz(target, noteRate, false);
        float[] points = data.Length == 0 ? [] : SampleProcessor.Resample(data, noteRate, rate, ResamplingMode.Smooth);
        return (SampleProcessor.ApplyGain(points, (float)now.Volume * scale), rate);
    }

    /// <summary>When tick <paramref name="t"/> starts; the song's end for the tick after its last.</summary>
    private static double Time(Playback playback, int t) => t < playback.Ticks.Count ? playback.Ticks[t].TimeSeconds : playback.DurationSeconds;

    /// <summary>A fingerprint of the bytes the target stores for <paramref name="points"/>, and their rate.</summary>
    private static Fingerprint Key(float[] points, double rate, PcmTarget target)
    {
        using Hasher key = new(Fingerprint.Of(target == PcmTarget.Dmf
            ? MemoryMarshal.AsBytes(SampleProcessor.ToSigned16(points, string.Empty, Silent).AsSpan())
            : MemoryMarshal.AsBytes(SampleProcessor.ToSigned8(points, string.Empty, Silent).AsSpan())));
        key.Add(rate);
        return key.Finish();
    }
}
