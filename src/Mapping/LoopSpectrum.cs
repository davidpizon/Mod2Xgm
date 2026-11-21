using Mod2Xgm.Model;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The power spectrum of a sample's loop as it repeats (docs/adr/0125-chord-samples.md): the loop tiled to
/// <see cref="Size"/> frames, without its DC offset, Hann-windowed and transformed. A loop that holds a chord, or a tone
/// chorused so that it never repeats exactly, has partials that aren't whole multiples of one short cycle, so neither a
/// cycle search nor one DFT bin per harmonic finds them; the power in a narrow band around each partial does.
/// Frequencies are in cycles per frame, so they hold at every playback rate.
/// </summary>
internal sealed class LoopSpectrum
{
    /// <summary>Frames transformed: the loop repeated as often as it takes. At ProTracker's C-2 rate a bin is 0.13 Hz.</summary>
    private const int Size = 1 << 16;

    /// <summary>Half the width of a Hann window's main lobe, in bins: a band is never narrower, so a partial's whole lobe counts.</summary>
    private const int LobeBins = 2;

    /// <summary>Steps of the search for a chord's bottom note, in cents.</summary>
    private const double StepCents = 2;

    /// <summary>Partials closer than this, in cents, are one: the strongest is kept (a semitone is 100).</summary>
    private const double MergeCents = 80;

    /// <summary>How far either side of a partial, in cents, the quietest band it must stand above is looked for.</summary>
    private const double ValleyCents = 200;

    /// <summary>How far, in dB, a partial must stand above the quietest band near it.</summary>
    private const double PeakDb = 10;

    /// <summary>Partials quieter than this under the loudest, in dB, are left out.</summary>
    private const double FloorDb = 30;

    /// <summary>The highest harmonic a partial can join a series as.</summary>
    private const int MaxHarmonic = 12;

    /// <summary>How close to a multiple of a series' root a partial must be to join it, in cents.</summary>
    private const double HarmonicCents = 30;

    /// <summary>A series whose loudest partial is within this of the loudest, in dB, is a note in its own right.</summary>
    private const double NoteDb = 12;

    private static readonly FftPlan Plan = new(Size);

    /// <summary>Power summed over bins 0 to <c>i − 1</c>, for bins 0 to <see cref="Size"/>/2.</summary>
    private readonly double[] _cumulative;

    private LoopSpectrum(double[] cumulative, double total)
    {
        _cumulative = cumulative;
        Total = total;
    }

    /// <summary>The loop's power, DC left out.</summary>
    private double Total { get; }

    /// <summary>The spectrum of <paramref name="sample"/>'s loop, or <c>null</c> when it doesn't loop or its loop is silent.</summary>
    public static LoopSpectrum? Of(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Loop is not { Length: > 1 } loop)
        {
            return null;
        }

        ReadOnlySpan<float> frames = sample.Data.AsSpan(loop.Start, loop.Length);
        double mean = 0;
        foreach (float v in frames)
        {
            mean += v;
        }

