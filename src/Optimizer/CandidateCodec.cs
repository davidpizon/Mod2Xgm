using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Writes and reads the optimizer's candidates for <see cref="CandidateCache"/> (plan T8.6,
/// docs/adr/0074-candidate-cache.md): every field, little-endian, doubles by their bits, so a candidate read back is the
/// one written. A read that finds anything out of place throws <see cref="InvalidDataException"/>.
/// </summary>
internal static class CandidateCodec
{
    /// <summary>Most items any list may claim, so a damaged count fails fast instead of allocating.</summary>
    private const int MaxCount = 1 << 24;

    /// <summary>Writes <paramref name="fit"/>; without <paramref name="times"/>, leaves out how long it took (for keys).</summary>
    public static void Write(BinaryWriter writer, FmRegionFit fit, bool times = true)
    {
        // Best and NoLfo are usually one of the other three: a table keeps them one object when read back.
        List<FmFit> table = [];
        int Index(FmFit? f)
        {
            if (f is null)
            {
                return -1;
            }

            int i = table.FindIndex(t => ReferenceEquals(t, f));
            if (i < 0)
            {
                table.Add(f);
                i = table.Count - 1;
            }

            return i;
        }

        int[] refs = [Index(fit.Library), Index(fit.Exhaustive), Index(fit.Genetic), Index(fit.Best), Index(fit.NoLfo)];
        writer.Write(fit.Region);
        writer.Write(table.Count);
        foreach (FmFit f in table)
        {
            Write(writer, f);
        }

        foreach (int r in refs)
        {
            writer.Write(r);
        }

        writer.Write(fit.Renders);
        writer.Write(times ? fit.Elapsed.Ticks : 0);
    }

    public static FmRegionFit ReadFmRegionFit(BinaryReader reader)
    {
        int region = reader.ReadInt32();
        FmFit[] table = ReadArray(reader, ReadFmFit);
        FmFit? Ref(bool optional)
        {
            int i = reader.ReadInt32();
            if (i == -1 && optional)
            {
                return null;
            }

            return i >= 0 && i < table.Length ? table[i] : throw Bad("FM fit index");
        }

        FmFit library = Ref(false)!;
        FmFit? exhaustive = Ref(true);
        FmFit? genetic = Ref(true);
        FmFit best = Ref(false)!;
        FmFit noLfo = Ref(false)!;
        return new FmRegionFit(region, library, exhaustive, genetic, best, noLfo, reader.ReadInt32(), ReadTime(reader));
    }

    public static void Write(BinaryWriter writer, PsgSampleFit fit)
    {
        writer.Write(fit.Sample);
        writer.Write(fit.Name);
        writer.Write(fit.MostAtOnce);
        WriteList(writer, fit.Regions, r =>
        {
            writer.Write(r.Region);
            WriteList(writer, r.Candidates, c =>
            {
                writer.Write((int)c.Setting.Sound);
                writer.Write(c.Setting.NoiseRate);
                writer.Write(c.Setting.Period);
                WriteList(writer, c.Setting.Volumes, writer.Write);
                Write(writer, c.Score);
                WriteNullable(writer, c.CentsOff);
                writer.Write((int)c.Room);
            });
            writer.Write(r.Renders);
            writer.Write(r.Elapsed.Ticks);
        });
    }

    public static PsgSampleFit ReadPsgSampleFit(BinaryReader reader) => new(
        reader.ReadInt32(),
        reader.ReadString(),
        reader.ReadInt32(),
        ReadArray(reader, r => new PsgRegionFit(
            r.ReadInt32(),
            ReadArray(r, c => new PsgCandidate(
                new PsgSetting(ReadEnum<PsgSound>(c), c.ReadInt32(), c.ReadInt32(), ReadArray(c, v => v.ReadInt32())),
                ReadNoteScore(c),
                ReadNullableDouble(c),
                ReadEnum<PsgRoom>(c))),
            r.ReadInt32(),
            ReadTime(r))));

