using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Optimizer;

/// <summary>What a PSG try plays (docs/adr/0070-psg-candidates.md).</summary>
internal enum PsgSound
{
    /// <summary>A square wave on one of the three tone channels.</summary>
    Tone,

    /// <summary>The noise channel's hiss (white noise).</summary>
    WhiteNoise,

    /// <summary>The noise channel's buzz (periodic noise): a thin pulse at a sixteenth of the shift rate.</summary>
    PeriodicNoise,
}

/// <summary>Whether the song has room for a PSG try, judged from how many notes it sounds at once (docs/adr/0070-psg-candidates.md).</summary>
internal enum PsgRoom
{
    /// <summary>The PSG has a channel for every note of the sample the song sounds at once.</summary>
    Fits,

    /// <summary>The song sounds the sample on more channels at once than the PSG has for the try (3 tone channels, 1 noise).</summary>
    SampleTooOften,

    /// <summary>The try also borrows tone channel 3, and the song sounds so many notes at once that no Genesis channel is left over for it.</summary>
    SongTooFull,
}

/// <summary>
/// One PSG try, as register values (docs/genesis-audio.md, the SN76489's registers).
/// </summary>
/// <param name="Sound">What it plays.</param>
/// <param name="NoiseRate">For noise, the noise register's rate: 0–2 a fixed shift rate (clock / 512, / 1024, / 2048), 3 tone channel 3's; 0 for a tone.</param>
/// <param name="Period">The tone period (1–1023): of the tone, or of tone channel 3 at noise rate 3; 0 otherwise.</param>
/// <param name="Volumes">The volume register (0 the loudest … 15 off) for each 1/60 s from the note's start; the last one holds.</param>
internal sealed record PsgSetting(PsgSound Sound, int NoiseRate, int Period, IReadOnlyList<int> Volumes)
{
    /// <summary>The noise rate that follows tone channel 3.</summary>
    public const int Tone3Rate = 3;

    /// <summary>The largest tone period: 10 bits.</summary>
    public const int MaxPeriod = 1023;

    /// <summary>Shifts per cycle of periodic noise: Sega's 16-bit shift register holds a single bit that goes round.</summary>
    public const int PeriodicShifts = 16;

    /// <summary>Volume steps per second: one per NTSC video frame, as SGDK's drivers and a 60 Hz song write them (David).</summary>
    public const int StepsPerSecond = 60;

    /// <summary>True when the try uses the noise channel.</summary>
    public bool UsesNoise => Sound != PsgSound.Tone;

    /// <summary>True when the try needs a tone channel: a tone, or noise at rate 3, which borrows tone channel 3 (kept silent) for its clock.</summary>
    public bool UsesToneChannel => Sound == PsgSound.Tone || NoiseRate == Tone3Rate;

    /// <summary>The Genesis channels the try takes: 1, or 2 for noise that borrows tone channel 3.</summary>
    public int Channels => (UsesNoise ? 1 : 0) + (UsesToneChannel ? 1 : 0);

    /// <summary>The tone's frequency (or tone channel 3's) on an NTSC Genesis, or <c>null</c> without one.</summary>
    public double? ToneHz => UsesToneChannel ? ToneFrequency(Period) : null;

    /// <summary>How often the noise shifts, or <c>null</c> for a tone.</summary>
    public double? ShiftHz => !UsesNoise ? null
        : NoiseRate == Tone3Rate ? ToneFrequency(Period)
        : Sn76489.NtscClockHz / (512 << NoiseRate);

    /// <summary>The pitch heard: the tone's, or periodic noise's (a sixteenth of the shift rate); <c>null</c> for white noise.</summary>
    public double? PitchHz => Sound switch
    {
        PsgSound.Tone => ToneHz,
        PsgSound.PeriodicNoise => ShiftHz / PeriodicShifts,
        _ => null,
    };

    /// <summary>A tone period's frequency on an NTSC Genesis: the counter runs at clock / 16 and flips the output every <paramref name="period"/> counts.</summary>
    public static double ToneFrequency(int period) => Sn76489.NtscClockHz / (32.0 * Math.Max(1, period));

    /// <summary>The tone period nearest <paramref name="hz"/>, unclamped (above <see cref="MaxPeriod"/> for a note too low for a tone channel).</summary>
    public static int NearestPeriod(double hz) => Math.Max(1, (int)Math.Round(Sn76489.NtscClockHz / (32.0 * hz)));
}