        mean /= frames.Length;
        double[] real = new double[Size];
        double[] imag = new double[Size];
        for (int i = 0; i < Size; i++)
        {
            double hann = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / Size));
            real[i] = (frames[i % frames.Length] - mean) * hann;
        }

        Plan.Forward(real, imag);
        double[] cumulative = new double[(Size / 2) + 2];
        for (int k = 0; k <= Size / 2; k++)
        {
            double power = k == 0 ? 0 : (real[k] * real[k]) + (imag[k] * imag[k]);
            cumulative[k + 1] = cumulative[k] + power;
        }

        double total = cumulative[^1];
        return total > 0 ? new LoopSpectrum(cumulative, total) : null;
    }

    /// <summary>
    /// The share (0–1) of the loop's power within <paramref name="cents"/> of <paramref name="cyclesPerFrame"/>, at least
    /// a window's main lobe either side; 0 above the Nyquist frequency.
    /// </summary>
    public double Share(double cyclesPerFrame, double cents)
    {
        (int low, int high) = Band(cyclesPerFrame, cents);
        return high < low ? 0 : (_cumulative[high + 1] - _cumulative[low]) / Total;
    }

    /// <summary>
    /// The lowest note of the loop, in cycles per frame: the lowest partial whose band of <paramref name="cents"/> holds
    /// within <paramref name="strongDb"/> of the loudest band's power, at or above <paramref name="lowestCyclesPerFrame"/>;
    /// its power-weighted centre, so a chorused partial counts once at its middle. <c>null</c> for a loop with no partial.
    /// A chord's bottom note is the note a single voice standing for it plays (docs/adr/0125-chord-samples.md).
    /// </summary>
    public double? BottomNote(double cents, double strongDb, double lowestCyclesPerFrame)
    {
        List<(double Frequency, double Share)> bands = [];
        for (double f = lowestCyclesPerFrame; f < 0.5; f *= Math.Pow(2, StepCents / 1200))
        {
            bands.Add((f, Share(f, cents)));
        }

        double loudest = bands.Count == 0 ? 0 : bands.Max(b => b.Share);
        if (loudest <= 0)
        {
            return null;
        }

        double strong = loudest * Math.Pow(10, -strongDb / 10);
        int first = bands.FindIndex(b => b.Share >= strong);
        int peak = first;
        for (int i = first; i < bands.Count && bands[i].Share >= strong; i++)
        {
            if (bands[i].Share > bands[peak].Share)
            {
                peak = i;
            }
        }

        return Centre(bands[peak].Frequency, cents);
    }

    /// <summary>
    /// The notes of the loop, read from its partials (docs/adr/0135-loop-chords.md). Partials are the strongest band of
    /// <paramref name="cents"/> within <see cref="MergeCents"/> either side, at least <see cref="PeakDb"/> above the
    /// quietest band within <see cref="ValleyCents"/> and within <see cref="FloorDb"/> of the loudest: a short loop repeated
    /// splits a partial into lines a few cents apart, and the merge keeps one. They are grouped into harmonic series: the
    /// lowest partial not yet in one and within <see cref="SamplePitch.StrongDb"/> of the loudest starts a series, and every
    /// partial within <see cref="HarmonicCents"/> of one of its first <see cref="MaxHarmonic"/> multiples joins it. A
    /// series is a note when its loudest partial is within <see cref="NoteDb"/> of the loudest and it has two partials or
    /// more. <c>null</c> for a loop with no partial.
    /// </summary>
    public LoopChord? Chord(double cents, double lowestCyclesPerFrame)
    {
        List<(double Frequency, double Share)> bands = [];
        for (double f = lowestCyclesPerFrame; f < 0.5; f *= Math.Pow(2, StepCents / 1200))
        {
            bands.Add((f, Share(f, cents)));
        }

        double loudest = bands.Count == 0 ? 0 : bands.Max(b => b.Share);
        if (loudest <= 0)
        {
            return null;
        }

        int merge = (int)(MergeCents / StepCents);
        int valley = (int)(ValleyCents / StepCents);
        List<(double Frequency, double Share)> partials = [];
        for (int i = 0; i < bands.Count; i++)
        {
            double share = bands[i].Share;
            if (share < loudest * Decibels(-FloorDb) || !IsHighest(bands, i, merge))
            {
                continue;
            }

            double quietest = bands.Skip(Math.Max(0, i - valley)).Take((2 * valley) + 1).Min(b => b.Share);
            if (share >= quietest * Decibels(PeakDb))
            {
                partials.Add((Centre(bands[i].Frequency, cents), share));
            }
        }

        double top = partials.Count == 0 ? 0 : partials.Max(p => p.Share);
        bool[] grouped = new bool[partials.Count];
        List<LoopNote> notes = [];
        int series = 0;
        while (Enumerable.Range(0, partials.Count).FirstOrDefault(i => !grouped[i] && partials[i].Share >= top * Decibels(-SamplePitch.StrongDb), -1) is var root and >= 0)
        {
            double f0 = partials[root].Frequency;
            List<double> members = [];
            for (int i = 0; i < partials.Count; i++)
            {
                double ratio = partials[i].Frequency / f0;
                int harmonic = (int)Math.Round(ratio);
                if ((i == root || !grouped[i]) && harmonic is >= 1 and <= MaxHarmonic && Math.Abs(1200 * Math.Log2(ratio / harmonic)) < HarmonicCents)
                {
                    members.Add(partials[i].Share);
                    grouped[i] = true;
                }
            }

            series++;
            if (members.Count >= 2 && members.Max() >= top * Decibels(-NoteDb))
            {
                notes.Add(new LoopNote(f0, members.Sum()));
            }
        }

        return new LoopChord(notes, notes.Sum(n => n.Share), series);
    }

    /// <summary>True when band <paramref name="i"/> is the loudest within <paramref name="reach"/> bands either side (the first of equals).</summary>
    private static bool IsHighest(List<(double Frequency, double Share)> bands, int i, int reach)
    {
        for (int j = Math.Max(0, i - reach); j <= Math.Min(bands.Count - 1, i + reach); j++)
        {
            if (j < i ? bands[j].Share >= bands[i].Share : bands[j].Share > bands[i].Share)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A power ratio of <paramref name="db"/> decibels.</summary>
    private static double Decibels(double db) => Math.Pow(10, db / 10);

    /// <summary>The bins within <paramref name="cents"/> of <paramref name="cyclesPerFrame"/>, at least <see cref="LobeBins"/> either side.</summary>
    private (int Low, int High) Band(double cyclesPerFrame, double cents)
    {
        double centre = cyclesPerFrame * Size;
        double spread = Math.Pow(2, cents / 1200);
        int low = Math.Max(1, (int)Math.Floor(Math.Min(centre / spread, centre - LobeBins)));
        int high = Math.Min(Size / 2, (int)Math.Ceiling(Math.Max(centre * spread, centre + LobeBins)));
        return (low, high);
    }

    /// <summary>The power-weighted centre, in cycles per frame, of the band of <paramref name="cents"/> around <paramref name="cyclesPerFrame"/>.</summary>
    private double Centre(double cyclesPerFrame, double cents)
    {
        (int low, int high) = Band(cyclesPerFrame, cents);
        double power = 0;
        double moment = 0;
        for (int b = low; b <= high; b++)
        {
            double p = _cumulative[b + 1] - _cumulative[b];
            power += p;
            moment += p * b;
        }

        return power > 0 ? moment / power / Size : cyclesPerFrame;
    }
}

/// <summary>A note found in a loop (docs/adr/0135-loop-chords.md).</summary>
/// <param name="CyclesPerFrame">Its root's frequency, in cycles per frame.</param>
/// <param name="Share">The share (0–1) of the loop's power its partials hold.</param>
internal readonly record struct LoopNote(double CyclesPerFrame, double Share);

/// <summary>What <see cref="LoopSpectrum.Chord"/> found in a loop (docs/adr/0135-loop-chords.md).</summary>
/// <param name="Notes">Its notes, low to high: the harmonic series strong enough to be heard in their own right.</param>
/// <param name="Share">The share (0–1) of the loop's power the notes hold together.</param>
/// <param name="Series">How many harmonic series its partials make, notes or not: a chorused or noisy sound makes many.</param>
internal sealed record LoopChord(IReadOnlyList<LoopNote> Notes, double Share, int Series)
{
    /// <summary>Fewest notes a chord is played with: three, a triad (David, 2026-11-21: triads first, not two-note fifths).</summary>
    private const int MinNotes = 3;

    /// <summary>Most notes a chord is played with, as the optimizer's chords (docs/adr/0114-fm-chords.md).</summary>
    private const int MaxNotes = 4;

    /// <summary>
    /// Most harmonic series a chord's partials make: more is a chorused, detuned or noisy sound, not separate notes.
    /// Eight lets in ai_renaissance's sample 3 (a G# C D# chord whose recording adds faint series); the corpus's noisy
    /// loops make nine or more.
    /// </summary>
    private const int MaxSeries = 8;

    /// <summary>Fewest pitch classes among the notes: a fifth and its octave (C#, G#, C#) is two, not a triad.</summary>
    private const int MinPitchClasses = 3;

    /// <summary>The least share of the loop's power its notes must hold together.</summary>
    private const double MinShare = 0.5;

    /// <summary>True when the loop is a chord to play as one: <see cref="MinNotes"/>–<see cref="MaxNotes"/> notes making most of it.</summary>
    public bool IsChord => Notes.Count is >= MinNotes and <= MaxNotes && Series <= MaxSeries && Share >= MinShare
        && PitchClasses >= MinPitchClasses;

    /// <summary>How many of the twelve pitch classes the notes fall on, each rounded to the nearest semitone.</summary>
    public int PitchClasses => Notes.Count == 0 ? 0 : Notes
        .Select(n => ((int)Math.Round(12 * Math.Log2(n.CyclesPerFrame / Notes[0].CyclesPerFrame)) % 12 + 12) % 12)
        .Distinct()
        .Count();
}