    public static void Write(BinaryWriter writer, PcmSampleFit fit)
    {
        writer.Write(fit.Sample);
        writer.Write(fit.Name);
        writer.Write((int)fit.Target);
        writer.Write(fit.MostAtOnce);
        WriteList(writer, fit.Options, o =>
        {
            writer.Write((int)o.Savings);
            WriteList(writer, o.Pitches, p =>
            {
                writer.Write(p.RateHz);
                writer.Write(p.Notes);
                writer.Write(p.Copy.SourceRateHz);
                writer.Write(p.Copy.PlayRateHz);
                writer.Write(p.Copy.HalfSpeed);
                writer.Write(p.Copy.SourceFrames);
                writer.Write(p.Copy.Trimmed);
                writer.Write(p.Copy.Points);
                writer.Write(p.Copy.Bytes);
                Write(writer, p.Score);
                writer.Write(p.CentsOff);
            });
            writer.Write((int)o.Room);
        });
        WriteList(writer, fit.Baked, b =>
        {
            writer.Write(b.Notes);
            writer.Write(b.StartFrame);
            writer.Write(b.Seconds);
            writer.Write(b.Bytes);
            Write(writer, b.Score);
            Write(writer, b.PlainScore);
        });
        writer.Write(fit.BentNotes);
        writer.Write(fit.Renders);
        writer.Write(fit.Elapsed.Ticks);
    }

    public static PcmSampleFit ReadPcmSampleFit(BinaryReader reader) => new(
        reader.ReadInt32(),
        reader.ReadString(),
        ReadEnum<PcmTarget>(reader),
        reader.ReadInt32(),
        ReadArray(reader, o => new PcmOption(
            ReadSavings(o),
            ReadArray(o, p => new PcmPitch(
                p.ReadDouble(),
                p.ReadInt32(),
                new PcmCopy(p.ReadDouble(), p.ReadDouble(), p.ReadBoolean(), p.ReadInt32(), p.ReadBoolean(), p.ReadInt32(), p.ReadInt32()),
                ReadNoteScore(p),
                p.ReadDouble())),
            ReadEnum<PcmRoom>(o))),
        ReadArray(reader, b => new PcmBaked(b.ReadInt32(), b.ReadInt32(), b.ReadDouble(), b.ReadInt32(), ReadNoteScore(b), ReadNoteScore(b))),
        reader.ReadInt32(),
        reader.ReadInt32(),
        ReadTime(reader));

    public static void Write(BinaryWriter writer, PreMixSongFit fit)
    {
        writer.Write((int)fit.Target);
        writer.Write(fit.Voices);
        writer.Write(fit.PcmNotes);
        writer.Write(fit.Overlaps);
        writer.Write(fit.Steals);
        writer.Write(fit.StealsWithPreMix);
        WriteList(writer, fit.Candidates, c =>
        {
            WriteList(writer, c.Parts, p =>
            {
                writer.Write(p.Sample);
                writer.Write(p.OffsetTicks);
                writer.Write(p.RateHz);
                writer.Write(p.StartFrame);
                writer.Write(p.Volume);
            });
            writer.Write(c.Occurrences);
            writer.Write(c.SavesSteal);
            writer.Write(c.WindowSeconds);
            writer.Write(c.SwitchSeconds);
            writer.Write((int)c.End);
            writer.Write(c.HeadBytes);
            writer.Write(c.Tail is not null);
            if (c.Tail is { } t)
            {
                writer.Write(t.Id);
                writer.Write(t.Sample);
                writer.Write(t.StartFrame);
                writer.Write(t.RateHz);
                writer.Write(t.Points);
                writer.Write(t.Bytes);
            }

            writer.Write(c.ClipDb);
            Write(writer, c.Score);
            writer.Write(c.Seam is not null);
            if (c.Seam is { } seam)
            {
                Write(writer, seam);
            }
        });
        writer.Write(fit.Renders);
        writer.Write(fit.Elapsed.Ticks);
    }

