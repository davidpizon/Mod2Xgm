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

    /// <summary>The second PCM voice of XGM and XGM2 (their first plays on <see cref="Fm6"/>); never in a DMF plan.</summary>
    Pcm2,

    /// <summary>The third PCM voice of XGM and XGM2.</summary>
    Pcm3,

    /// <summary>The fourth PCM voice of XGM.</summary>
    Pcm4,
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

    /// <summary>A PSG square wave at the note's pitch (<see cref="GenesisChannel.Psg1"/>–<see cref="GenesisChannel.Psg3"/>).</summary>
    PsgTone,
}

/// <summary>The sound a note is played with.</summary>
/// <param name="Kind">What kind of voice.</param>
/// <param name="Index">Into <see cref="GenesisPlan.FmPatches"/> or <see cref="GenesisPlan.DacSamples"/>; 0 for the PSG.</param>
internal readonly record struct PlanVoice(VoiceKind Kind, int Index);

/// <summary>What one Genesis channel does on one row.</summary>
/// <param name="Note">The note started on this row, at the pitch the tick player plays it (finetune included, ADR 0061), or <c>null</c> for none.</param>
/// <param name="Voice">The sound of <paramref name="Note"/>; <c>null</c> when there is no note.</param>
/// <param name="Volume">Volume set on this row, 0–1, or <c>null</c> to leave it. A note carries its starting volume.</param>
/// <param name="NoteOff">True when the sound on this channel must stop (a note of the same MOD channel moved elsewhere).</param>
/// <param name="Effects">The MOD channel's effects on this row that act on this channel (not the song-wide ones).</param>
/// <param name="Source">The song channel the note, volume or effects came from, or <c>null</c> when the cell is empty; writers pan by it (<see cref="Channel.Pan"/>).</param>
/// <param name="Sample">The song sample <paramref name="Note"/> plays (<see cref="Song.Samples"/>), or <c>null</c> when there is no note; writers measure its pitch and noise colour.</param>
internal sealed record PlanCell(NotePitch? Note, PlanVoice? Voice, double? Volume, bool NoteOff, IReadOnlyList<Effect> Effects, int? Source = null, int? Sample = null)
{
    /// <summary>A cell that does nothing.</summary>
    public static PlanCell Empty { get; } = new(null, null, null, false, []);

    /// <summary>The factor on the level <see cref="Note"/>'s voice would otherwise play at: below 1 for a voice sharing a MOD note with another (docs/adr/0088-layering.md, docs/adr/0089-fm-layer.md).</summary>
    public double Level { get; init; } = 1;

    /// <summary>How far <see cref="Note"/>'s voice plays from the note's pitch, in cents, so that two voices of one MOD note beat as the sample's own layers do (docs/adr/0089-fm-layer.md). Only FM takes it.</summary>
    public double Cents { get; init; }
}

/// <summary>One row of the plan.</summary>
/// <param name="Cells">In <see cref="GenesisChannel"/> order: <see cref="GenesisPlan.ChannelCount"/> of them, or <see cref="GenesisPlan.ColumnCount"/> in a plan with several PCM voices.</param>
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
/// <param name="SsgEg">SSG-EG, register <c>$90</c>'s low 4 bits: 0 = off, 8–15 = on with shape 0–7 (docs/formats/dmf.md#6-instruments).</param>
internal sealed record FmOperator(int Mult, int Detune, int Tl, int Rs, int Ar, bool Am, int Dr, int D2R, int Sl, int Rr, int SsgEg = 0);

/// <summary>An FM patch: one YM2612 channel's sound.</summary>
/// <param name="Name">Short name.</param>
/// <param name="Algorithm">Connection of the operators, 0–7.</param>
/// <param name="Feedback">OP1 feedback, 0–7.</param>
/// <param name="Operators">OP1, OP2, OP3, OP4 in Yamaha order (the DMF file lists them OP1, OP3, OP2, OP4).</param>
/// <param name="Fms">LFO vibrato depth (frequency modulation sensitivity), 0–7; 0 = none.</param>
/// <param name="Ams">LFO tremolo depth (amplitude modulation sensitivity) for operators with <see cref="FmOperator.Am"/>, 0–3; 0 = none.</param>
/// <param name="HeardRms">The level a render of the patch measured at full volume, on <see cref="VoiceLevel"/>'s scale, or <c>null</c> to work it out from the registers.</param>
internal sealed record FmPatch(string Name, int Algorithm, int Feedback, IReadOnlyList<FmOperator> Operators, int Fms = 0, int Ams = 0, double? HeardRms = null)
{
    /// <summary>True when the patch needs the chip's LFO: some vibrato, or some tremolo on an operator that takes it.</summary>
    public bool UsesLfo => Fms > 0 || (Ams > 0 && Operators.Any(o => o.Am));
}

