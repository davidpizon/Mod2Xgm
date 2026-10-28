using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Model;

/// <summary>Tests for the effect types of the song model (plan T3.1).</summary>
public sealed class EffectTests
{
    /// <summary>One example of every effect kind; <see cref="EveryEffectKindHasAnExample"/> keeps the list complete.</summary>
    private static readonly Effect[] Examples =
    [
        new Arpeggio(4, 7),
        new PitchSlide(16, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst),
        new TonePortamento(null, PitchScale.AmigaPeriod),
        new Vibrato(4 / 64.0, 25, PitchScale.Cents),
        new Tremolo(4 / 64.0, 0.25),
        new SetPan(-1),
        new SampleOffset(256),
        new VolumeSlide(-2 / 64.0, SlideTiming.OnceAtRowStart),
        new PositionJump(0),
        new PatternBreak(16),
        new SetLowPassFilter(false),
        new SetGlissando(true),
        new SetVibratoWaveform(Waveform.Random, Retrigger: true),
        new SetFineTune(-25),
        new PatternLoopStart(),
        new PatternLoop(3),
        new SetTremoloWaveform(Waveform.Sine, Retrigger: false),
        new Retrigger(3),
        new NoteCut(2),
        new NoteDelay(1),
        new PatternDelay(2),
        new InvertLoop(5),
        new SetSpeed(3),
        new SetTempo(140),
        new StopSong(),
        new UnsupportedEffect("E8x with x = 3"),
    ];

    /// <summary>Every concrete effect type in the model appears in <see cref="Examples"/>, once.</summary>
    [Fact]
    public void EveryEffectKindHasAnExample()
    {
        Type[] kinds = [.. typeof(Effect).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Effect)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal)];

        Assert.Equal(kinds, Examples.Select(e => e.GetType()).OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    /// <summary>Every parameter keeps the value it was given, under the name the docs give it (catches swapped parameters).</summary>
    [Fact]
    public void EveryEffectKeepsItsParameters()
    {
        bool[] matches =
        [
            Examples[0] is Arpeggio { FirstSemitones: 4, SecondSemitones: 7 },
            Examples[1] is PitchSlide { Amount: 16, Scale: PitchScale.AmigaPeriod, Timing: SlideTiming.EveryTickAfterFirst },
            Examples[2] is TonePortamento { Speed: null, Scale: PitchScale.AmigaPeriod },
            Examples[3] is Vibrato { CyclesPerTick: 4 / 64.0, Depth: 25, Scale: PitchScale.Cents },
            Examples[4] is Tremolo { CyclesPerTick: 4 / 64.0, Depth: 0.25 },
            Examples[5] is SetPan { Pan: -1 },
            Examples[6] is SampleOffset { Frame: 256 },
            Examples[7] is VolumeSlide { Amount: -2 / 64.0, Timing: SlideTiming.OnceAtRowStart },
            Examples[8] is PositionJump { Order: 0 },
            Examples[9] is PatternBreak { Row: 16 },
            Examples[10] is SetLowPassFilter { On: false },
            Examples[11] is SetGlissando { On: true },
            Examples[12] is SetVibratoWaveform { Shape: Waveform.Random, Retrigger: true },
            Examples[13] is SetFineTune { Cents: -25 },
            Examples[14] is PatternLoopStart,
            Examples[15] is PatternLoop { Count: 3 },
            Examples[16] is SetTremoloWaveform { Shape: Waveform.Sine, Retrigger: false },
            Examples[17] is Retrigger { IntervalTicks: 3 },
            Examples[18] is NoteCut { Tick: 2 },
            Examples[19] is NoteDelay { Tick: 1 },
            Examples[20] is PatternDelay { Rows: 2 },
            Examples[21] is InvertLoop { Speed: 5 },
            Examples[22] is SetSpeed { TicksPerRow: 3 },
            Examples[23] is SetTempo { Bpm: 140 },
            Examples[24] is StopSong,
            Examples[25] is UnsupportedEffect { Description: "E8x with x = 3" },
        ];

        Assert.Equal(Examples.Length, matches.Length);
        Assert.DoesNotContain(false, matches);
    }

    /// <summary>Effects compare by value, so tests and mappers can match them directly.</summary>
    [Fact]
    public void EffectsCompareByValue()
    {
        Assert.Equal(new VolumeSlide(0.5, SlideTiming.EveryTickAfterFirst), new VolumeSlide(0.5, SlideTiming.EveryTickAfterFirst));
        Assert.NotEqual<Effect>(new SetSpeed(3), new SetTempo(3));
        Assert.NotEqual(new SetVibratoWaveform(Waveform.Square, false), new SetVibratoWaveform(Waveform.RampDown, false));
        Assert.All(Examples, e => Assert.Equal(e, e with { }));
    }

    /// <summary>A cell can carry several effects, in order (one MOD command may become two).</summary>
    [Fact]
    public void ACellCanCarrySeveralEffects()
    {
        Cell cell = Cell.Empty with { Effects = [new TonePortamento(null, PitchScale.AmigaPeriod), new VolumeSlide(1 / 64.0, SlideTiming.EveryTickAfterFirst)] };

        Assert.IsType<TonePortamento>(cell.Effects[0]);
        Assert.IsType<VolumeSlide>(cell.Effects[1]);
        Assert.Null(cell.Note);
    }
}