    public static PreMixSongFit ReadPreMixSongFit(BinaryReader reader) => new(
        ReadEnum<PcmTarget>(reader),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        ReadArray(reader, c => new PreMixCandidate(
            ReadArray(c, p => new PreMixPart(p.ReadInt32(), p.ReadInt32(), p.ReadDouble(), p.ReadInt32(), p.ReadDouble())),
            c.ReadInt32(),
            c.ReadBoolean(),
            c.ReadDouble(),
            c.ReadDouble(),
            ReadEnum<PreMixEnd>(c),
            c.ReadInt32(),
            c.ReadBoolean() ? new PreMixTail(c.ReadInt32(), c.ReadInt32(), c.ReadInt32(), c.ReadDouble(), c.ReadInt32(), c.ReadInt32()) : null,
            c.ReadDouble(),
            ReadNoteScore(c),
            c.ReadBoolean() ? ReadNoteScore(c) : null)),
        reader.ReadInt32(),
        ReadTime(reader));

    public static void Write(BinaryWriter writer, Ch3SongFit fit)
    {
        WriteList(writer, fit.Pairs, p =>
        {
            writer.Write(p.SampleA);
            writer.Write(p.ChannelA);
            writer.Write(p.SampleB);
            writer.Write(p.ChannelB);
            WriteList(writer, p.Onsets, o =>
            {
                Write(writer, o.A);
                Write(writer, o.B);
            });
            writer.Write(p.NotesA);
            writer.Write(p.NotesB);
        });
        WriteList(writer, fit.Candidates, c =>
        {
            // The pair by its place in the list, so a candidate and the list share one object when read back.
            int pair = -1;
            for (int i = 0; i < fit.Pairs.Count && pair < 0; i++)
            {
                pair = ReferenceEquals(fit.Pairs[i], c.Pair) ? i : -1;
            }

            if (pair < 0)
            {
                throw new ArgumentException("A CH3 candidate's pair isn't in the fit's pairs.", nameof(fit));
            }

            writer.Write(pair);
            writer.Write(c.RegionA);
            writer.Write(c.RegionB);
            writer.Write(c.Occurrences);
            writer.Write(c.HzA);
            writer.Write(c.HzB);
            writer.Write(c.WindowSeconds);
            Write(writer, c.Patch);
            writer.Write(c.AOnOps12);
            Write(writer, c.VoiceA);
            Write(writer, c.VoiceB);
            Write(writer, c.Score);
            Write(writer, c.Algorithm4Score);
            Write(writer, c.CutDownScore);
        });
        writer.Write(fit.Renders);
        writer.Write(fit.Elapsed.Ticks);
    }

    public static Ch3SongFit ReadCh3SongFit(BinaryReader reader)
    {
        LockedPair[] pairs = ReadArray(reader, p => new LockedPair(
            p.ReadInt32(),
            p.ReadInt32(),
            p.ReadInt32(),
            p.ReadInt32(),
            ReadArray(p, o => new LockedOnset(ReadSongNote(o), ReadSongNote(o))),
            p.ReadInt32(),
            p.ReadInt32()));
        Ch3Candidate[] candidates = ReadArray(reader, c =>
        {
            int pair = c.ReadInt32();
            return new Ch3Candidate(
                pair >= 0 && pair < pairs.Length ? pairs[pair] : throw Bad("locked pair index"),
                c.ReadInt32(),
                c.ReadInt32(),
                c.ReadInt32(),
                c.ReadDouble(),
                c.ReadDouble(),
                c.ReadDouble(),
                ReadFmPatch(c),
                c.ReadBoolean(),
                ReadCh3Voice(c),
                ReadCh3Voice(c),
                ReadNoteScore(c),
                ReadNoteScore(c),
                ReadNoteScore(c));
        });
        return new Ch3SongFit(pairs, candidates, reader.ReadInt32(), ReadTime(reader));
    }

    private static void Write(BinaryWriter writer, FmFit fit)
    {
        Write(writer, fit.Patch);
        WriteNullable(writer, fit.LfoRate);
        writer.Write((int)fit.Source);
        Write(writer, fit.Score);
    }

    private static FmFit ReadFmFit(BinaryReader reader) =>
        new(ReadFmPatch(reader), ReadNullableInt(reader), ReadEnum<FmFitSource>(reader), ReadNoteScore(reader));

