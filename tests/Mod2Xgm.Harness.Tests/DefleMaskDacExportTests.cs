using Mod2Xgm.Audio;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Harness.Tests;

/// <summary>
/// Checks the DMF DAC model (<see cref="DmfDac"/>, docs/adr/0058-pcm-driver-models.md) against DefleMask's own WAV
/// export of the <c>dac-sample</c> probe (plan T7.9, difference 5 of ADR 0058; docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
/// The export is made by hand in DefleMask, so the test skips until it is in the repository.
/// </summary>
public sealed class DefleMaskDacExportTests
{
    /// <summary>
    /// The bar for the first beep's log-mel distance from the model, at matched loudness. A first guess, to be set from
    /// the first export (the probe's other checks are in tests/golden/dmf-probes/README.md).
    /// </summary>
    private const double MaxMelDistanceDb = 3.0;

    /// <summary>
    /// The export's first beep (the probe's "a440 @ 32 kHz" sample, 0.4 s) sounds like the model playing the same sample
    /// on the DAC: the model's bytes, at the YM3438's rate, resampled to the export's rate.
    /// </summary>
    [Fact]
    public void TheFirstBeepSoundsLikeTheDmfDacModel()
    {
        string path = TestFiles.DefleMaskDacExport;
        if (!File.Exists(path))
        {
            Assert.Skip($"{path} is missing: export tests/golden/dmf-probes/dac-sample.dmf to WAV in DefleMask (steps in docs/adr/0066-pcm-drivers-checked-against-sgdk.md).");
            return;
        }

        AudioSignal export = WavFile.Read(path).ToMono();
        float[] samples = export.Samples.ToArray();
        int onset = Array.FindIndex(samples, v => Math.Abs(v) > 0.01f);
        Assert.True(onset >= 0, "the export is silent");
        int length = (int)(0.35 * export.SampleRate);
        float[] beep = samples[onset..Math.Min(samples.Length, onset + length)];

        // The model: the probe's sample, as DmfProbes builds it, on the DAC at 32 kHz.
        const int RateHz = 32000;
        short[] sine = [.. Enumerable.Range(0, (int)Math.Round(RateHz * 0.4)).Select(i => (short)Math.Round(16000 * Math.Sin(2 * Math.PI * 440 * i / RateHz)))];
        DmfDac dac = new(Ym3438.NtscClockHz);
        dac.SetDacMode(true);
        dac.Play(sine, RateHz);
        double ymRate = Ym3438.SampleRateHz(Ym3438.NtscClockHz);
        byte[] data = new byte[(int)(ymRate * 0.4)];
        dac.Generate(data, new byte[data.Length]);
        float[] model = SampleProcessor.Resample([.. data.Select(b => (b - 0x80) / 128f)], ymRate, export.SampleRate, ResamplingMode.Smooth);
        model = model[..Math.Min(model.Length, beep.Length)];
        beep = beep[..model.Length];

        double distance = SpectralMetrics.MelDistanceDb(new AudioSignal(Normalize(beep), export.SampleRate), new AudioSignal(Normalize(model), export.SampleRate));
        Assert.True(distance <= MaxMelDistanceDb, $"the export's first beep is {distance:0.00} dB from the model (bar {MaxMelDistanceDb} dB)");
    }

    /// <summary>The samples scaled to an RMS of 0.25, so the comparison ignores DefleMask's output level.</summary>
    private static float[] Normalize(float[] samples)
    {
        double rms = Math.Sqrt(samples.Average(v => (double)v * v));
        return rms == 0 ? samples : [.. samples.Select(v => (float)(v * 0.25 / rms))];
    }
}
