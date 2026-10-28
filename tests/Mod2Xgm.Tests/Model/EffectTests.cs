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
        new Vibrato(4 / 64.0, 2, PitchScale.AmigaPeriod),
        new Tremolo(4 / 64.0, 0.25),
        new SetPan(-1),
        new SampleOffset(256),
        new VolumeSlide(-2 / 64.0, SlideTiming.OnceAtRowStart),
        new PositionJump(0),
        new PatternBreak(16),
        new SetLowPassFilter(false),
        new SetGlissando(true),
        new SetVibratoWaveform(Waveform.Square, Retrigger: true),
        new SetFineTune(-25),
        new PatternLoopStart(),
        new PatternLoop(3),
        new SetTremoloWaveform(Waveform.RampDown, Retrigger: false),
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

    /// <summary>Effects compare by value, so tests and mappers can match them directly.</summary>
    [Fact]
    public void EffectsCompareByValue()
    {
        Assert.Equal(new VolumeSlide(0.5, SlideTiming.EveryTickAfterFirst), new VolumeSlide(0.5, SlideTiming.EveryTickAfterFirst));
        Assert.NotEqual<Effect>(new SetSpeed(3), new SetTempo(3));
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