/// <summary>
/// Where the DAC plays when recordings from both sides sound at once: XGM and XGM2 mix every PCM voice onto the one DAC,
/// which has one pan (FM6's), so they can't all stay on their side (docs/adr/0091-shrinking-recordings.md).
/// </summary>
internal enum DacPanClash
{
    /// <summary>The DAC takes the pan of the first PCM voice's note, and the other voices follow it.</summary>
    FirstVoice,

    /// <summary>While the sides clash, the DAC plays on both; otherwise on the side its recordings share.</summary>
    Centre,

    /// <summary>While the sides clash, the DAC plays on the loudest recording's side; otherwise on the side they share.</summary>
    Louder,
}

/// <summary>A song sample played on the DAC.</summary>
/// <param name="Sample">Index into <see cref="Song.Samples"/>.</param>
/// <param name="PlaybackRateHz">The rate it plays at. The DAC can't change pitch per note, so the simple mapper picks the pitch the song plays it at most often; the optimizer gives each pitch a <see cref="DacSample"/> of its own.</param>
internal sealed record DacSample(int Sample, double PlaybackRateHz)
{
    /// <summary>True when XGM2 stores and plays it at half speed, 6.65 kHz (docs/adr/0091-shrinking-recordings.md).</summary>
    public bool HalfSpeed { get; init; }

    /// <summary>
    /// How many 3 dB loudness steps one stored copy covers on XGM and XGM2: a note plays at its copy's loudness. 0 or 1
    /// plays every note at its own loudness (docs/adr/0091-shrinking-recordings.md).
    /// </summary>
    public int LoudnessShareSteps { get; init; }

    /// <summary>The gain its notes take, in dB: a look-alike sample merged into <see cref="Sample"/> plays at its own level (docs/adr/0091-shrinking-recordings.md).</summary>
    public double GainDb { get; init; }

    /// <summary>
    /// The sound itself when it isn't <see cref="Sample"/> played out: notes mixed into one recording, or the rest of one
    /// after such a mix (docs/adr/0094-pre-mixed-voice-steals.md). Its note plays it from the start at full volume.
    /// </summary>
    public DacMix? Mix { get; init; }
}

/// <summary>
/// A recording the mapper made from several notes (a pre-mix) or the rest of one (its tail): already at
/// <see cref="DacSample.PlaybackRateHz"/> and at the loudness it plays at, quantized only when written
/// (docs/adr/0094-pre-mixed-voice-steals.md).
/// </summary>
/// <param name="Points">The sound, −1…1, at the <see cref="DacSample"/>'s rate.</param>
/// <param name="Name">What the output's sample table calls it, e.g. <c>mix 01+05</c>.</param>
/// <param name="Centred">True when its notes come from both sides, so it plays from both speakers (docs/adr/0091-shrinking-recordings.md).</param>
internal sealed record DacMix(float[] Points, string Name, bool Centred);

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
    /// <summary>The Genesis's own channels: the columns of a plan with one PCM voice, which is all a DMF has.</summary>
    public const int ChannelCount = 10;

    /// <summary>The columns of a plan with several PCM voices: the channels, then <see cref="GenesisChannel.Pcm2"/>–<see cref="GenesisChannel.Pcm4"/> (docs/adr/0087-greedy-allocator.md).</summary>
    public const int ColumnCount = 13;

    /// <summary>
    /// The song samples the mapper plays as recordings, even if a clash moved all of their notes to another voice: what the
    /// capacity check counts (docs/adr/0086-capacity-check.md).
    /// </summary>
    public IReadOnlySet<int> PcmSamples { get; init; } = new HashSet<int>();

    /// <summary>
    /// True for the levels and pans David chose by ear for the optimizer: quieter noise and centred bass
    /// (<see cref="PlanVoicing"/>, docs/adr/0087-greedy-allocator.md). The simple mapper stays as graded in ADR 0049.
    /// </summary>
    public bool TunedByEar { get; init; }

    /// <summary>Where the DAC plays when recordings from both sides sound at once (XGM and XGM2; a DMF plays one recording at a time).</summary>
    public DacPanClash DacPanClash { get; init; }
}
