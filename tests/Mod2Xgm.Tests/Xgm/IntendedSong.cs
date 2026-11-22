using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Mod2Xgm.Tests.Dmf.Sanity;
using Mod2Xgm.Xgm;
using Serilog.Core;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>What <see cref="IntendedSong.Check"/> found.</summary>
/// <param name="Differences">Every state difference that counts, frame by frame (the first few).</param>
/// <param name="DifferenceCount">How many there are in all.</param>
/// <param name="FirstSampleDifference">The first rendered sample where the file and the recording differ, or <c>null</c>.</param>
/// <param name="Samples">Rendered samples compared.</param>
internal sealed record IntendedSongCheck(IReadOnlyList<FrameDifference> Differences, int DifferenceCount, long? FirstSampleDifference, long Samples)
{
    /// <summary>True when the file plays the recording: the same state on every sounding channel, and the same sound.</summary>
    public bool Same => DifferenceCount == 0 && FirstSampleDifference is null;

    /// <summary>A short account for an assertion message.</summary>
    public override string ToString() =>
        FormattableString.Invariant($"{DifferenceCount} state differences ({string.Join("; ", Differences.Select(d => $"frame {d.Frame} {d.Key}: {d.Reference} vs {d.Candidate}"))}), first sample difference {FirstSampleDifference?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} of {Samples}");
}

/// <summary>
/// The acceptance check of docs/adr/0139-xgm2-frame-budget.md: an XGM2 file, decoded as SGDK's driver plays it, against
/// the song the player recorded (the <see cref="GenesisStream"/> the writer got). Every frame must leave the chips as the
/// recording does, with the frequency high byte read through the chip's shared latch, and the two must render to the
/// same samples. A channel's registers may differ only while the YM3438 model has it silent (the writer sends a patch
/// early there); registers the file sets and the recording never does (the loop's silent levels) don't count. Nor does
/// the last frame's PCM, and its sound, of a looping song: the writer stops there what still plays at the jump (ADR 0099).
/// </summary>
internal static class IntendedSong
{
    private const int ExamplesKept = 5;
    private const int RegKey = 0x28;

