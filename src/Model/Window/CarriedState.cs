namespace Mod2Xgm.Model.Window;

/// <summary>
/// What the song's cells have set by a point in the song, followed row by row in playing order
/// (docs/adr/0116-song-window.md): each channel's instrument, effect memory, tone-portamento target and waveform settings,
/// and the song's speed and tempo. A window that starts later writes it into its first rows, so every later stage reads
/// the window like a song of its own.
/// </summary>
internal sealed class CarriedState
{
    private readonly int?[] _instrument;
    private readonly double?[] _portaSpeed;
    private readonly double?[] _vibratoSpeed;
    private readonly double?[] _vibratoDepth;
    private readonly double?[] _tremoloSpeed;
    private readonly double?[] _tremoloDepth;
    private readonly int?[] _offset;
    private readonly NotePitch?[] _portaTarget;
    private readonly SetGlissando?[] _glissando;
    private readonly SetVibratoWaveform?[] _vibratoWaveform;
    private readonly SetTremoloWaveform?[] _tremoloWaveform;

    /// <summary>The state at the song's start: nothing set, the song's own speed and tempo.</summary>
    public CarriedState(int channels, Timing timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        _instrument = new int?[channels];
        _portaSpeed = new double?[channels];
        _vibratoSpeed = new double?[channels];
        _vibratoDepth = new double?[channels];
        _tremoloSpeed = new double?[channels];
        _tremoloDepth = new double?[channels];
        _offset = new int?[channels];
        _portaTarget = new NotePitch?[channels];
        _glissando = new SetGlissando?[channels];
        _vibratoWaveform = new SetVibratoWaveform?[channels];
        _tremoloWaveform = new SetTremoloWaveform?[channels];
        Speed = timing.Speed;
        Bpm = timing.Bpm;
    }

    /// <summary>Ticks per row now.</summary>
    public int Speed { get; private set; }

    /// <summary>Tempo now.</summary>
    public double Bpm { get; private set; }

    /// <summary>The instrument channel <paramref name="channel"/> plays, or <c>null</c> before its first.</summary>
    public int? Instrument(int channel) => _instrument[channel];

    /// <summary>The note channel <paramref name="channel"/>'s tone portamento slides toward, or <c>null</c> when none was set.</summary>
    public NotePitch? PortaTarget(int channel) => _portaTarget[channel];

    /// <summary>The waveform and glissando settings channel <paramref name="channel"/> has that differ from a song's start.</summary>
    public IEnumerable<Effect> Settings(int channel)
    {
        if (_glissando[channel] is { On: true } glissando)
        {
            yield return glissando;
        }

        if (_vibratoWaveform[channel] is { } vibrato && vibrato != new SetVibratoWaveform(Waveform.Sine, true))
        {
            yield return vibrato;
        }

        if (_tremoloWaveform[channel] is { } tremolo && tremolo != new SetTremoloWaveform(Waveform.Sine, true))
        {
            yield return tremolo;
        }
    }

    /// <summary>Follows one played row; <paramref name="pitchOf"/> gives a written note's pitch as the tick player tuned it.</summary>
    public void Apply(Row row, Func<int, NotePitch?> pitchOf)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(pitchOf);
        for (int c = 0; c < row.Cells.Count; c++)
        {
            Cell cell = row.Cells[c];
            if (cell.Instrument is { } instrument)
            {
                _instrument[c] = instrument;
            }

            if (cell.Note is { } note && cell.Effects.OfType<TonePortamento>().Any())
            {
                _portaTarget[c] = pitchOf(c) ?? note;
            }

            foreach (Effect effect in cell.Effects)
            {
                Remember(c, effect);
            }
        }
    }

    /// <summary><paramref name="cell"/> with every "same as last time" parameter (<c>null</c>) filled in from memory where known.</summary>
    public Cell Resolve(Cell cell, int channel)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell with { Effects = [.. cell.Effects.Select(e => Resolve(e, channel))] };
    }

    private Effect Resolve(Effect effect, int c) => effect switch
    {
        TonePortamento { Speed: null } porta when _portaSpeed[c] is { } speed => porta with { Speed = speed },
        Vibrato vibrato => vibrato with { CyclesPerTick = vibrato.CyclesPerTick ?? _vibratoSpeed[c], Depth = vibrato.Depth ?? _vibratoDepth[c] },
        Tremolo tremolo => tremolo with { CyclesPerTick = tremolo.CyclesPerTick ?? _tremoloSpeed[c], Depth = tremolo.Depth ?? _tremoloDepth[c] },
        SampleOffset { Frame: null } offset when _offset[c] is { } frame => offset with { Frame = frame },
        _ => effect,
    };

    private void Remember(int c, Effect effect)
    {
        switch (effect)
        {
            case TonePortamento { Speed: { } speed }:
                _portaSpeed[c] = speed;
                break;
            case Vibrato vibrato:
                _vibratoSpeed[c] = vibrato.CyclesPerTick ?? _vibratoSpeed[c];
                _vibratoDepth[c] = vibrato.Depth ?? _vibratoDepth[c];
                break;
            case Tremolo tremolo:
                _tremoloSpeed[c] = tremolo.CyclesPerTick ?? _tremoloSpeed[c];
                _tremoloDepth[c] = tremolo.Depth ?? _tremoloDepth[c];
                break;
            case SampleOffset { Frame: { } frame }:
                _offset[c] = frame;
                break;
            case SetGlissando glissando:
                _glissando[c] = glissando;
                break;
            case SetVibratoWaveform waveform:
                _vibratoWaveform[c] = waveform;
                break;
            case SetTremoloWaveform waveform:
                _tremoloWaveform[c] = waveform;
                break;
            case SetSpeed speed when speed.TicksPerRow > 0:
                Speed = speed.TicksPerRow;
                break;
            case SetTempo tempo:
                Bpm = tempo.Bpm;
                break;
        }
    }
}
