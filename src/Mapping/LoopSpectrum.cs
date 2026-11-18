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