    /// <summary>Records <paramref name="mod"/> with the simple mapper on an NTSC Genesis, as a conversion to XGM2 does.</summary>
    public static GenesisStream Record(byte[] mod, MappingOptions? options = null)
    {
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(mod, notes);
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, notes);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, notes);
        GenesisPlan plan = new SimpleMapper().Map(song, playback, options ?? MappingOptions.Default, notes);
        return GenesisStream.RecordXgm2(plan, song, playback, Region.Ntsc, ResamplingMode.Smooth, notes);
    }

    /// <summary>Checks <paramref name="output"/>, written from <paramref name="stream"/>, against it.</summary>
    public static IntendedSongCheck Check(GenesisStream stream, Xgm2Output output)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ChipLog intended = Log(stream);
        ChipLog decoded = Xgm2Decoder.Decode(output.Bytes, output.Samples);
        decoded = decoded with { Frames = [.. Enumerable.Range(0, intended.Frames.Count).Select(f => f < decoded.Frames.Count ? decoded.Frames[f] : [])] };
        (List<FrameDifference> differences, int count) = CompareState(Effective(intended), Effective(decoded), Xgm2PatchMover.SilentChannels(stream), stream.LoopFrame);
        double frameSeconds = 1 / stream.FrameRate;
        StereoAudio a = SoundCheck.Render(intended, PcmDriver.Xgm2, frameSeconds);
        StereoAudio b = SoundCheck.Render(decoded, PcmDriver.Xgm2, frameSeconds);
        int frames = stream.LoopFrame is null ? intended.Frames.Count : intended.Frames.Count - 1;
        long compared = Math.Min(a.Left.Length, (long)Math.Round(frames * frameSeconds * a.SampleRate));
        return new IntendedSongCheck(differences, count, FirstDifference(a, b, compared), compared);
    }

    /// <summary>
    /// <paramref name="stream"/> as a chip log: its FM and PSG writes and its PCM plays and stops, frame by frame. A play
    /// names the first song sample whose recording has the same bytes, as the file does: the writer stores those once.
    /// </summary>
    public static ChipLog Log(GenesisStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        List<(sbyte[] Data, int Sample)> stored = [];
        int Stored(ChipEvent e)
        {
            foreach ((sbyte[] data, int sample) in stored)
            {
                if (data.AsSpan().SequenceEqual(e.Data))
                {
                    return sample;
                }
            }

            stored.Add((e.Data ?? [], e.Sample));
            return e.Sample;
        }

        List<IReadOnlyList<ChipWrite>> frames = [];
        foreach (IReadOnlyList<ChipEvent> frame in stream.Frames)
        {
            frames.Add([.. frame.Select(e => e.Kind switch
            {
                ChipEventKind.FmWrite => ChipWrite.Fm(e.Part, e.Register, e.Value),
                ChipEventKind.PsgWrite => ChipWrite.Psg(e.Value),
                ChipEventKind.PcmPlay => new ChipWrite(ChipTarget.PcmPlay, Channel: e.Channel, Sample: Stored(e), RateHz: e.HalfSpeed ? stream.PcmRateHz / 2 : stream.PcmRateHz, Data: e.Data, HalfSpeed: e.HalfSpeed),
                _ => new ChipWrite(ChipTarget.PcmStop, Channel: e.Channel),
            })]);
        }

        return new ChipLog(frames, stream.LoopFrame, stream.FrameRate);
    }

    /// <summary>
    /// <paramref name="log"/> with the frequency high bytes as the chip takes them: a write to <c>$A4</c>–<c>$A6</c> (or
    /// <c>$AC</c>–<c>$AE</c>) only fills its part's shared latch, and the low byte's write gives its channel the latch's
    /// value. Each low byte then comes after the high byte it really takes, so <see cref="ChipState"/> holds what plays.
    /// </summary>
    private static ChipLog Effective(ChipLog log)
    {
        int[] latch = [-1, -1, -1, -1];
        List<IReadOnlyList<ChipWrite>> frames = [];
        foreach (IReadOnlyList<ChipWrite> frame in log.Frames)
        {
            List<ChipWrite> writes = [];
            foreach (ChipWrite w in frame)
            {
                if (w.Target == ChipTarget.Fm && w.Register is (>= 0xA4 and <= 0xA6) or (>= 0xAC and <= 0xAE))
                {
                    latch[(w.Part * 2) + (w.Register >= 0xAC ? 1 : 0)] = w.Value & 0x3F;
                    continue;
                }

                if (w.Target == ChipTarget.Fm && w.Register is (>= 0xA0 and <= 0xA2) or (>= 0xA8 and <= 0xAA))
                {
                    int value = latch[(w.Part * 2) + (w.Register >= 0xA8 ? 1 : 0)];
                    if (value >= 0)
                    {
                        writes.Add(ChipWrite.Fm(w.Part, w.Register + 4, value));
                    }
                }

                writes.Add(w);
            }

            frames.Add(writes);
        }

        return log with { Frames = frames };
    }

    /// <summary>
    /// Every key the intended log has set that the decoded one holds differently at a frame's end, except a silent
    /// channel's registers (<paramref name="silent"/>: channels silent at each frame's start).
    /// </summary>
    private static (List<FrameDifference> Differences, int Count) CompareState(ChipLog intended, ChipLog decoded, bool[][] silent, int? loop)
    {
        ChipState a = new();
        ChipState b = new();
        List<ChipChange> changes = [];
        List<FrameDifference> differences = [];
        int count = 0;
        for (int f = 0; f < intended.Frames.Count; f++)
        {
            foreach (ChipWrite w in intended.Frames[f])
            {
                a.Apply(w, changes);
            }

            foreach (ChipWrite w in decoded.Frames[f])
            {
                b.Apply(w, changes);
            }

            changes.Clear();
            int next = f + 1 < silent.Length ? f + 1 : loop ?? f;
            bool jump = loop is not null && f == intended.Frames.Count - 1;
            foreach ((string key, int value) in a.Values)
            {
                int other = b.Values.GetValueOrDefault(key, -1);
                if (other == value || (Channel(key) is { } channel && silent[next][channel]) || (jump && key.StartsWith("PCM", StringComparison.Ordinal)))
                {
                    continue;
                }

                count++;
                if (differences.Count < ExamplesKept)
                {
                    differences.Add(new FrameDifference(f, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), other.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        return (differences, count);
    }

    /// <summary>The FM channel (part × 3 + channel) a register key such as <c>FM1:B4</c> belongs to, or <c>null</c> for any other key.</summary>
    private static int? Channel(string key)
    {
        if (!key.StartsWith("FM", StringComparison.Ordinal))
        {
            return null;
        }

        int part = key[2] - '0';
        int register = Convert.ToInt32(key[4..], 16);
        return register == RegKey ? null : (part * 3) + (register is >= 0xA8 and <= 0xAE ? 2 : register & 0x03);
    }

    /// <summary>The first of the first <paramref name="count"/> samples where <paramref name="a"/> and <paramref name="b"/> differ, on either side, or <c>null</c>.</summary>
    private static long? FirstDifference(StereoAudio a, StereoAudio b, long count)
    {
        long length = Math.Min(count, Math.Min(a.Left.Length, b.Left.Length));
        for (long i = 0; i < length; i++)
        {
            if (a.Left[i] != b.Left[i] || a.Right[i] != b.Right[i])
            {
                return i;
            }
        }

        return length == count ? null : length;
    }
}