    private static void Write(BinaryWriter writer, FmPatch patch)
    {
        writer.Write(patch.Name);
        writer.Write(patch.Algorithm);
        writer.Write(patch.Feedback);
        WriteList(writer, patch.Operators, o =>
        {
            writer.Write(o.Mult);
            writer.Write(o.Detune);
            writer.Write(o.Tl);
            writer.Write(o.Rs);
            writer.Write(o.Ar);
            writer.Write(o.Am);
            writer.Write(o.Dr);
            writer.Write(o.D2R);
            writer.Write(o.Sl);
            writer.Write(o.Rr);
            writer.Write(o.SsgEg);
        });
        writer.Write(patch.Fms);
        writer.Write(patch.Ams);
    }

    private static FmPatch ReadFmPatch(BinaryReader reader) => new(
        reader.ReadString(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        ReadArray(reader, o => new FmOperator(
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadBoolean(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32(),
            o.ReadInt32())),
        reader.ReadInt32(),
        reader.ReadInt32());

    private static void Write(BinaryWriter writer, Ch3Voice voice)
    {
        writer.Write((int)voice.Source);
        writer.Write(voice.OnOps12);
        Write(writer, voice.Patch);
        Write(writer, voice.Score);
    }

    private static Ch3Voice ReadCh3Voice(BinaryReader reader) =>
        new(ReadEnum<Ch3VoiceSource>(reader), reader.ReadBoolean(), ReadFmPatch(reader), ReadNoteScore(reader));

    private static void Write(BinaryWriter writer, SongNote note)
    {
        writer.Write(note.Channel);
        writer.Write(note.Sample);
        writer.Write(note.FirstTick);
        writer.Write(note.Ticks);
        writer.Write(note.RateHz);
        writer.Write(note.StartFrame);
        writer.Write(note.Volume);
        writer.Write(note.Seconds);
        writer.Write(note.Bent);
    }

    private static SongNote ReadSongNote(BinaryReader reader) => new(
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadDouble(),
        reader.ReadInt32(),
        reader.ReadDouble(),
        reader.ReadDouble(),
        reader.ReadBoolean());

    private static void Write(BinaryWriter writer, NoteScore score)
    {
        writer.Write(score.MelDb);
        writer.Write(score.MfccDistance);
        WriteNullable(writer, score.HarmonicDb);
    }

    private static NoteScore ReadNoteScore(BinaryReader reader) => new(reader.ReadDouble(), reader.ReadDouble(), ReadNullableDouble(reader));

    private static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<T> write)
    {
        writer.Write(items.Count);
        foreach (T item in items)
        {
            write(item);
        }
    }

    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > MaxCount)
        {
            throw Bad("list length");
        }

        T[] items = new T[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = read(reader);
        }

        return items;
    }

    private static void WriteNullable(BinaryWriter writer, double? value)
    {
        writer.Write(value.HasValue);
        if (value is { } v)
        {
            writer.Write(v);
        }
    }

    private static void WriteNullable(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value is { } v)
        {
            writer.Write(v);
        }
    }

    private static double? ReadNullableDouble(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadDouble() : null;

    private static int? ReadNullableInt(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;

    private static TimeSpan ReadTime(BinaryReader reader)
    {
        long ticks = reader.ReadInt64();
        return ticks >= 0 ? TimeSpan.FromTicks(ticks) : throw Bad("time");
    }

    private static T ReadEnum<T>(BinaryReader reader)
        where T : struct, Enum
    {
        int value = reader.ReadInt32();
        T e = (T)(object)value;
        return Enum.IsDefined(e) ? e : throw Bad(typeof(T).Name);
    }

    private static PcmSavings ReadSavings(BinaryReader reader)
    {
        const int all = (int)(PcmSavings.HalfSpeed | PcmSavings.Semitones | PcmSavings.ShortLoops | PcmSavings.TrimTail);
        int value = reader.ReadInt32();
        return (value & ~all) == 0 ? (PcmSavings)value : throw Bad(nameof(PcmSavings));
    }

    private static InvalidDataException Bad(string what) => new($"Damaged cache entry: bad {what}.");
}
