using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The Genesis channels a plan has a column for, in DMF order (docs/formats/dmf.md#2-system). FM6 doubles as the DAC:
/// a <see cref="VoiceKind.Dac"/> voice on <see cref="Fm6"/> means the channel is switched to the DAC for that note.
/// </summary>
internal enum GenesisChannel
{
    /// <summary>YM2612 channel 1.</summary>
    Fm1,

    /// <summary>YM2612 channel 2.</summary>
    Fm2,

    /// <summary>YM2612 channel 3.</summary>
    Fm3,

    /// <summary>YM2612 channel 4.</summary>
    Fm4,

    /// <summary>YM2612 channel 5.</summary>
    Fm5,

    /// <summary>YM2612 channel 6, which is also the DAC (digitized PCM).</summary>
    Fm6,

    /// <summary>SN76489 square tone 1.</summary>
    Psg1,

    /// <summary>SN76489 square tone 2.</summary>
    Psg2,

    /// <summary>SN76489 square tone 3.</summary>
    Psg3,

    /// <summary>SN76489 noise.</summary>
    Psg4,
}

/// <summary>How a note is sounded.</summary>
internal enum VoiceKind
{
    /// <summary>An FM patch (<see cref="GenesisPlan.FmPatches"/>).</summary>
    Fm,

    /// <summary>A recording on the DAC (<see cref="GenesisPlan.DacSamples"/>).</summary>
    Dac,

    /// <summary>The PSG noise channel.</summary>
    PsgNoise,
}

/// <summary>The sound a note is played with.</summary>
/// <param name="Kind">What kind of voice.</param>
/// <param name="Index">Into <see cref="GenesisPlan.FmPatches"/> or <see cref="GenesisPlan.DacSamples"/>; 0 for noise.</param>
internal readonly record struct PlanVoice(VoiceKind Kind, int Index);

/// <summary>What one Genesis channel does on one row.</summary>
/// <param name="Note">The note started on this row, or <c>null</c> for none.</param>
/// <param name="Voice">The sound of <paramref name="Note"/>; <c>null</c> when there is no note.</param>
/// <param name="Volume">Volume set on this row, 0–1, or <c>null</c> to leave it. A note carries its starting volume.</param>
/// <param name="NoteOff">True when the sound on this channel must stop (a note of the same MOD channel moved elsewhere).</param>
/// <param name="Effects">The MOD channel's effects on this row that act on this channel (not the song-wide ones).</param>
internal sealed record PlanCell(NotePitch? Note, PlanVoice? Voice, double? Volume, bool NoteOff, IReadOnlyList<Effect> Effects)
{
    /// <summary>A cell that does nothing.</summary>
    public static PlanCell Empty { get; } = new(null, null, null, false, []);
}

/// <summary>One row of the plan.</summary>
/// <param name="Cells">One per <see cref="GenesisChannel"/>, in that order.</param>
/// <param name="Flow">Song-wide effects on this row (jumps, breaks, loops, speed, tempo, stop), from any MOD channel.</param>
internal sealed record PlanRow(IReadOnlyList<PlanCell> Cells, IReadOnlyList<Effect> Flow);

/// <summary>An FM operator in the YM2612's own terms (docs/genesis-audio.md); patches list OP1–OP4 in Yamaha order.</summary>
/// <param name="Mult">Frequency multiplier, 0–15.</param>
/// <param name="Detune">Detune, −3…+3.</param>
/// <param name="Tl">Total level (attenuation), 0–127.</param>
/// <param name="Rs">Rate scaling, 0–3.</param>
/// <param name="Ar">Attack rate, 0–31.</param>
/// <param name="Am">Amplitude modulation on.</param>
/// <param name="Dr">Decay rate, 0–31.</param>
/// <param name="D2R">Second decay rate, 0–31.</param>
/// <param name="Sl">Sustain level, 0–15.</param>
/// <param name="Rr">Release rate, 0–15.</param>
internal sealed record FmOperator(int Mult, int Detune, int Tl, int Rs, int Ar, bool Am, int Dr, int D2R, int Sl, int Rr);

/// <summary>An FM patch: one YM2612 channel's sound.</summary>
/// <param name="Name">Short name.</param>
/// <param name="Algorithm">Connection of the operators, 0–7.</param>
/// <param name="Feedback">OP1 feedback, 0–7.</param>
/// <param name="Operators">OP1, OP2, OP3, OP4 in Yamaha order (the DMF file lists them OP1, OP3, OP2, OP4).</param>
internal sealed record FmPatch(string Name, int Algorithm, int Feedback, IReadOnlyList<FmOperator> Operators);

/// <summary>A song sample played on the DAC.</summary>
/// <param name="Sample">Index into <see cref="Song.Samples"/>.</param>
/// <param name="PlaybackRateHz">The rate it plays at: the rate of the pitch the song plays it at most often. The DAC can't change pitch per note.</param>
internal sealed record DacSample(int Sample, double PlaybackRateHz);

/// <summary>
/// What a mapper decides (docs/architecture.md): for every order of the song, which Genesis channel plays each note and
/// with which sound. Patterns are per <b>order</b>, not per song pattern, because what a note sounds like depends on the
/// instrument and volume the channel carried in. Effects are passed through untranslated (plan T5.5).
/// </summary>
/// <param name="Title">Song title.</param>
/// <param name="Patterns">One pattern per entry of <see cref="Song.Orders"/>, in playing order.</param>
/// <param name="LoopOrder">Where the song continues after its end, or <c>null</c>.</param>
/// <param name="Timing">Speed and tempo when the song starts.</param>
/// <param name="FmPatches">The FM patches the notes use, numbered from 0.</param>
/// <param name="DacSamples">The song samples played on the DAC, numbered from 0.</param>
internal sealed record GenesisPlan(
    string Title,
    IReadOnlyList<IReadOnlyList<PlanRow>> Patterns,
    int? LoopOrder,
    Timing Timing,
    IReadOnlyList<FmPatch> FmPatches,
    IReadOnlyList<DacSample> DacSamples)
{
    /// <summary>Number of columns in every row.</summary>
    public const int ChannelCount = 10;
}
